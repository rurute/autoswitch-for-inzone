namespace AutoSwitch.Core.Tests;

// ドングルへの問い合わせ（FakeBudsProbe）の結果を与え、差し替えた時計・切り替えで検証する
public sealed class AutoSwitchEngineTests
{
    private const int PollSeconds = 3;
    private const int IdleSeconds = 7;

    // 既定の時刻: 2026-09-15 06:36:40Z 固定
    private static readonly DateTime NowUtc = new(2026, 9, 15, 6, 36, 40, DateTimeKind.Utc);

    private static readonly ProbeResult Connected = new(ProbeStatus.Connected, new BudsLevels(100, 100, 100));
    private static readonly ProbeResult Disconnected = new(ProbeStatus.Disconnected);
    private static readonly ProbeResult Unavailable = new(ProbeStatus.Unavailable, Error: "not found");

    private const string RecoveredMessage = "INZONE Buds のドングルを読めるようになりました。";
    private const string CancelledMessage = "終了が要求されたため、切り替えを行いません。";
    private const string ResumeMessage = "長時間のギャップを検知しました（スリープ復帰とみなします）。監視を通常間隔へ戻します。";

    private readonly ListLogger _logger = new();
    private readonly RecordingSwitcher _switcher = new();
    private readonly FakeClock _clock = new(NowUtc);
    private readonly FakeBudsProbe _probe = new();
    private AppSettings _settings;

    public AutoSwitchEngineTests()
    {
        _settings = AppSettings.Default with
        {
            BudsDevice = "BUDS",
            SpeakerDevice = "SPEAKER",
            PollIntervalSeconds = PollSeconds,
            IdlePollIntervalSeconds = IdleSeconds,
        };
    }

    private AutoSwitchEngine CreateEngine() => new(_settings, _logger, _switcher, _clock, _probe);

    private void Results(params ProbeResult[] results)
    {
        foreach (var result in results)
        {
            _probe.Results.Enqueue(result);
        }
    }

    private static string UnavailableMessage(int idleSeconds, string error) =>
        $"INZONE Buds のドングルを読めません。{idleSeconds} 秒おきに探し直します: {error}";

    private static string ResyncMessage(HeadsetState state) => $"現在の状態を {state} と判定しました。";

    private static string ResyncWithoutSwitchMessage(HeadsetState state) =>
        $"現在の状態を {state} と判定しました。startupSyncMaxAgeMinutes が 0 のため切り替えません。";

    private int Count(LogLevel level, string message) => _logger.Entries.Count(e => e == (level, message));

    private int CountLevel(LogLevel level) => _logger.Entries.Count(e => e.Level == level);

    // 起動時に Disconnected と判定させ（スピーカーへの合わせ直しを 1 回行う）、その記録を消してから返す。
    // 以後の Tick での切り替えだけを見るテストに使う
    private AutoSwitchEngine StartedEngine()
    {
        Results(Disconnected);
        var engine = CreateEngine();
        engine.StartupSync();
        _switcher.Calls.Clear();
        _switcher.FlowCalls.Clear();
        _logger.Entries.Clear();
        return engine;
    }

    // ---- 起動時（StartupSync） ----

    [Fact]
    public void StartupSync_Connected_SwitchesToBuds()
    {
        Results(Connected);
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Equal(PollSeconds, engine.NextDelaySeconds);
        Assert.Equal(1, Count(LogLevel.Info, ResyncMessage(HeadsetState.Connected)));
        Assert.Equal(1, _probe.CallCount);
    }

    [Fact]
    public void StartupSync_Disconnected_SwitchesToSpeaker()
    {
        Results(Disconnected);
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(["SPEAKER"], _switcher.Calls);
        Assert.Equal(PollSeconds, engine.NextDelaySeconds);
        Assert.Equal(1, Count(LogLevel.Info, ResyncMessage(HeadsetState.Disconnected)));
    }

    [Fact]
    public void StartupSync_SyncDisabled_SetsStateWithoutSwitching()
    {
        _settings = _settings with { StartupSyncMaxAgeMinutes = 0 };
        Results(Connected);
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(PollSeconds, engine.NextDelaySeconds);
        Assert.Equal(1, Count(LogLevel.Info, ResyncWithoutSwitchMessage(HeadsetState.Connected)));
    }

    [Fact]
    public void StartupSync_Unavailable_StaysUnknownWithIdleDelay()
    {
        Results(Unavailable);
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(IdleSeconds, engine.NextDelaySeconds);
        Assert.Equal(1, CountLevel(LogLevel.Warn));
        Assert.Equal(1, Count(LogLevel.Warn, UnavailableMessage(IdleSeconds, "not found")));
    }

