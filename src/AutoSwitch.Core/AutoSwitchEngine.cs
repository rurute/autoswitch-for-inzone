using System.Diagnostics;

namespace AutoSwitch.Core;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public interface IHubProcessProbe
{
    /// <summary>INZONEHub の最も早い起動時刻（UTC）。無い・取得に失敗した場合は null</summary>
    DateTime? GetStartTimeUtc();
}

public sealed class SystemHubProcessProbe : IHubProcessProbe
{
    private const string HubProcessName = "INZONEHub";

    public DateTime? GetStartTimeUtc()
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcessesByName(HubProcessName);
            if (processes.Length == 0)
            {
                return null;
            }

            // 起動時刻を読めないプロセスがあれば、取得の失敗として null にする
            return processes.Min(p => p.StartTime).ToUniversalTime();
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

/// <summary>
/// 起動時同期と監視 tick。規則とログの文言は旧版 Orchestrator.psm1 に合わせる。
/// 切り替えの例外は外へ出す（監視ループが記録する）。
/// </summary>
public sealed class AutoSwitchEngine
{
    private const int SeenLogIdCapacity = 200;
    private const int MaxBackoffSeconds = 60;
    private const int ResumeGapSeconds = 300;

    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;
    private readonly IPlaybackSwitcher _switcher;
    private readonly IClock _clock;
    private readonly IHubProcessProbe _hub;
    private readonly ActionLogReader _reader;
    private readonly SeenLogIdSet _seenLogIds = new(SeenLogIdCapacity);
    private DateTime? _lastTickUtc;

    public AutoSwitchEngine(AppSettings settings, IAppLogger logger, IPlaybackSwitcher switcher, IClock clock, IHubProcessProbe hub)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(switcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hub);

        _settings = settings;
        _logger = logger;
        _switcher = switcher;
        _clock = clock;
        _hub = hub;
        _reader = new ActionLogReader(settings.ActionLogPath);
        NextDelaySeconds = settings.PollIntervalSeconds;
    }

    public HeadsetState State { get; private set; } = HeadsetState.Unknown;

    public int ConsecutiveErrors { get; private set; }

    public int NextDelaySeconds { get; private set; }

    public void StartupSync()
    {
        var read = _reader.Read();
        _lastTickUtc = _clock.UtcNow;

        if (read.Status == ReadStatus.FileMissing)
        {
            _logger.Write(LogLevel.Info, "ActionLog.log がまだありません。INZONE Hub の起動を待機します。");
            NextDelaySeconds = _settings.IdlePollIntervalSeconds;
            return;
        }

        if (read.Status == ReadStatus.Error)
        {
            _logger.Write(LogLevel.Warn, $"起動時同期: ActionLog.log を読めません: {read.Message}");
            NextDelaySeconds = _settings.IdlePollIntervalSeconds;
            return;
        }

        // 採用可否にかかわらず、読んだ行はすべて既読として登録する。
        // これをしないと次の tick で過去のイベントが「新規」として再発火する
        var events = HubEventParser.Parse(read.Lines, _seenLogIds);
        NextDelaySeconds = _settings.PollIntervalSeconds;

        if (_settings.StartupSyncMaxAgeMinutes <= 0)
        {
            _logger.Write(LogLevel.Info, "起動時同期は無効です。既存のログは読み飛ばしました。");
            return;
        }

        if (events.Count == 0)
        {
            _logger.Write(LogLevel.Info, "起動時同期: 判定できるイベントがありません。切り替えません。");
            return;
        }

        var hubStartUtc = _hub.GetStartTimeUtc();
        if (hubStartUtc is null)
        {
            _logger.Write(LogLevel.Info, "起動時同期: INZONE Hub が起動していません。切り替えません。");
            return;
        }

        var lastEvent = events[^1];
        // 範囲外の timeStamp は旧版の ConvertFrom-EpochMilliseconds と同じく例外になる
        var eventUtc = DateTimeOffset.FromUnixTimeMilliseconds(lastEvent.TimeStampMs).UtcDateTime;
        var nowUtc = _clock.UtcNow;

        if (eventUtc < hubStartUtc.Value)
        {
            _logger.Write(LogLevel.Info, "起動時同期: 最終イベントが INZONE Hub の起動より前です。切り替えません。");
            return;
        }

        var ageMinutes = (nowUtc - eventUtc).TotalMinutes;
        if (ageMinutes > _settings.StartupSyncMaxAgeMinutes)
        {
            // 旧版の '{0:N1}' -f と同じく現在のカルチャで書式化する
            _logger.Write(LogLevel.Info, $"起動時同期: 最終イベントが古すぎます（{ageMinutes:N1} 分前）。切り替えません。");
            return;
        }

        State = lastEvent.State;
        _logger.Write(LogLevel.Info, $"起動時同期: 現在の状態を {lastEvent.State} と判定しました。");
        SwitchFor(lastEvent.State);
    }

