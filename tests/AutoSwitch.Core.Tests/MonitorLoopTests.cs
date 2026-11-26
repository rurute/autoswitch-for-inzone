using System.Diagnostics;

namespace AutoSwitch.Core.Tests;

// 最初に作った PowerShell 版の監視ループを基準に、偽の問い合わせ（FakeBudsProbe）で作ったエンジンと差し替えた待機で検証する。
// 待機は ScriptedWaiter が記録するだけで、実際には待たない
public sealed class MonitorLoopTests : IDisposable
{
    private const int PollSeconds = 2;
    private const int IdleSeconds = 30;
    private const string UnexpectedPrefix = "予期しない例外が発生しました: ";

    private static readonly DateTime NowUtc = new(2026, 9, 15, 6, 36, 40, DateTimeKind.Utc);

    private readonly ListLogger _logger = new();
    private readonly RecordingSwitcher _switcher = new();
    private readonly FakeClock _clock = new(NowUtc);
    private readonly FakeBudsProbe _probe = new();
    private readonly CancellationTokenSource _cts = new();
    private AppSettings _settings;

    public MonitorLoopTests()
    {
        _settings = AppSettings.Default with
        {
            BudsDevice = "BUDS",
            SpeakerDevice = "SPEAKER",
            PollIntervalSeconds = PollSeconds,
            IdlePollIntervalSeconds = IdleSeconds,
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private AutoSwitchEngine CreateEngine() => new(_settings, _logger, _switcher, _clock, _probe);

    private void Run(AutoSwitchEngine engine, ScriptedWaiter waiter) =>
        MonitorLoop.Run(engine, _settings, _logger, waiter, _cts.Token);

    // 問い合わせの結果を Connected にする（以前の「接続イベントを書く」の代わり）。
    // MonitorLoop.Run は StartupSync を呼ばないので、最初の Tick が合わせ直しとして BUDS へ切り替える
    private void ReportConnected() =>
        _probe.Results.Enqueue(new ProbeResult(ProbeStatus.Connected, new BudsLevels(100, 100, 100)));

    // 問い合わせの結果を Disconnected にする（以前の「ログが無い」の代わり。Tick の回数や間隔だけを見るテストに使う）
    private void ReportDisconnected() => _probe.Results.Enqueue(new ProbeResult(ProbeStatus.Disconnected));

    private static TimeSpan[] Seconds(params int[] values) => values.Select(v => TimeSpan.FromSeconds(v)).ToArray();

    // 呼ばれた順の待機。負の値は WaitIgnoringWake（例外のあとの 5 秒）、正の値は Wait の秒数を表す
    private static (TimeSpan Duration, bool IgnoringWake)[] Waits(params int[] values) =>
        values.Select(v => (TimeSpan.FromSeconds(Math.Abs(v)), v < 0)).ToArray();

    private string[] Errors() => _logger.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToArray();

    // ---- 待機と Tick ----

    [Fact]
    public void Run_WaitTrueThenFalse_TicksOnceAfterPollIntervalAndEnds()
    {
        ReportConnected();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter().Returns(true, false);

        Run(engine, waiter);

        Assert.Equal(Seconds(PollSeconds, PollSeconds), waiter.Durations);
        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Empty(Errors());
    }

    [Fact]
    public void Run_FirstWaitFalse_EndsWithoutTick()
    {
        ReportConnected();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter().Returns(false);

        Run(engine, waiter);

        Assert.Equal(Seconds(PollSeconds), waiter.Durations);
        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(0, _probe.CallCount);
        Assert.Empty(Errors());
    }

    [Fact]
    public void Run_TokenCanceledBeforeWait_EndsWithoutWaitOrTick()
    {
        ReportConnected();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter().Returns(true, true);
        _cts.Cancel();

        Run(engine, waiter);

        Assert.Empty(waiter.Durations);
        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(0, _probe.CallCount);
        Assert.Empty(Errors());
    }

    [Fact]
    public void Run_TokenCanceledDuringTickIteration_EndsBeforeNextWait()
    {
        ReportConnected();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter().Does(() =>
        {
            _cts.Cancel();
            return true;
        }).Returns(true);

        Run(engine, waiter);

        Assert.Equal(Seconds(PollSeconds), waiter.Durations);
        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(0, _probe.CallCount);
        Assert.Empty(Errors());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3600, 3600)]
    [InlineData(5000, 3600)]
    public void Run_DelayIsClampedBetweenOneSecondAndOneHour(int pollIntervalSeconds, int expectedSeconds)
    {
        // 範囲外の値は SettingsLoader では作れないため AppSettings を直接作る
        _settings = _settings with { PollIntervalSeconds = pollIntervalSeconds };
        var waiter = new ScriptedWaiter().Returns(false);

        Run(CreateEngine(), waiter);

        Assert.Equal(Seconds(expectedSeconds), waiter.Durations);
    }

    // ---- 想定外の例外からの復帰 ----

    [Fact]
    public void Run_SwitcherThrows_LogsErrorWaitsFiveSecondsThenIdleAndContinues()
    {
        ReportConnected();
        _switcher.Exception = new InvalidOperationException("切り替えで例外");
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter().Returns(true, true, true, false);

        Run(engine, waiter);

        // poll 間隔で Tick（例外）→ 5 秒 → idle 間隔で Tick（同じ Connected なので切り替えない）→ poll 間隔
        Assert.Equal(Waits(PollSeconds, -5, IdleSeconds, PollSeconds), waiter.AllWaits);
        Assert.Equal([UnexpectedPrefix + "切り替えで例外"], Errors());
        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
    }

    [Fact]
    public void Run_FirstWaitThrows_LogsErrorWaitsFiveSecondsThenIdleAndContinues()
    {
        ReportConnected();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter()
            .Throws(new InvalidOperationException("待機で例外"))
            .Returns(true, true, false);

        Run(engine, waiter);

        Assert.Equal(Waits(PollSeconds, -5, IdleSeconds, PollSeconds), waiter.AllWaits);
        Assert.Equal([UnexpectedPrefix + "待機で例外"], Errors());
        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
    }

    [Fact]
    public void Run_FiveSecondWaitThrows_SwallowsAndContinuesToNextWait()
    {
        ReportConnected();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter()
            .Throws(new InvalidOperationException("待機で例外"))
            .Throws(new InvalidOperationException("5秒の待機で例外"))
            .Returns(true, false);

        Run(engine, waiter);

        Assert.Equal(Waits(PollSeconds, -5, IdleSeconds, PollSeconds), waiter.AllWaits);
        Assert.Equal([UnexpectedPrefix + "待機で例外"], Errors());
        Assert.Equal(HeadsetState.Connected, engine.State);
    }

    [Fact]
    public void Run_FiveSecondWaitFalse_Ends()
    {
        ReportConnected();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter()
            .Throws(new InvalidOperationException("待機で例外"))
            .Returns(false, true);

        Run(engine, waiter);

        Assert.Equal(Waits(PollSeconds, -5), waiter.AllWaits);
        Assert.Equal([UnexpectedPrefix + "待機で例外"], Errors());
        Assert.Equal(HeadsetState.Unknown, engine.State);
    }

    [Fact]
    public void Run_IdleIntervalAfterException_IsClampedToOneSecond()
    {
        _settings = _settings with { IdlePollIntervalSeconds = 0 };
        var waiter = new ScriptedWaiter()
            .Throws(new InvalidOperationException("待機で例外"))
            .Returns(true, false);

        Run(CreateEngine(), waiter);

        Assert.Equal(Waits(PollSeconds, -5, 1), waiter.AllWaits);
    }

    [Fact]
    public void Run_RecoveryDelay_UsesWaitIgnoringWake()
    {
        // 例外のあとの 5 秒は WaitIgnoringWake で待つ（通知で打ち切らない。spec §8）
        var waiter = new ScriptedWaiter()
            .Throws(new InvalidOperationException("待機で例外"))
            .Returns(true, false);

        Run(CreateEngine(), waiter);

        Assert.Equal(Seconds(5), waiter.IgnoringWakeDurations);
        Assert.Equal(Seconds(PollSeconds, IdleSeconds), waiter.Durations);
    }

    [Fact]
    public void Run_OperationCanceledAfterCancellation_EndsWithoutError()
    {
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter().Does(() =>
        {
            _cts.Cancel();
            throw new OperationCanceledException(_cts.Token);
        }).Returns(true);

        Run(engine, waiter);

        Assert.Equal(Seconds(PollSeconds), waiter.Durations);
        Assert.Empty(Errors());
    }

    [Fact]
    public void Run_OperationCanceledWithoutCancellation_IsLoggedAsUnexpected()
    {
        var waiter = new ScriptedWaiter()
            .Throws(new OperationCanceledException("取り消し以外"))
            .Returns(false);

        Run(CreateEngine(), waiter);

        Assert.Equal(Waits(PollSeconds, -5), waiter.AllWaits);
        Assert.Equal([UnexpectedPrefix + "取り消し以外"], Errors());
    }

    [Fact]
    public void Run_NullArguments_ThrowArgumentNullException()
    {
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter();

        Assert.Throws<ArgumentNullException>(() => MonitorLoop.Run(null!, _settings, _logger, waiter, _cts.Token));
        Assert.Throws<ArgumentNullException>(() => MonitorLoop.Run(engine, null!, _logger, waiter, _cts.Token));
        Assert.Throws<ArgumentNullException>(() => MonitorLoop.Run(engine, _settings, null!, waiter, _cts.Token));
        Assert.Throws<ArgumentNullException>(() => MonitorLoop.Run(engine, _settings, _logger, null!, _cts.Token));
        Assert.Empty(waiter.Durations);
    }

    // ---- 本物の WakeableWaiter との組み合わせ（実時間を使う） ----
    // poll 間隔は 60 秒にして、時間切れの Tick が混ざらないようにする。Tick の回数と時刻は CountingClock で数える

    private const int LongPollSeconds = 60;

    private AutoSwitchEngine CreateEngine(CountingClock clock)
    {
        _settings = _settings with { PollIntervalSeconds = LongPollSeconds };
        return new AutoSwitchEngine(_settings, _logger, _switcher, clock, _probe);
    }

    // 監視ループを別スレッドで動かす
    private Thread StartRun(AutoSwitchEngine engine, IWaiter waiter)
    {
        var thread = new Thread(() => MonitorLoop.Run(engine, _settings, _logger, waiter, _cts.Token)) { IsBackground = true };
        thread.Start();
        return thread;
    }

    // 取り消して、監視ループが 5 秒以内に終わることを確かめる
    private void StopRun(Thread thread)
    {
        _cts.Cancel();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "監視ループが 5 秒以内に終わりませんでした");
    }

