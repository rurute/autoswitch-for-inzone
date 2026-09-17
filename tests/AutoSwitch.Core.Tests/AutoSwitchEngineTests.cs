using System.Text;

namespace AutoSwitch.Core.Tests;

// 旧版 Orchestrator.Tests.ps1 と同じく、実際の一時ファイルと差し替えた時計・Hub 検出・切り替えで検証する
public sealed class AutoSwitchEngineTests : IDisposable
{
    private const int ConnectDevice = 23011;
    private const int DisconnectDevice = 23012;

    // 既定の時刻: 2026-09-15 06:36:40Z 固定
    private static readonly DateTime NowUtc = new(2026, 9, 15, 6, 36, 40, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _path;
    private readonly ListLogger _logger = new();
    private readonly RecordingSwitcher _switcher = new();
    private readonly FakeClock _clock = new(NowUtc);
    private readonly FakeHubProcessProbe _hub = new() { StartTimeUtc = NowUtc.AddMinutes(-30) };
    private AppSettings _settings;

    public AutoSwitchEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ase-" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "ActionLog.log");
        _settings = AppSettings.Default with
        {
            BudsDevice = "BUDS",
            SpeakerDevice = "SPEAKER",
            ActionLogPath = _path,
        };
    }

    public void Dispose()
    {
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

    private static long EpochMs(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static string EventLine(string logId, long timeStampMs, int actionTypeId)
    {
        var name = actionTypeId == ConnectDevice ? "ConnectDevice" : "DisconnectDevice";
        return $"{{\"logVersion\":\"1.5\",\"logId\":\"{logId}\",\"timeStamp\":{timeStampMs},\"actionTypeId\":{actionTypeId},\"actionTypeName\":\"{name}\"}}";
    }

    private static string Text(string[] lines) => lines.Length == 0 ? string.Empty : string.Join("\n", lines) + "\n";

    // 旧版テストの Write-LogLines と同じく、書き直しは BOM 付き、追記は BOM なし
    private void WriteLines(params string[] lines) =>
        File.WriteAllText(_path, Text(lines), new UTF8Encoding(true));

    private void AppendLines(params string[] lines)
    {
        var bytes = new UTF8Encoding(false).GetBytes(Text(lines));
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes, 0, bytes.Length);
    }

    // ActionLogReader の共有要求（ReadWrite | Delete）を拒む実際の排他ロック
    private FileStream Lock() => new(_path, FileMode.Open, FileAccess.Read, FileShare.None);

    // ---- StartupSync ----

    [Fact]
    public void StartupSync_HubNotRunning_DoesNotSwitch()
    {
        _hub.StartTimeUtc = null;
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-1)), ConnectDevice));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Contains((LogLevel.Info, "起動時同期: INZONE Hub が起動していません。切り替えません。"), _logger.Entries);
    }

    [Fact]
    public void StartupSync_LastEventBeforeHubStart_DoesNotSwitch()
    {
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddHours(-2)), ConnectDevice));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Contains((LogLevel.Info, "起動時同期: 最終イベントが INZONE Hub の起動より前です。切り替えません。"), _logger.Entries);
    }

    [Fact]
    public void StartupSync_LastEventOlderThanMaxAge_DoesNotSwitch()
    {
        _hub.StartTimeUtc = NowUtc.AddHours(-6);
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-11)), ConnectDevice));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        // 旧版は '{0:N1}' -f で現在のカルチャの書式にする
        Assert.Contains((LogLevel.Info, $"起動時同期: 最終イベントが古すぎます（{11.0:N1} 分前）。切り替えません。"), _logger.Entries);
    }

    [Fact]
    public void StartupSync_RecentConnectEvent_SwitchesToBudsOnce()
    {
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-2)), ConnectDevice));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Equal(_settings.PollIntervalSeconds, engine.NextDelaySeconds);
        Assert.Contains((LogLevel.Info, "起動時同期: 現在の状態を Connected と判定しました。"), _logger.Entries);
    }

    [Fact]
    public void StartupSync_MultipleEvents_UsesLastEvent()
    {
        WriteLines(
            EventLine("e1", EpochMs(NowUtc.AddMinutes(-5)), ConnectDevice),
            EventLine("e2", EpochMs(NowUtc.AddMinutes(-2)), DisconnectDevice));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(["SPEAKER"], _switcher.Calls);
    }

    [Fact]
    public void StartupSync_ThenTick_DoesNotRefireSyncedEvent()
    {
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-2)), ConnectDevice));
        var engine = CreateEngine();

        engine.StartupSync();
        engine.Tick();

        Assert.Single(_switcher.Calls);
    }

    [Fact]
    public void StartupSync_MaxAgeZero_SkipsExistingLinesWithoutSwitching()
    {
        _settings = _settings with { StartupSyncMaxAgeMinutes = 0 };
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-2)), ConnectDevice));
        var engine = CreateEngine();

        engine.StartupSync();
        engine.Tick();

        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Contains((LogLevel.Info, "起動時同期は無効です。既存のログは読み飛ばしました。"), _logger.Entries);
    }

    [Fact]
    public void StartupSync_LogMissing_WaitsWithIdleInterval()
    {
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Equal(_settings.IdlePollIntervalSeconds, engine.NextDelaySeconds);
        Assert.Contains((LogLevel.Info, "ActionLog.log がまだありません。INZONE Hub の起動を待機します。"), _logger.Entries);
    }

    // 以下、表の外。旧版 Orchestrator.psm1 の分岐と境界を確認する

    [Fact]
    public void StartupSync_ReadError_WaitsWithIdleIntervalAndWarns()
    {
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-2)), ConnectDevice));
        var engine = CreateEngine();

        using (Lock())
        {
            engine.StartupSync();
        }

        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(_settings.IdlePollIntervalSeconds, engine.NextDelaySeconds);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warn && e.Message.StartsWith("起動時同期: ActionLog.log を読めません: ", StringComparison.Ordinal));
    }

    // 旧版はイベントが無ければ Hub のプロセスを調べずに終える
    [Fact]
    public void StartupSync_NoEvents_DoesNotSwitchOrProbeHub()
    {
        WriteLines("{\"logId\":\"x1\",\"timeStamp\":1000,\"actionTypeId\":23004}");
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Equal(0, _hub.CallCount);
        Assert.Equal(_settings.PollIntervalSeconds, engine.NextDelaySeconds);
        Assert.Contains((LogLevel.Info, "起動時同期: 判定できるイベントがありません。切り替えません。"), _logger.Entries);
    }

    // 旧版は「上限より古い」を -gt で判定するため、ちょうど上限なら切り替える
    [Fact]
    public void StartupSync_LastEventExactlyMaxAge_Switches()
    {
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-_settings.StartupSyncMaxAgeMinutes)), ConnectDevice));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
    }

    // 旧版は「Hub 起動より前」を -lt で判定するため、起動時刻ちょうどなら切り替える
    [Fact]
    public void StartupSync_LastEventExactlyAtHubStart_Switches()
    {
        _hub.StartTimeUtc = NowUtc.AddMinutes(-3);
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-3)), DisconnectDevice));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(["SPEAKER"], _switcher.Calls);
    }

    // ---- Tick ----

    [Fact]
    public void NewEngine_StartsUnknownWithPollInterval()
    {
        var engine = CreateEngine();

        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Equal(0, engine.ConsecutiveErrors);
        Assert.Equal(_settings.PollIntervalSeconds, engine.NextDelaySeconds);
    }

    [Fact]
    public void Tick_ConnectEventAppended_SwitchesToBuds()
    {
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Contains((LogLevel.Info, "状態遷移を検知しました: Connected"), _logger.Entries);
    }

    [Fact]
    public void Tick_ConnectThenDisconnect_SwitchesToBudsThenSpeaker()
    {
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("d1", 2000, DisconnectDevice));
        engine.Tick();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(["BUDS", "SPEAKER"], _switcher.Calls);
    }

    [Fact]
    public void Tick_ConnectTwice_SwitchesOnlyOnce()
    {
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("c2", 5000, ConnectDevice));
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Single(_switcher.Calls);
        Assert.Contains((LogLevel.Debug, "状態は Connected のままです。切り替えません。"), _logger.Entries);
    }

    [Fact]
    public void Tick_SwitchOnConnectFalse_UpdatesStateWithoutSwitching()
    {
        _settings = _settings with { SwitchOnConnect = false };
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Empty(_switcher.Calls);
        Assert.Contains((LogLevel.Info, "switchOnConnect が false のため切り替えません。"), _logger.Entries);
    }

    [Fact]
    public void Tick_SwitchOnDisconnectFalse_UpdatesStateWithoutSwitching()
    {
        _settings = _settings with { SwitchOnDisconnect = false };
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("d1", 2000, DisconnectDevice));
        engine.Tick();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Contains((LogLevel.Info, "switchOnDisconnect が false のため切り替えません。"), _logger.Entries);
    }

    [Fact]
    public void Tick_MultipleEventsInOneAppend_AppliesInOrder()
    {
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();

        AppendLines(
            EventLine("m1", 1000, ConnectDevice),
            EventLine("m2", 2000, DisconnectDevice),
            EventLine("m3", 3000, ConnectDevice));
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS", "SPEAKER", "BUDS"], _switcher.Calls);
    }

    [Fact]
    public void Tick_TruncatedToFirstLine_DoesNotRefire()
    {
        WriteLines(
            EventLine("t1", 1000, ConnectDevice),
            EventLine("t2", 2000, DisconnectDevice));
        var engine = CreateEngine();
        engine.Tick();
        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(2, _switcher.Calls.Count);

        // Hub が truncate して1行目だけを書き直した状況（Length < Offset）
        WriteLines(EventLine("t1", 1000, ConnectDevice));
        engine.Tick();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(2, _switcher.Calls.Count);
        Assert.Contains((LogLevel.Info, "ActionLog.log の truncate を検知しました。先頭から読み直します。"), _logger.Entries);
    }

    [Fact]
    public void Tick_FileDeleted_KeepsStateWithIdleInterval()
    {
        WriteLines(EventLine("k1", 1000, ConnectDevice));
        var engine = CreateEngine();
        engine.Tick();

        File.Delete(_path);
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(_settings.IdlePollIntervalSeconds, engine.NextDelaySeconds);
        Assert.Contains((LogLevel.Debug, "ActionLog.log がありません。待機します。"), _logger.Entries);
    }

    [Fact]
    public void Tick_FileMissingThenCreated_ResumesMonitoring()
    {
        var engine = CreateEngine();
        engine.Tick();
        Assert.Equal(_settings.IdlePollIntervalSeconds, engine.NextDelaySeconds);

        WriteLines(EventLine("r1", 1000, ConnectDevice));
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Equal(_settings.PollIntervalSeconds, engine.NextDelaySeconds);
    }

    [Fact]
    public void Tick_ReadErrors_BackOff1To2To4()
    {
        WriteLines();
        var engine = CreateEngine();

        using (Lock())
        {
            engine.Tick();
            Assert.Equal(1, engine.NextDelaySeconds);
            engine.Tick();
            Assert.Equal(2, engine.NextDelaySeconds);
            engine.Tick();
            Assert.Equal(4, engine.NextDelaySeconds);
        }

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warn && e.Message.StartsWith("ActionLog.log の読み取りに失敗しました: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Tick_ReadErrors_BackOffCapsAt60()
    {
        WriteLines();
        var engine = CreateEngine();

        using (Lock())
        {
            for (var i = 0; i < 12; i++)
            {
                engine.Tick();
            }
            Assert.Equal(60, engine.NextDelaySeconds);
        }
    }

    [Fact]
    public void Tick_SuccessAfterErrors_ResetsToPollInterval()
    {
        WriteLines();
        var engine = CreateEngine();

        using (Lock())
        {
            engine.Tick();
            engine.Tick();
            Assert.Equal(2, engine.NextDelaySeconds);
        }

        // ロックが外れたので次の tick は成功する
        engine.Tick();

        Assert.Equal(0, engine.ConsecutiveErrors);
        Assert.Equal(_settings.PollIntervalSeconds, engine.NextDelaySeconds);
    }

    [Fact]
    public void Tick_LongGap_ResetsBackOff()
    {
        // ロックを掴んだまま時刻を進めるのが要点。先に解放すると、ギャップ検知を削除しても
        // 通常の成功経路でリセットされてテストが通ってしまう
        WriteLines();
        var engine = CreateEngine();

        using (Lock())
        {
            for (var i = 0; i < 5; i++)
            {
                engine.Tick();
            }
            Assert.Equal(5, engine.ConsecutiveErrors);
            Assert.Equal(16, engine.NextDelaySeconds);

            // 1時間後に復帰。ギャップ検知が無ければ6回目のエラーとして 32 秒になる
            _clock.UtcNow = _clock.UtcNow.AddHours(1);
            engine.Tick();

            Assert.Equal(1, engine.ConsecutiveErrors);
            Assert.Equal(1, engine.NextDelaySeconds);
        }

        Assert.Contains((LogLevel.Info, "長時間のギャップを検知しました（スリープ復帰とみなします）。監視を通常間隔へ戻します。"), _logger.Entries);
    }

    // 既定の poll 間隔（1秒）だと係数を外した実装でも通ってしまうので、既定以外の間隔で
    // plan §5 Task 8 の `poll × 2^(回数-1)` をそのまま確かめる
    [Fact]
    public void Tick_ReadErrors_BackOffScalesWithPollInterval()
    {
        _settings = _settings with { PollIntervalSeconds = 3 };
        WriteLines();
        var engine = CreateEngine();

        using (Lock())
        {
            engine.Tick();
            Assert.Equal(3, engine.NextDelaySeconds);
            engine.Tick();
            Assert.Equal(6, engine.NextDelaySeconds);
            engine.Tick();
            Assert.Equal(12, engine.NextDelaySeconds);
            engine.Tick();
            Assert.Equal(24, engine.NextDelaySeconds);
            engine.Tick();
            Assert.Equal(48, engine.NextDelaySeconds);
            // 6回目は 96 になるが 60 で頭打ち
            engine.Tick();
            Assert.Equal(60, engine.NextDelaySeconds);
            engine.Tick();
            Assert.Equal(60, engine.NextDelaySeconds);
        }
    }

    // plan §5 Task 8 の「前回からの経過が300秒超」の境界。ちょうど300秒では再開とみなさない
    [Fact]
    public void Tick_GapOfExactly300Seconds_DoesNotResetBackOff()
    {
        WriteLines();
        var engine = CreateEngine();

        using (Lock())
        {
            for (var i = 0; i < 5; i++)
            {
                engine.Tick();
            }
            Assert.Equal(5, engine.ConsecutiveErrors);
            Assert.Equal(16, engine.NextDelaySeconds);

            _clock.UtcNow = _clock.UtcNow.AddSeconds(300);
            engine.Tick();

            Assert.Equal(6, engine.ConsecutiveErrors);
            Assert.Equal(32, engine.NextDelaySeconds);
        }

        Assert.DoesNotContain((LogLevel.Info, "長時間のギャップを検知しました（スリープ復帰とみなします）。監視を通常間隔へ戻します。"), _logger.Entries);
    }

    [Fact]
    public void Tick_GapJustOver300Seconds_ResetsBackOff()
    {
        WriteLines();
        var engine = CreateEngine();

        using (Lock())
        {
            for (var i = 0; i < 5; i++)
            {
                engine.Tick();
            }
            Assert.Equal(5, engine.ConsecutiveErrors);

            _clock.UtcNow = _clock.UtcNow.AddSeconds(300).AddMilliseconds(1);
            engine.Tick();

            Assert.Equal(1, engine.ConsecutiveErrors);
            Assert.Equal(1, engine.NextDelaySeconds);
        }

        Assert.Contains((LogLevel.Info, "長時間のギャップを検知しました（スリープ復帰とみなします）。監視を通常間隔へ戻します。"), _logger.Entries);
    }

    [Fact]
    public void Tick_DeviceNotFound_DoesNotRetryInSameState()
    {
        _switcher.Result = SwitchResult.DeviceNotFound;
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();

        AppendLines(EventLine("n1", 1000, ConnectDevice));
        engine.Tick();
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Single(_switcher.Calls);
    }

    [Fact]
    public void Tick_FailedSwitch_KeepsMonitoringWithoutRepeatingSameState()
    {
        _switcher.Result = SwitchResult.Failed;
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();

        AppendLines(EventLine("f1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("f2", 2000, ConnectDevice));
        engine.Tick();
        _switcher.Result = SwitchResult.Switched;
        AppendLines(EventLine("f3", 3000, DisconnectDevice));
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
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();

        AppendLines(EventLine("l1", 1000, ConnectDevice));
        engine.Tick();

        Assert.Contains((expectedLevel, $"{result}: BUDS"), _logger.Entries);
    }

    // 以下、表の外。旧版 Orchestrator.psm1 の分岐を確認する

    [Fact]
    public void Tick_Recreated_LogsAndReadsNewLines()
    {
        WriteLines(EventLine("o1", 1000, ConnectDevice));
        var engine = CreateEngine();
        engine.Tick();

        // 元より長い別内容へ差し替える
        WriteLines(EventLine("o2", 2000, DisconnectDevice));
        engine.Tick();

        Assert.Equal(HeadsetState.Disconnected, engine.State);
        Assert.Equal(["BUDS", "SPEAKER"], _switcher.Calls);
        Assert.Contains((LogLevel.Info, "ActionLog.log の再生成を検知しました。先頭から読み直します。"), _logger.Entries);
    }

    // 切り替えの例外は外へ出す（監視ループが記録する）。旧版と同じく状態は切り替えの前に更新済み
    [Fact]
    public void Tick_SwitcherThrows_PropagatesAfterUpdatingState()
    {
        _switcher.Exception = new InvalidOperationException("simulated switcher failure");
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();

        AppendLines(EventLine("x1", 1000, ConnectDevice));
        var ex = Assert.Throws<InvalidOperationException>(engine.Tick);

        Assert.Equal("simulated switcher failure", ex.Message);
        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new AutoSwitchEngine(null!, _logger, _switcher, _clock, _hub));
        Assert.Throws<ArgumentNullException>(() => new AutoSwitchEngine(_settings, null!, _switcher, _clock, _hub));
        Assert.Throws<ArgumentNullException>(() => new AutoSwitchEngine(_settings, _logger, null!, _clock, _hub));
        Assert.Throws<ArgumentNullException>(() => new AutoSwitchEngine(_settings, _logger, _switcher, null!, _hub));
        Assert.Throws<ArgumentNullException>(() => new AutoSwitchEngine(_settings, _logger, _switcher, _clock, null!));
    }

    // S2: DateTimeOffset.FromUnixTimeMilliseconds の有効範囲
    private const long MinValidUnixMs = -62135596800000L;
    private const long MaxValidUnixMs = 253402300799999L;

    [Fact]
    public void StartupSync_TimeStampAboveRange_LogsAndDoesNotThrow()
    {
        File.WriteAllText(_path, Text([EventLine("L1", long.MaxValue, ConnectDevice)]));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Empty(_switcher.Calls);
        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("範囲外"));
    }

    [Fact]
    public void StartupSync_TimeStampBelowRange_LogsAndDoesNotThrow()
    {
        File.WriteAllText(_path, Text([EventLine("L1", long.MinValue, ConnectDevice)]));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Empty(_switcher.Calls);
        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("範囲外"));
    }

    // 境界値は従来どおり受け入れる（範囲外として弾かない）
    [Fact]
    public void StartupSync_TimeStampAtUpperBound_IsAccepted()
    {
        File.WriteAllText(_path, Text([EventLine("L1", MaxValidUnixMs, ConnectDevice)]));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("範囲外"));
        Assert.Equal(HeadsetState.Connected, engine.State);
    }

    [Fact]
    public void Tick_StateChanged_RaisesEventOnce()
    {
        File.WriteAllText(_path, string.Empty);
        var engine = CreateEngine();
        engine.StartupSync();
        var raised = new List<HeadsetState>();
        engine.StateChanged += (_, state) => raised.Add(state);

        File.WriteAllText(_path, Text([EventLine("L1", EpochMs(NowUtc), ConnectDevice)]));
        engine.Tick();

        Assert.Equal([HeadsetState.Connected], raised);
    }

    [Fact]
    public void Tick_SameState_DoesNotRaiseEvent()
    {
        File.WriteAllText(_path, string.Empty);
        var engine = CreateEngine();
        engine.StartupSync();
        File.WriteAllText(_path, Text([EventLine("L1", EpochMs(NowUtc), ConnectDevice)]));
        engine.Tick();
        var raised = new List<HeadsetState>();
        engine.StateChanged += (_, state) => raised.Add(state);

        File.WriteAllText(_path, Text([EventLine("L2", EpochMs(NowUtc), ConnectDevice)]));
        engine.Tick();

        Assert.Empty(raised);
    }

    [Fact]
    public void Tick_StateChangedHandlerThrows_LogsErrorAndKeepsSwitching()
    {
        File.WriteAllText(_path, string.Empty);
        var engine = CreateEngine();
        engine.StartupSync();
        engine.StateChanged += (_, _) => throw new InvalidOperationException("boom");

        File.WriteAllText(_path, Text([EventLine("L1", EpochMs(NowUtc), ConnectDevice)]));
        engine.Tick();

        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("boom"));
    }

    // F5: 終了が要求されたら、その読み取りの残りのイベントを処理しない
    [Fact]
    public void Tick_Cancelled_StopsBeforeSwitching()
    {
        File.WriteAllText(_path, string.Empty);
        var engine = CreateEngine();
        engine.StartupSync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        File.WriteAllText(_path, Text([EventLine("L1", EpochMs(NowUtc), ConnectDevice)]));
        engine.Tick(cancellation.Token);

        Assert.Empty(_switcher.Calls);
        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Contains(_logger.Entries, e => e.Message.Contains("残りのイベントの処理を中止"));
    }

    // F9: 起動時同期も取り消されていれば切り替えない
    [Fact]
    public void StartupSync_Cancelled_DoesNotSwitch()
    {
        File.WriteAllText(_path, Text([EventLine("L1", EpochMs(NowUtc), ConnectDevice)]));
        var engine = CreateEngine();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        engine.StartupSync(cancellation.Token);

        Assert.Empty(_switcher.Calls);
        Assert.Equal(HeadsetState.Unknown, engine.State);
        Assert.Contains(_logger.Entries, e => e.Message.Contains("起動時同期の切り替えを行いません"));
    }

    // F9: 処理の途中で取り消されたら、その時点のイベントまでで止まる。
    // StateChanged は SwitchFor の前に発火するので、ハンドラから取り消すと
    // 「1件目は切り替わり、2件目は切り替わらない」という境界を固定できる
    [Fact]
    public void Tick_CancelledBetweenEvents_StopsAfterCurrentEvent()
    {
        File.WriteAllText(_path, string.Empty);
        var engine = CreateEngine();
        engine.StartupSync();
        using var cancellation = new CancellationTokenSource();
        engine.StateChanged += (_, _) => cancellation.Cancel();

        File.WriteAllText(_path, Text([
            EventLine("L1", EpochMs(NowUtc), ConnectDevice),
            EventLine("L2", EpochMs(NowUtc), DisconnectDevice),
        ]));
        engine.Tick(cancellation.Token);

        Assert.Equal(["BUDS"], _switcher.Calls);
        Assert.Equal(HeadsetState.Connected, engine.State);
        Assert.Contains(_logger.Entries, e => e.Message.Contains("残りのイベントの処理を中止"));
    }

    // 取り消されていなければ、1回の読み取りのイベントをすべて処理する
    [Fact]
    public void Tick_NotCancelled_ProcessesEveryEvent()
    {
        File.WriteAllText(_path, string.Empty);
        var engine = CreateEngine();
        engine.StartupSync();
        using var cancellation = new CancellationTokenSource();

        File.WriteAllText(_path, Text([
            EventLine("L1", EpochMs(NowUtc), ConnectDevice),
            EventLine("L2", EpochMs(NowUtc), DisconnectDevice),
        ]));
        engine.Tick(cancellation.Token);

        Assert.Equal(["BUDS", "SPEAKER"], _switcher.Calls);
    }

    // ---- 録音の切り替え（Issue rurute/inzone-auto-switch#8） ----

    private void UseCaptureTargets() =>
        _settings = _settings with { BudsInputDevice = "BUDS_MIC", SpeakerInputDevice = "SPEAKER_MIC" };

    // 空のログで監視を始め、1件ずつイベントを追記して tick する
    private AutoSwitchEngine StartedEngine()
    {
        WriteLines();
        var engine = CreateEngine();
        engine.Tick();
        return engine;
    }

    [Fact]
    public void Tick_ConnectThenDisconnect_SwitchesPlaybackThenCaptureInEachDirection()
    {
        UseCaptureTargets();
        var engine = StartedEngine();

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("d1", 2000, DisconnectDevice));
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

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("d1", 2000, DisconnectDevice));
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

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("d1", 2000, DisconnectDevice));
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

        AppendLines(EventLine("c1", 1000, ConnectDevice));
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

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("d1", 2000, DisconnectDevice));
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

        AppendLines(EventLine("c1", 1000, ConnectDevice));
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

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();

        Assert.Contains((expectedLevel, $"Capture {captureResult}: BUDS_MIC"), _logger.Entries);
    }

    // Switcher 自身の想定外の例外は従来どおり外へ出す。再生で出たら録音は試みない
    [Fact]
    public void Tick_SwitcherThrowsOnPlayback_PropagatesWithoutCapture()
    {
        UseCaptureTargets();
        _switcher.Exception = new InvalidOperationException("simulated switcher failure");
        var engine = StartedEngine();

        AppendLines(EventLine("x1", 1000, ConnectDevice));
        var ex = Assert.Throws<InvalidOperationException>(engine.Tick);

        Assert.Equal("simulated switcher failure", ex.Message);
        Assert.Equal([(DeviceFlow.Playback, "BUDS")], _switcher.FlowCalls);
    }

    [Fact]
    public void StartupSync_RecentConnectEvent_SwitchesPlaybackThenCapture()
    {
        UseCaptureTargets();
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-2)), ConnectDevice));
        var engine = CreateEngine();

        engine.StartupSync();

        Assert.Equal([(DeviceFlow.Playback, "BUDS"), (DeviceFlow.Capture, "BUDS_MIC")], _switcher.FlowCalls);
    }

    [Fact]
    public void Tick_SameStateRepeated_DoesNotSwitchCaptureAgain()
    {
        UseCaptureTargets();
        var engine = StartedEngine();

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("c2", 2000, ConnectDevice));
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

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick(cancellation.Token);

        Assert.Equal([(DeviceFlow.Playback, "BUDS")], _switcher.FlowCalls);
        Assert.Contains((LogLevel.Info, "終了が要求されたため、録音の切り替えを行いません。"), _logger.Entries);
    }

    [Fact]
    public void StartupSync_CancelledDuringPlaybackSwitch_SkipsCapture()
    {
        UseCaptureTargets();
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-2)), ConnectDevice));
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

        AppendLines(EventLine("c1", 1000, ConnectDevice));
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
        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();

        engine.Settings.Replace(NewTargets(engine.Settings.Current));
        AppendLines(EventLine("d1", 2000, DisconnectDevice));
        engine.Tick();
        AppendLines(EventLine("c2", 3000, ConnectDevice));
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
        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        engine.Settings.Replace(engine.Settings.Current with { SwitchOnConnect = true, SwitchOnDisconnect = false });
        AppendLines(EventLine("d1", 2000, DisconnectDevice));
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
        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        var callsBefore = _switcher.FlowCalls.Count;

        engine.Settings.Replace(NewTargets(engine.Settings.Current));
        engine.Tick();

        Assert.Equal(callsBefore, _switcher.FlowCalls.Count);
        Assert.Equal(HeadsetState.Connected, engine.State);
    }

    // 入れ替えても既読の logId は残る。既に処理したイベントを再び読んでも切り替えない（spec §6「守ること」）
    [Fact]
    public void Replace_KeepsSeenLogIds()
    {
        UseCaptureTargets();
        var engine = StartedEngine();
        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("d1", 2000, DisconnectDevice));
        engine.Tick();
        var callsBefore = _switcher.FlowCalls.Count;

        engine.Settings.Replace(NewTargets(engine.Settings.Current));
        // 短く書き直して truncate として先頭から読み直させ、既読の接続イベントだけを再び読ませる
        WriteLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();

        Assert.Equal(callsBefore, _switcher.FlowCalls.Count);
        Assert.Equal(HeadsetState.Disconnected, engine.State);
    }

    // 6項目以外は起動時の値を使い続ける（spec §6）
    [Fact]
    public void Tick_AfterReplace_KeepsStartupPollInterval()
    {
        var engine = StartedEngine();

        engine.Settings.Replace(engine.Settings.Current with { PollIntervalSeconds = 30, IdlePollIntervalSeconds = 60 });
        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();

        Assert.Equal(_settings.PollIntervalSeconds, engine.NextDelaySeconds);
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

        AppendLines(EventLine("c1", 1000, ConnectDevice));
        engine.Tick();
        AppendLines(EventLine("d1", 2000, DisconnectDevice));
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
        WriteLines(EventLine("e1", EpochMs(NowUtc.AddMinutes(-2)), ConnectDevice));
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
        AppendLines(EventLine("d1", EpochMs(NowUtc), DisconnectDevice));
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