    // 起動時の合わせ直しは 1 回だけ。同じ状態が続いても次の Tick では切り替えない
    [Fact]
    public void StartupSync_ThenTickSameState_DoesNotSwitchAgain()
    {
        Results(Connected, Connected);
        var engine = CreateEngine();

        engine.StartupSync();
        engine.Tick();

        Assert.Equal(["BUDS"], _switcher.Calls);
    }

    [Fact]
    public void StartupSync_Cancelled_DoesNotSwitch()
    {
        Results(Connected);
        var engine = CreateEngine();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        engine.StartupSync(cancellation.Token);

        Assert.Empty(_switcher.Calls);
        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Equal(1, Count(LogLevel.Info, CancelledMessage));
    }

    // ---- Tick ----

    [Fact]
    public void NewEngine_StartsUnknownWithPollInterval()
    {
        var engine = CreateEngine();

        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Equal(PollSeconds, engine.NextDelaySeconds);
        Assert.Equal(0, _probe.CallCount);
    }

    [Fact]
    public void Tick_SameState_DoesNotSwitch()
    {
        var engine = StartedEngine();
        Results(Disconnected, Disconnected);

        engine.Tick();
        engine.Tick();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(2, Count(LogLevel.Debug, "状態は Disconnected のままです。切り替えません。"));
        Assert.Equal(PollSeconds, engine.NextDelaySeconds);
    }

    [Fact]
    public void Tick_StateChanged_SwitchesOnce()
    {
        var engine = StartedEngine();
        Results(Connected, Connected);

        engine.Tick();
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Equal(1, Count(LogLevel.Info, "状態遷移を検知しました: Connected"));
    }

    [Fact]
    public void Tick_ConnectThenDisconnect_SwitchesToBudsThenSpeaker()
    {
        var engine = StartedEngine();
        Results(Connected, Disconnected);

        engine.Tick();
        engine.Tick();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(["BUDS", "SPEAKER"], _switcher.Calls);
    }

    [Fact]
    public void Tick_SwitchOnConnectFalse_UpdatesStateWithoutSwitching()
    {
        _settings = _settings with { SwitchOnConnect = false };
        var engine = StartedEngine();
        Results(Connected);

        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Contains((LogLevel.Info, "switchOnConnect が false のため切り替えません。"), _logger.Entries);
    }

    [Fact]
    public void Tick_SwitchOnDisconnectFalse_UpdatesStateWithoutSwitching()
    {
        _settings = _settings with { SwitchOnDisconnect = false };
        var engine = StartedEngine();
        Results(Connected, Disconnected);

        engine.Tick();
        engine.Tick();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Contains((LogLevel.Info, "switchOnDisconnect が false のため切り替えません。"), _logger.Entries);
    }

    // ---- 読めないとき・戻ったとき ----

    [Fact]
    public void Tick_Unavailable_KeepsStateAndWarnsOnce()
    {
        Results(Connected, Unavailable, Unavailable, Unavailable);
        var engine = CreateEngine();
        engine.StartupSync();

        engine.Tick();
        engine.Tick();
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Equal(1, CountLevel(LogLevel.Warn));
        Assert.Equal(1, Count(LogLevel.Warn, UnavailableMessage(IdleSeconds, "not found")));
        Assert.Equal(IdleSeconds, engine.NextDelaySeconds);
    }

    [Fact]
    public void Tick_RecoveredWithSameState_SwitchesAgain()
    {
        Results(Connected, Unavailable, Connected);
        var engine = CreateEngine();
        engine.StartupSync();
        engine.Tick();

        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS", "BUDS"], _switcher.Calls);
        Assert.Equal(1, Count(LogLevel.Info, RecoveredMessage));
        Assert.Equal(2, Count(LogLevel.Info, ResyncMessage(HeadsetState.Connected)));
        Assert.Equal(PollSeconds, engine.NextDelaySeconds);
    }

    // 合わせ直しは戻ったときの 1 回だけ。その後は状態の変化で切り替える
    [Fact]
    public void Tick_RecoveredThenSameState_DoesNotSwitchAgain()
    {
        Results(Connected, Unavailable, Connected, Connected);
        var engine = CreateEngine();
        engine.StartupSync();
        engine.Tick();
        engine.Tick();

        engine.Tick();

        Assert.Equal(["BUDS", "BUDS"], _switcher.Calls);
    }

