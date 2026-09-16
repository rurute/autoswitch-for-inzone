using System.Text;

namespace AutoSwitch.Core.Tests;

// 旧版 INZONE-AutoSwitch.ps1 の監視ループを基準に、実際の一時ファイルで作ったエンジンと差し替えた待機で検証する。
// 待機は ScriptedWaiter が記録するだけで、実際には待たない
public sealed class MonitorLoopTests : IDisposable
{
    private const int PollSeconds = 2;
    private const int IdleSeconds = 30;
    private const string UnexpectedPrefix = "予期しない例外が発生しました: ";

    private static readonly DateTime NowUtc = new(2026, 9, 15, 6, 36, 40, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _path;
    private readonly ListLogger _logger = new();
    private readonly RecordingSwitcher _switcher = new();
    private readonly FakeClock _clock = new(NowUtc);
    private readonly FakeHubProcessProbe _hub = new() { StartTimeUtc = NowUtc.AddMinutes(-30) };
    private readonly CancellationTokenSource _cts = new();
    private AppSettings _settings;

    public MonitorLoopTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mlt-" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "ActionLog.log");
        _settings = AppSettings.Default with
        {
            BudsDevice = "BUDS",
            SpeakerDevice = "SPEAKER",
            ActionLogPath = _path,
            PollIntervalSeconds = PollSeconds,
            IdlePollIntervalSeconds = IdleSeconds,
        };
    }

    public void Dispose()
    {
        _cts.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private AutoSwitchEngine CreateEngine() => new(_settings, _logger, _switcher, _clock, _hub);

    private void Run(AutoSwitchEngine engine, ScriptedWaiter waiter) =>
        MonitorLoop.Run(engine, _settings, _logger, waiter, _cts.Token);

    private void WriteConnectEvent()
    {
        var timeStampMs = new DateTimeOffset(NowUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var line = $"{{\"logVersion\":\"1.5\",\"logId\":\"c1\",\"timeStamp\":{timeStampMs},\"actionTypeId\":23011,\"actionTypeName\":\"ConnectDevice\"}}\n";
        File.WriteAllText(_path, line, new UTF8Encoding(true));
    }

    private static TimeSpan[] Seconds(params int[] values) => values.Select(v => TimeSpan.FromSeconds(v)).ToArray();

    private string[] Errors() => _logger.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToArray();

    // ---- 待機と Tick ----

    [Fact]
    public void Run_WaitTrueThenFalse_TicksOnceAfterPollIntervalAndEnds()
    {
        WriteConnectEvent();
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
        WriteConnectEvent();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter().Returns(false);

        Run(engine, waiter);

        Assert.Equal(Seconds(PollSeconds), waiter.Durations);
        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Empty(Errors());
    }

    [Fact]
    public void Run_TokenCanceledBeforeWait_EndsWithoutWaitOrTick()
    {
        WriteConnectEvent();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter().Returns(true, true);
        _cts.Cancel();

        Run(engine, waiter);

        Assert.Empty(waiter.Durations);
        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Empty(Errors());
    }

    [Fact]
    public void Run_TokenCanceledDuringTickIteration_EndsBeforeNextWait()
    {
        WriteConnectEvent();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter().Does(() =>
        {
            _cts.Cancel();
            return true;
        }).Returns(true);

        Run(engine, waiter);

        Assert.Equal(Seconds(PollSeconds), waiter.Durations);
        Assert.Equal(HeadsetState.Connected, engine.State);
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
        WriteConnectEvent();
        _switcher.Exception = new InvalidOperationException("切り替えで例外");
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter().Returns(true, true, true, false);

        Run(engine, waiter);

        // poll 間隔で Tick（例外）→ 5 秒 → idle 間隔で Tick（新しいイベントなし）→ poll 間隔
        Assert.Equal(Seconds(PollSeconds, 5, IdleSeconds, PollSeconds), waiter.Durations);
        Assert.Equal([UnexpectedPrefix + "切り替えで例外"], Errors());
        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
    }

    [Fact]
    public void Run_FirstWaitThrows_LogsErrorWaitsFiveSecondsThenIdleAndContinues()
    {
        WriteConnectEvent();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter()
            .Throws(new InvalidOperationException("待機で例外"))
            .Returns(true, true, false);

        Run(engine, waiter);

        Assert.Equal(Seconds(PollSeconds, 5, IdleSeconds, PollSeconds), waiter.Durations);
        Assert.Equal([UnexpectedPrefix + "待機で例外"], Errors());
        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
    }

    [Fact]
    public void Run_FiveSecondWaitThrows_SwallowsAndContinuesToNextWait()
    {
        WriteConnectEvent();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter()
            .Throws(new InvalidOperationException("待機で例外"))
            .Throws(new InvalidOperationException("5秒の待機で例外"))
            .Returns(true, false);

        Run(engine, waiter);

        Assert.Equal(Seconds(PollSeconds, 5, IdleSeconds, PollSeconds), waiter.Durations);
        Assert.Equal([UnexpectedPrefix + "待機で例外"], Errors());
        Assert.Equal(HeadsetState.Connected, engine.State);
    }

    [Fact]
    public void Run_FiveSecondWaitFalse_Ends()
    {
        WriteConnectEvent();
        var engine = CreateEngine();
        var waiter = new ScriptedWaiter()
            .Throws(new InvalidOperationException("待機で例外"))
            .Returns(false, true);

        Run(engine, waiter);

        Assert.Equal(Seconds(PollSeconds, 5), waiter.Durations);
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

        Assert.Equal(Seconds(PollSeconds, 5, 1), waiter.Durations);
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

        Assert.Equal(Seconds(PollSeconds, 5), waiter.Durations);
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