    [Fact]
    public void Run_WakeableWaiterWokenDuringWait_TicksBeforePollInterval()
    {
        var clock = new CountingClock();
        using var waiter = new WakeableWaiter();
        // Tick の始まり（時計を読んだとき）ではなく切り替えまで待つ。すぐ取り消すと Tick の途中で切り替えをやめるため
        using var switched = new ManualResetEventSlim();
        _switcher.OnSwitch = (_, _) => switched.Set();
        var thread = StartRun(CreateEngine(clock), waiter);
        Thread.Sleep(200);

        ReportConnected();
        waiter.Wake();
        var ticked = switched.Wait(TimeSpan.FromSeconds(2));
        StopRun(thread);

        Assert.True(ticked, "起こしてから 2 秒以内に Tick で切り替えが行われませんでした");
        Assert.Equal(1, clock.Count);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Empty(Errors());
    }

    [Fact]
    public void Run_WakeableWaiterWokenManyTimesBeforeRun_TicksOnce()
    {
        // 起こした印を、ループを始める前に 1 つの束にしておく
        ReportDisconnected();
        var clock = new CountingClock();
        using var waiter = new WakeableWaiter();
        for (var i = 0; i < 50; i++)
        {
            waiter.Wake();
        }

        var thread = StartRun(CreateEngine(clock), waiter);
        Thread.Sleep(1000);
        var count = clock.Count;
        StopRun(thread);

        Assert.Equal(1, count);
        Assert.Empty(Errors());
    }