    // 読めなくなるたびに Warn を 1 行、戻るたびに Info を 1 行
    [Fact]
    public void Tick_UnavailableTwice_WarnsOncePerLoss()
    {
        Results(Connected, Unavailable, Unavailable, Connected, Unavailable, Unavailable);
        var engine = CreateEngine();
        engine.StartupSync();

        for (var i = 0; i < 5; i++)
        {
            engine.Tick();
        }

        Assert.Equal(2, Count(LogLevel.Warn, UnavailableMessage(IdleSeconds, "not found")));
        Assert.Equal(1, Count(LogLevel.Info, RecoveredMessage));
    }

    // 戻ったときも startupSyncMaxAgeMinutes が 0 なら判定だけ（状態が変わっていても切り替えない）
    [Fact]
    public void Tick_RecoveredWithSyncDisabled_DoesNotSwitch()
    {
        _settings = _settings with { StartupSyncMaxAgeMinutes = 0 };
        Results(Connected, Unavailable, Disconnected);
        var engine = CreateEngine();
        engine.StartupSync();
        engine.Tick();

        engine.Tick();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(1, Count(LogLevel.Info, RecoveredMessage));
        Assert.Equal(1, Count(LogLevel.Info, ResyncWithoutSwitchMessage(HeadsetState.Disconnected)));
    }

    [Fact]
    public void Tick_FoundAfterUnavailableStartup_SwitchesOnce()
    {
        Results(Unavailable, Disconnected, Disconnected);
        var engine = CreateEngine();
        engine.StartupSync();

        engine.Tick();
        engine.Tick();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(["SPEAKER"], _switcher.Calls);
        Assert.Equal(1, Count(LogLevel.Info, RecoveredMessage));
        Assert.Equal(1, Count(LogLevel.Info, ResyncMessage(HeadsetState.Disconnected)));
        Assert.Equal(PollSeconds, engine.NextDelaySeconds);
    }

    // 切り替えないのは合わせ直しのときだけ。その後の状態の変化では切り替える
    [Fact]
    public void Tick_FoundAfterUnavailableStartup_SyncDisabled_DoesNotSwitch()
    {
        _settings = _settings with { StartupSyncMaxAgeMinutes = 0 };
        Results(Unavailable, Disconnected, Connected);
        var engine = CreateEngine();
        engine.StartupSync();

        engine.Tick();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(1, Count(LogLevel.Info, ResyncWithoutSwitchMessage(HeadsetState.Disconnected)));

        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
    }

    // ---- 問い合わせの前後のイベント ----

    [Theory]
    [InlineData(ProbeStatus.Connected)]
    [InlineData(ProbeStatus.Disconnected)]
    [InlineData(ProbeStatus.Unavailable)]
    public void Tick_RaisesQueryStartingThenQueryCompleted(ProbeStatus status)
    {
        var engine = StartedEngine();
        var order = new List<string>();
        engine.QueryStarting += () => order.Add("starting");
        engine.QueryCompleted += s => order.Add($"completed:{s}");
        _probe.OnQuery = _ => order.Add("query");
        Results(new ProbeResult(status));

        engine.Tick();

        Assert.Equal(["starting", "query", $"completed:{status}"], order);
    }

    [Fact]
    public void StartupSync_RaisesQueryStartingThenQueryCompleted()
    {
        Results(Connected);
        var engine = CreateEngine();
        var order = new List<string>();
        engine.QueryStarting += () => order.Add("starting");
        engine.QueryCompleted += s => order.Add($"completed:{s}");
        _probe.OnQuery = _ => order.Add("query");

        engine.StartupSync();

        Assert.Equal(["starting", "query", "completed:Connected"], order);
    }