    public void Tick()
    {
        var nowUtc = _clock.UtcNow;
        if (_lastTickUtc is not null && (nowUtc - _lastTickUtc.Value).TotalSeconds > ResumeGapSeconds)
        {
            _logger.Write(LogLevel.Info, "長時間のギャップを検知しました（スリープ復帰とみなします）。監視を通常間隔へ戻します。");
            ConsecutiveErrors = 0;
        }
        _lastTickUtc = nowUtc;

        var read = _reader.Read();

        if (read.Status == ReadStatus.FileMissing)
        {
            _logger.Write(LogLevel.Debug, "ActionLog.log がありません。待機します。");
            ConsecutiveErrors = 0;
            NextDelaySeconds = _settings.IdlePollIntervalSeconds;
            return;
        }

        if (read.Status == ReadStatus.Error)
        {
            ConsecutiveErrors++;
            var delay = (double)_settings.PollIntervalSeconds * Math.Pow(2, ConsecutiveErrors - 1);
            NextDelaySeconds = (int)Math.Min(delay, MaxBackoffSeconds);
            _logger.Write(LogLevel.Warn, $"ActionLog.log の読み取りに失敗しました: {read.Message}");
            return;
        }

        if (read.Status == ReadStatus.Truncated)
        {
            _logger.Write(LogLevel.Info, "ActionLog.log の truncate を検知しました。先頭から読み直します。");
        }
        else if (read.Status == ReadStatus.Recreated)
        {
            _logger.Write(LogLevel.Info, "ActionLog.log の再生成を検知しました。先頭から読み直します。");
        }

        ConsecutiveErrors = 0;
        NextDelaySeconds = _settings.PollIntervalSeconds;

        // 追記順がそのまま時系列順なので並べ替えない
        foreach (var hubEvent in HubEventParser.Parse(read.Lines, _seenLogIds))
        {
            var (changed, newState) = StateTransition.Resolve(State, hubEvent.State);
            if (!changed)
            {
                _logger.Write(LogLevel.Debug, $"状態は {hubEvent.State} のままです。切り替えません。");
                continue;
            }

            State = newState;
            _logger.Write(LogLevel.Info, $"状態遷移を検知しました: {newState}");
            SwitchFor(newState);
        }
    }

    private void SwitchFor(HeadsetState state)
    {
        string targetName;
        if (state == HeadsetState.Connected)
        {
            if (!_settings.SwitchOnConnect)
            {
                _logger.Write(LogLevel.Info, "switchOnConnect が false のため切り替えません。");
                return;
            }
            targetName = _settings.BudsDevice;
        }
        else
        {
            if (!_settings.SwitchOnDisconnect)
            {
                _logger.Write(LogLevel.Info, "switchOnDisconnect が false のため切り替えません。");
                return;
            }
            targetName = _settings.SpeakerDevice;
        }

        var outcome = _switcher.SwitchTo(targetName);
        var level = outcome.Result switch
        {
            SwitchResult.Switched => LogLevel.Info,
            SwitchResult.AlreadyDefault => LogLevel.Debug,
            SwitchResult.DeviceNotFound => LogLevel.Warn,
            SwitchResult.Unavailable => LogLevel.Warn,
            _ => LogLevel.Error,
        };
        _logger.Write(level, outcome.Message);
    }
}