    [Fact]
    public void Run_WakeableWaiterWokenDuringTick_TicksAgainImmediately()
    {
        ReportDisconnected();
        var clock = new CountingClock();
        using var waiter = new WakeableWaiter();
        // 1 回目の Tick の最中に起こす
        clock.OnRead = count =>
        {
            if (count == 1)
            {
                waiter.Wake();
            }
        };
        waiter.Wake();

        var thread = StartRun(CreateEngine(clock), waiter);
        var ticked = SpinWait.SpinUntil(() => clock.Count >= 2, TimeSpan.FromSeconds(2));
        StopRun(thread);

        Assert.True(ticked, $"Tick の最中に起こしてから 2 秒以内に次の Tick が行われませんでした（回数 {clock.Count}）");
        Assert.Empty(Errors());
    }

    [Fact]
    public void Run_WakeableWaiterWokenContinuously_TickSpacingIsAtLeastDefaultMinInterval()
    {
        ReportDisconnected();
        var clock = new CountingClock();
        using var waiter = new WakeableWaiter();
        var thread = StartRun(CreateEngine(clock), waiter);

        // ドングルの知らせがまとまって届くときの代わりに、1 秒間 1 ms おきに起こす
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(1))
        {
            waiter.Wake();
            Thread.Sleep(1);
        }
        StopRun(thread);