    [Fact]
    public void Tick_QueryEventHandlersThrow_KeepsMonitoring()
    {
        var engine = StartedEngine();
        engine.QueryStarting += () => throw new InvalidOperationException("starting boom");
        engine.QueryCompleted += _ => throw new InvalidOperationException("completed boom");
        Results(Connected);

        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Equal(2, CountLevel(LogLevel.Error));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("starting boom"));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("completed boom"));
    }

    // 問い合わせが取り消しの例外で終わったら、そのまま外へ出し、QueryCompleted は発火しない
    [Fact]
    public void Tick_QueryThrowsCancellation_PropagatesWithoutQueryCompleted()
    {
        var engine = StartedEngine();
        var completed = 0;
        engine.QueryCompleted += _ => completed++;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _probe.OnQuery = token => token.ThrowIfCancellationRequested();

        Assert.ThrowsAny<OperationCanceledException>(() => engine.Tick(cancellation.Token));

        Assert.Equal(0, completed);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(HeadsetState.Disconnected, engine.State);
    }

    // ---- 残量の通知（LevelsUpdated、Issue rurute/inzone-auto-switch#27） ----

    [Fact]
    public void Tick_Connected_RaisesLevelsUpdatedWithLevels()
    {
        var engine = StartedEngine();
        var received = new List<BudsLevels?>();
        engine.LevelsUpdated += levels => received.Add(levels);
        Results(new ProbeResult(ProbeStatus.Connected, new BudsLevels(80, 60, null)));

        engine.Tick();

        Assert.Equal([new BudsLevels(80, 60, null)], received);
    }

    [Fact]
    public void Tick_Unavailable_RaisesLevelsUpdatedWithNull()
    {
        var engine = StartedEngine();
        var received = new List<BudsLevels?>();
        engine.LevelsUpdated += levels => received.Add(levels);
        Results(Unavailable);

        engine.Tick();

        Assert.Equal([null], received);
    }

    [Fact]
    public void Tick_LevelsUpdatedHandlerThrows_LogsErrorAndStillSwitches()
    {
        var engine = StartedEngine();
        engine.LevelsUpdated += _ => throw new InvalidOperationException("levels boom");
        Results(Connected);

        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error
            && e.Message.Contains("残量の通知で予期しない例外")
            && e.Message.Contains("levels boom"));
    }

    // 問い合わせが取り消しの例外で終わったら、LevelsUpdated も発火しない
    [Fact]
    public void Tick_QueryThrowsCancellation_DoesNotRaiseLevelsUpdated()
    {
        var engine = StartedEngine();
        var raised = 0;
        engine.LevelsUpdated += _ => raised++;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _probe.OnQuery = token => token.ThrowIfCancellationRequested();

        Assert.ThrowsAny<OperationCanceledException>(() => engine.Tick(cancellation.Token));

        Assert.Equal(0, raised);
    }

    // ---- 取り消し ----

    [Fact]
    public void Tick_CancelledDuringQuery_DoesNotSwitch()
    {
        var engine = StartedEngine();
        using var cancellation = new CancellationTokenSource();
        _probe.OnQuery = _ => cancellation.Cancel();
        Results(Connected);

        engine.Tick(cancellation.Token);

        Assert.Empty(_switcher.Calls);
        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(1, Count(LogLevel.Info, CancelledMessage));
    }

    [Fact]
    public void Tick_Cancelled_StopsBeforeSwitching()
    {
        var engine = StartedEngine();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Results(Connected);

        engine.Tick(cancellation.Token);

        Assert.Empty(_switcher.Calls);
        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(1, Count(LogLevel.Info, CancelledMessage));
    }

    [Fact]
    public void Tick_CancelledWhileRecovering_DoesNotSwitch()
    {
        Results(Connected, Unavailable, Connected);
        var engine = CreateEngine();
        engine.StartupSync();
        engine.Tick();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        engine.Tick(cancellation.Token);

        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Equal(1, Count(LogLevel.Info, CancelledMessage));
    }

    // ---- スリープ復帰 ----

    [Fact]
    public void Tick_LongGap_LogsResume()
    {
        var engine = StartedEngine();
        Results(Disconnected);

        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        engine.Tick();

        Assert.Equal(1, Count(LogLevel.Info, ResumeMessage));
    }

    // 「前回からの経過が300秒超」の境界。ちょうど300秒では復帰とみなさない
    [Fact]
    public void Tick_GapOfExactly300Seconds_DoesNotLogResume()
    {
        var engine = StartedEngine();
        Results(Disconnected);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(300);
        engine.Tick();

        Assert.Equal(0, Count(LogLevel.Info, ResumeMessage));
    }

    [Fact]
    public void Tick_GapJustOver300Seconds_LogsResume()
    {
        var engine = StartedEngine();
        Results(Disconnected);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(300).AddMilliseconds(1);
        engine.Tick();

        Assert.Equal(1, Count(LogLevel.Info, ResumeMessage));
    }

    // ---- 切り替えの結果と例外 ----

    [Fact]
    public void Tick_DeviceNotFound_DoesNotRetryInSameState()
    {
        _switcher.Result = SwitchResult.DeviceNotFound;
        var engine = StartedEngine();
        Results(Connected, Connected);

        engine.Tick();
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Single(_switcher.Calls);
    }

    [Fact]
    public void Tick_FailedSwitch_KeepsMonitoringWithoutRepeatingSameState()
    {
        _switcher.Result = SwitchResult.Failed;
        var engine = StartedEngine();
        Results(Connected, Connected);

        engine.Tick();
        engine.Tick();
        _switcher.Result = SwitchResult.Switched;
        Results(Disconnected);
        engine.Tick();

        Assert.Equal(["BUDS", "SPEAKER"], _switcher.Calls);
        Assert.Equal(HeadsetState.Disconnected, engine.State);
    }

    [Theory]
    [InlineData(SwitchResult.Switched, LogLevel.Info)]
    [InlineData(SwitchResult.AlreadyDefault, LogLevel.Debug)]
    [InlineData(SwitchResult.DeviceNotFound, LogLevel.Warn)]
    [InlineData(SwitchResult.Unavailable, LogLevel.Warn)]
    [InlineData(SwitchResult.Failed, LogLevel.Error)]
    public void Tick_SwitchOutcome_IsLoggedAtLevelForResult(SwitchResult result, LogLevel expectedLevel)
    {
        _switcher.Result = result;
        var engine = StartedEngine();
        Results(Connected);

        engine.Tick();

        Assert.Contains((expectedLevel, $"{result}: BUDS"), _logger.Entries);
    }

    // 切り替えの例外は外へ出す（監視ループが記録する）。状態は切り替えの前に更新済み
    [Fact]
    public void Tick_SwitcherThrows_PropagatesAfterUpdatingState()
    {
        var engine = StartedEngine();
        _switcher.Exception = new InvalidOperationException("simulated switcher failure");
        Results(Connected);

        var ex = Assert.Throws<InvalidOperationException>(engine.Tick);

        Assert.Equal("simulated switcher failure", ex.Message);
        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new AutoSwitchEngine(null!, _logger, _switcher, _clock, _probe));
        Assert.Throws<ArgumentNullException>(() => new AutoSwitchEngine(_settings, null!, _switcher, _clock, _probe));
        Assert.Throws<ArgumentNullException>(() => new AutoSwitchEngine(_settings, _logger, null!, _clock, _probe));
        Assert.Throws<ArgumentNullException>(() => new AutoSwitchEngine(_settings, _logger, _switcher, null!, _probe));
        Assert.Throws<ArgumentNullException>(() => new AutoSwitchEngine(_settings, _logger, _switcher, _clock, null!));
    }

    // ---- StateChanged ----

    [Fact]
    public void Tick_StateChanged_RaisesEventOnce()
    {
        var engine = StartedEngine();
        var raised = new List<HeadsetState>();
        engine.StateChanged += (_, state) => raised.Add(state);
        Results(Connected, Connected);

        engine.Tick();
        engine.Tick();

        Assert.Equal([HeadsetState.Connected], raised);
    }

    [Fact]
    public void Tick_SameState_DoesNotRaiseEvent()
    {
        var engine = StartedEngine();
        Results(Connected);
        engine.Tick();
        var raised = new List<HeadsetState>();
        engine.StateChanged += (_, state) => raised.Add(state);

        Results(Connected);
        engine.Tick();

        Assert.Empty(raised);
    }

    // 戻ったときの合わせ直しで状態が同じなら、StateChanged は発火しない（切り替えは行う）
    [Fact]
    public void Tick_RecoveredWithSameState_DoesNotRaiseEvent()
    {
        Results(Connected, Unavailable, Connected);
        var engine = CreateEngine();
        engine.StartupSync();
        var raised = new List<HeadsetState>();
        engine.StateChanged += (_, state) => raised.Add(state);

        engine.Tick();
        engine.Tick();

        Assert.Empty(raised);
        Assert.Equal(["BUDS", "BUDS"], _switcher.Calls);
    }

    [Fact]
    public void Tick_StateChangedHandlerThrows_LogsErrorAndKeepsSwitching()
    {
        var engine = StartedEngine();
        engine.StateChanged += (_, _) => throw new InvalidOperationException("boom");
        Results(Connected);

        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("boom"));
    }

    // ---- 録音の切り替え（Issue rurute/inzone-auto-switch#8） ----

    private void UseCaptureTargets() =>
        _settings = _settings with { BudsInputDevice = "BUDS_MIC", SpeakerInputDevice = "SPEAKER_MIC" };

    [Fact]
    public void Tick_ConnectThenDisconnect_SwitchesPlaybackThenCaptureInEachDirection()
    {
        UseCaptureTargets();
        var engine = StartedEngine();
        Results(Connected, Disconnected);

        engine.Tick();
        engine.Tick();

        Assert.Equal(
            [
                (DeviceFlow.Playback, "BUDS"),
                (DeviceFlow.Capture, "BUDS_MIC"),
                (DeviceFlow.Playback, "SPEAKER"),
                (DeviceFlow.Capture, "SPEAKER_MIC"),
            ],
            _switcher.FlowCalls);
        Assert.Contains((LogLevel.Info, "Switched: BUDS"), _logger.Entries);
        Assert.Contains((LogLevel.Info, "Capture Switched: BUDS_MIC"), _logger.Entries);
    }

    // 既定の設定（録音は未設定）では、これまでどおり再生だけを切り替える
    [Fact]
    public void Tick_CaptureTargetsUnset_SwitchesPlaybackOnly()
    {
        var engine = StartedEngine();
        Results(Connected, Disconnected);

        engine.Tick();
        engine.Tick();

        Assert.Equal([(DeviceFlow.Playback, "BUDS"), (DeviceFlow.Playback, "SPEAKER")], _switcher.FlowCalls);
    }

    // 未設定かどうかは向きごとに独立して判定する
    [Theory]
    [InlineData("", "SPEAKER_MIC")]
    [InlineData("   ", "SPEAKER_MIC")]
    public void Tick_CaptureTargetUnsetForConnectOnly_SwitchesCaptureOnDisconnectOnly(string budsInput, string speakerInput)
    {
        _settings = _settings with { BudsInputDevice = budsInput, SpeakerInputDevice = speakerInput };
        var engine = StartedEngine();
        Results(Connected, Disconnected);

        engine.Tick();
        engine.Tick();

        Assert.Equal(
            [(DeviceFlow.Playback, "BUDS"), (DeviceFlow.Playback, "SPEAKER"), (DeviceFlow.Capture, "SPEAKER_MIC")],
            _switcher.FlowCalls);
    }

    [Fact]
    public void Tick_SwitchOnConnectFalse_SkipsPlaybackAndCapture()
    {
        UseCaptureTargets();
        _settings = _settings with { SwitchOnConnect = false };
        var engine = StartedEngine();
        Results(Connected);

        engine.Tick();

        Assert.Empty(_switcher.FlowCalls);
        Assert.Contains((LogLevel.Info, "switchOnConnect が false のため切り替えません。"), _logger.Entries);
    }

    [Fact]
    public void Tick_SwitchOnDisconnectFalse_SkipsPlaybackAndCapture()
    {
        UseCaptureTargets();
        _settings = _settings with { SwitchOnDisconnect = false };
        var engine = StartedEngine();
        Results(Connected, Disconnected);

        engine.Tick();
        engine.Tick();

        Assert.Equal([(DeviceFlow.Playback, "BUDS"), (DeviceFlow.Capture, "BUDS_MIC")], _switcher.FlowCalls);
        Assert.Contains((LogLevel.Info, "switchOnDisconnect が false のため切り替えません。"), _logger.Entries);
    }

    // 再生の結果が失敗でも録音は試みる。録音の結果は結果ごとのレベルでログに残る
    [Theory]
    [InlineData(SwitchResult.DeviceNotFound, LogLevel.Warn)]
    [InlineData(SwitchResult.Unavailable, LogLevel.Warn)]
    [InlineData(SwitchResult.Failed, LogLevel.Error)]
    public void Tick_PlaybackNotSwitched_StillSwitchesCapture(SwitchResult playbackResult, LogLevel playbackLevel)
    {
        UseCaptureTargets();
        _switcher.Result = playbackResult;
        _switcher.CaptureResult = SwitchResult.Switched;
        var engine = StartedEngine();
        Results(Connected);

        engine.Tick();

        Assert.Equal([(DeviceFlow.Playback, "BUDS"), (DeviceFlow.Capture, "BUDS_MIC")], _switcher.FlowCalls);
        Assert.Contains((playbackLevel, $"{playbackResult}: BUDS"), _logger.Entries);
        Assert.Contains((LogLevel.Info, "Capture Switched: BUDS_MIC"), _logger.Entries);
    }

    [Theory]
    [InlineData(SwitchResult.Switched, LogLevel.Info)]
    [InlineData(SwitchResult.AlreadyDefault, LogLevel.Debug)]
    [InlineData(SwitchResult.DeviceNotFound, LogLevel.Warn)]
    [InlineData(SwitchResult.Unavailable, LogLevel.Warn)]
    [InlineData(SwitchResult.Failed, LogLevel.Error)]
    public void Tick_CaptureOutcome_IsLoggedAtLevelForResult(SwitchResult captureResult, LogLevel expectedLevel)
    {
        UseCaptureTargets();
        _switcher.CaptureResult = captureResult;
        var engine = StartedEngine();
        Results(Connected);

        engine.Tick();

        Assert.Contains((expectedLevel, $"Capture {captureResult}: BUDS_MIC"), _logger.Entries);
    }

    // Switcher 自身の想定外の例外は従来どおり外へ出す。再生で出たら録音は試みない
    [Fact]
    public void Tick_SwitcherThrowsOnPlayback_PropagatesWithoutCapture()
    {
        UseCaptureTargets();
        var engine = StartedEngine();
        _switcher.Exception = new InvalidOperationException("simulated switcher failure");
        Results(Connected);

        var ex = Assert.Throws<InvalidOperationException>(engine.Tick);

        Assert.Equal("simulated switcher failure", ex.Message);
        Assert.Equal([(DeviceFlow.Playback, "BUDS")], _switcher.FlowCalls);
    }

    [Fact]
    public void StartupSync_Connected_SwitchesPlaybackThenCapture()
    {
        UseCaptureTargets();
        Results(Connected);
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal([(DeviceFlow.Playback, "BUDS"), (DeviceFlow.Capture, "BUDS_MIC")], _switcher.FlowCalls);
    }

    [Fact]
    public void Tick_SameStateRepeated_DoesNotSwitchCaptureAgain()
    {
        UseCaptureTargets();
        var engine = StartedEngine();
        Results(Connected, Connected);

        engine.Tick();
        engine.Tick();

        Assert.Equal([(DeviceFlow.Playback, "BUDS"), (DeviceFlow.Capture, "BUDS_MIC")], _switcher.FlowCalls);
    }

    // 段階3の終了の契約: 取り消しのあとの音声操作は多くとも1回。再生の切り替え中に取り消されたら録音はしない
    [Fact]
    public void Tick_CancelledDuringPlaybackSwitch_SkipsCapture()
    {
        UseCaptureTargets();
        var engine = StartedEngine();
        using var cancellation = new CancellationTokenSource();
        _switcher.OnSwitch = (flow, _) =>
        {
            if (flow == DeviceFlow.Playback)
            {
                cancellation.Cancel();
            }
        };
        Results(Connected);

        engine.Tick(cancellation.Token);

        Assert.Equal([(DeviceFlow.Playback, "BUDS")], _switcher.FlowCalls);
        Assert.Contains((LogLevel.Info, "終了が要求されたため、録音の切り替えを行いません。"), _logger.Entries);
    }

    [Fact]
    public void StartupSync_CancelledDuringPlaybackSwitch_SkipsCapture()
    {
        UseCaptureTargets();
        Results(Connected);
        var engine = CreateEngine();
        using var cancellation = new CancellationTokenSource();
        _switcher.OnSwitch = (flow, _) =>
        {
            if (flow == DeviceFlow.Playback)
            {
                cancellation.Cancel();
            }
        };

        engine.StartupSync(cancellation.Token);

        Assert.Equal([(DeviceFlow.Playback, "BUDS")], _switcher.FlowCalls);
        Assert.Contains((LogLevel.Info, "終了が要求されたため、録音の切り替えを行いません。"), _logger.Entries);
    }

    // 録音が未設定なら、取り消されていても取り消しのログは出さない（録音をもともと試みないため）
    [Fact]
    public void Tick_CancelledDuringPlaybackSwitch_CaptureUnset_LogsNothingAboutCapture()
    {
        var engine = StartedEngine();
        using var cancellation = new CancellationTokenSource();
        _switcher.OnSwitch = (_, _) => cancellation.Cancel();
        Results(Connected);

        engine.Tick(cancellation.Token);

        Assert.Equal([(DeviceFlow.Playback, "BUDS")], _switcher.FlowCalls);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("録音の切り替えを行いません"));
    }

    // ---- 動作中の設定の入れ替え（Issue rurute/inzone-auto-switch#9） ----

    private static AppSettings NewTargets(AppSettings current) => current with
    {
        BudsDevice = "NEW_BUDS",
        SpeakerDevice = "NEW_SPEAKER",
        BudsInputDevice = "NEW_BUDS_MIC",
        SpeakerInputDevice = "NEW_SPEAKER_MIC",
    };

    [Fact]
    public void Settings_StartsWithConstructorSettings()
    {
        var engine = CreateEngine();

        Assert.Same(_settings, engine.Settings.Current);
    }

    [Fact]
    public void Tick_AfterReplace_UsesNewTargetsFromNextTransition()
    {
        UseCaptureTargets();
        var engine = StartedEngine();
        Results(Connected);
        engine.Tick();

        engine.Settings.Replace(NewTargets(engine.Settings.Current));
        Results(Disconnected, Connected);
        engine.Tick();
        engine.Tick();

        Assert.Equal(
            [
                (DeviceFlow.Playback, "BUDS"),
                (DeviceFlow.Capture, "BUDS_MIC"),
                (DeviceFlow.Playback, "NEW_SPEAKER"),
                (DeviceFlow.Capture, "NEW_SPEAKER_MIC"),
                (DeviceFlow.Playback, "NEW_BUDS"),
                (DeviceFlow.Capture, "NEW_BUDS_MIC"),
            ],
            _switcher.FlowCalls);
    }

    [Fact]
    public void Tick_AfterReplace_UsesNewSwitchFlags()
    {
        UseCaptureTargets();
        var engine = StartedEngine();

        engine.Settings.Replace(engine.Settings.Current with { SwitchOnConnect = false });
        Results(Connected);
        engine.Tick();
        engine.Settings.Replace(engine.Settings.Current with { SwitchOnConnect = true, SwitchOnDisconnect = false });
        Results(Disconnected);
        engine.Tick();

        Assert.Empty(_switcher.FlowCalls);
        Assert.Contains((LogLevel.Info, "switchOnConnect が false のため切り替えません。"), _logger.Entries);
        Assert.Contains((LogLevel.Info, "switchOnDisconnect が false のため切り替えません。"), _logger.Entries);
    }

    [Fact]
    public void Replace_AloneDoesNotSwitchOrChangeState()
    {
        UseCaptureTargets();
        var engine = StartedEngine();
        Results(Connected);
        engine.Tick();
        var callsBefore = _switcher.FlowCalls.Count;

        engine.Settings.Replace(NewTargets(engine.Settings.Current));
        engine.Tick();

        Assert.Equal(callsBefore, _switcher.FlowCalls.Count);
        Assert.Equal(HeadsetState.Connected, engine.State);
    }

    // 6項目以外は起動時の値を使い続ける（spec §6）
    [Fact]
    public void Tick_AfterReplace_KeepsStartupPollInterval()
    {
        var engine = StartedEngine();

        engine.Settings.Replace(engine.Settings.Current with { PollIntervalSeconds = 30, IdlePollIntervalSeconds = 60 });
        Results(Connected);
        engine.Tick();

        Assert.Equal(PollSeconds, engine.NextDelaySeconds);

        Results(Unavailable);
        engine.Tick();

        Assert.Equal(IdleSeconds, engine.NextDelaySeconds);
    }

    // 1回の切り替えは始めたときの設定で最後まで行う（spec §6）
    [Fact]
    public void Tick_ReplacedDuringPlaybackSwitch_FinishesWithSnapshotThenUsesNewSettings()
    {
        UseCaptureTargets();
        var engine = StartedEngine();
        var replaced = false;
        _switcher.OnSwitch = (flow, _) =>
        {
            if (flow == DeviceFlow.Playback && !replaced)
            {
                replaced = true;
                engine.Settings.Replace(NewTargets(engine.Settings.Current));
            }
        };
        Results(Connected, Disconnected);

        engine.Tick();
        engine.Tick();

        Assert.Equal(
            [
                (DeviceFlow.Playback, "BUDS"),
                (DeviceFlow.Capture, "BUDS_MIC"),
                (DeviceFlow.Playback, "NEW_SPEAKER"),
                (DeviceFlow.Capture, "NEW_SPEAKER_MIC"),
            ],
            _switcher.FlowCalls);
    }

    [Fact]
    public void StartupSync_ReplacedDuringPlaybackSwitch_FinishesWithSnapshotThenUsesNewSettings()
    {
        UseCaptureTargets();
        Results(Connected, Disconnected);
        var engine = CreateEngine();
        var replaced = false;
        _switcher.OnSwitch = (flow, _) =>
        {
            if (flow == DeviceFlow.Playback && !replaced)
            {
                replaced = true;
                engine.Settings.Replace(NewTargets(engine.Settings.Current));
            }
        };

        engine.StartupSync();
        engine.Tick();

        Assert.Equal(
            [
                (DeviceFlow.Playback, "BUDS"),
                (DeviceFlow.Capture, "BUDS_MIC"),
                (DeviceFlow.Playback, "NEW_SPEAKER"),
                (DeviceFlow.Capture, "NEW_SPEAKER_MIC"),
            ],
            _switcher.FlowCalls);
    }
}
