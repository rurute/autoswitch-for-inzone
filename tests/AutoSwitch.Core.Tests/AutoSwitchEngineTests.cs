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
}