        var times = clock.ReadTimes();
        Assert.True(times.Length >= 3, $"Tick の回数が少なすぎます（{times.Length}）");
        var tolerance = TimeSpan.FromMilliseconds(10);
        for (var i = 1; i < times.Length; i++)
        {
            var spacing = times[i] - times[i - 1];
            Assert.True(spacing >= WakeableWaiter.DefaultMinInterval - tolerance, $"Tick {i} と {i + 1} の間隔が {spacing.TotalMilliseconds} ms");
        }
        Assert.Empty(Errors());
    }

    [Fact]
    public void Run_WakeableWaiterCanceledThenWoken_DoesNotTickAfterCancellation()
    {
        var clock = new CountingClock();
        using var waiter = new WakeableWaiter();
        var thread = StartRun(CreateEngine(clock), waiter);
        Thread.Sleep(200);

        _cts.Cancel();
        waiter.Wake();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "監視ループが 5 秒以内に終わりませんでした");
        Thread.Sleep(500);

        Assert.Equal(0, clock.Count);
        Assert.Empty(_switcher.Calls);
        Assert.Empty(Errors());
    }

    // ---- SystemWaiter（実際に長く待たない条件だけを確かめる） ----

    [Fact]
    public void SystemWaiter_CanceledToken_ReturnsFalseWithoutWaiting()
    {
        _cts.Cancel();

        Assert.False(new SystemWaiter().Wait(TimeSpan.FromHours(1), _cts.Token));
    }

    [Fact]
    public void SystemWaiter_ZeroDuration_ReturnsTrue()
    {
        Assert.True(new SystemWaiter().Wait(TimeSpan.Zero, _cts.Token));
    }

    [Fact]
    public void SystemWaiter_NegativeDuration_ReturnsTrueWithoutException()
    {
        Assert.True(new SystemWaiter().Wait(TimeSpan.FromSeconds(-1), _cts.Token));
    }

    [Fact]
    public void SystemWaiter_DurationBeyondWaitHandleLimit_WithCanceledToken_ReturnsFalseWithoutException()
    {
        _cts.Cancel();

        Assert.False(new SystemWaiter().Wait(TimeSpan.MaxValue, _cts.Token));
    }

    [Fact]
    public void SystemWaiter_DisposedSource_ReturnsFalseWithoutException()
    {
        var source = new CancellationTokenSource();
        var token = source.Token;
        source.Dispose();

        Assert.False(new SystemWaiter().Wait(TimeSpan.FromHours(1), token));
    }
}
