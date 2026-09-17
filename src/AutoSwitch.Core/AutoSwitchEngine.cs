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
/// 切り替えの6項目（SwitchSettings）は Settings から切り替えのたびに読み、ほかの項目は起動時の値を使い続ける（Issue rurute/inzone-auto-switch#9）。
/// </summary>
public sealed class AutoSwitchEngine
{
    private const int SeenLogIdCapacity = 200;
    private const int MaxBackoffSeconds = 60;
    private const int ResumeGapSeconds = 300;

    // DateTimeOffset.FromUnixTimeMilliseconds が受け付ける範囲（0001-01-01Z 〜 9999-12-31T23:59:59.999Z）
    private const long MinUnixMilliseconds = -62135596800000L;
    private const long MaxUnixMilliseconds = 253402300799999L;

    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;
    private readonly IAudioSwitcher _switcher;
    private readonly IClock _clock;
    private readonly IHubProcessProbe _hub;
    private readonly ActionLogReader _reader;
    private readonly SeenLogIdSet _seenLogIds = new(SeenLogIdCapacity);
    private DateTime? _lastTickUtc;

    public AutoSwitchEngine(AppSettings settings, IAppLogger logger, IAudioSwitcher switcher, IClock clock, IHubProcessProbe hub)
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
        Settings = new LiveSettings(settings);
    }

    /// <summary>
    /// 動作中の設定。入れ替えると、次に始まる切り替えから6項目の新しい値を使う。
    /// ポーリング間隔や起動時同期の条件など、6項目以外はコンストラクタで受け取った値を使い続ける
    /// </summary>
    public LiveSettings Settings { get; }

    public HeadsetState State { get; private set; } = HeadsetState.Unknown;

    /// <summary>状態が実際に変わったときだけ発火する。監視スレッドから呼ばれる</summary>
    public event EventHandler<HeadsetState>? StateChanged;

    private void SetState(HeadsetState newState)
    {
        if (State == newState)
        {
            return;
        }

        State = newState;

        try
        {
            StateChanged?.Invoke(this, newState);
        }
        catch (Exception ex)
        {
            // 表示側の失敗で監視と切り替えを止めない
            _logger.Write(LogLevel.Error, $"状態変更の通知で予期しない例外が発生しました: {ex.Message}");
        }
    }

    public int ConsecutiveErrors { get; private set; }

    public int NextDelaySeconds { get; private set; }

    public void StartupSync() => StartupSync(CancellationToken.None);

    public void StartupSync(CancellationToken token)
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

        if (read.Status == ReadStatus.Skipped)
        {
            _logger.Write(LogLevel.Warn, $"ActionLog.log の完結しない行が上限に達したため読み飛ばしました: {read.Message}");
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
        if (lastEvent.TimeStampMs < MinUnixMilliseconds || lastEvent.TimeStampMs > MaxUnixMilliseconds)
        {
            // 旧版は変換の例外でそのまま落ちる。常駐を止めないため、ここで弾いて切り替えないだけにする（S2）
            _logger.Write(LogLevel.Warn, $"起動時同期: 最終イベントの時刻が範囲外です（{lastEvent.TimeStampMs}）。切り替えません。");
            return;
        }

        // ここへ来る timeStamp は上で範囲を確認済み（旧版の ConvertFrom-EpochMilliseconds は範囲外で例外になる）
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

        if (token.IsCancellationRequested)
        {
            _logger.Write(LogLevel.Info, "終了が要求されたため、起動時同期の切り替えを行いません。");
            return;
        }

        SetState(lastEvent.State);
        _logger.Write(LogLevel.Info, $"起動時同期: 現在の状態を {lastEvent.State} と判定しました。");
        SwitchFor(lastEvent.State, token);
    }

    public void Tick() => Tick(CancellationToken.None);

    public void Tick(CancellationToken token)
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
        else if (read.Status == ReadStatus.Skipped)
        {
            _logger.Write(LogLevel.Warn, $"ActionLog.log の完結しない行が上限に達したため読み飛ばしました: {read.Message}");
        }

        ConsecutiveErrors = 0;
        NextDelaySeconds = _settings.PollIntervalSeconds;

        // 追記順がそのまま時系列順なので並べ替えない
        foreach (var hubEvent in HubEventParser.Parse(read.Lines, _seenLogIds))
        {
            // 終了が要求されたら残りは処理しない。1回の読み取りは最大 4 MiB 分の
            // イベントを含みうるので、ここで止めないと終了後も切り替えが続く
            if (token.IsCancellationRequested)
            {
                _logger.Write(LogLevel.Info, "終了が要求されたため、残りのイベントの処理を中止します。");
                break;
            }

            var (changed, newState) = StateTransition.Resolve(State, hubEvent.State);
            if (!changed)
            {
                _logger.Write(LogLevel.Debug, $"状態は {hubEvent.State} のままです。切り替えません。");
                continue;
            }

            SetState(newState);
            _logger.Write(LogLevel.Info, $"状態遷移を検知しました: {newState}");
            SwitchFor(newState, token);
        }
    }

    private void SwitchFor(HeadsetState state, CancellationToken token)
    {
        // 1回の切り替え（ON/OFF の判定、再生、録音）は、ここで取り出した設定だけで行う。
        // 途中で入れ替えられても古い設定と新しい設定が混ざらない（spec §6）
        var settings = Settings.Current;
        string playbackTarget;
        string captureTarget;
        if (state == HeadsetState.Connected)
        {
            if (!settings.SwitchOnConnect)
            {
                _logger.Write(LogLevel.Info, "switchOnConnect が false のため切り替えません。");
                return;
            }
            playbackTarget = settings.BudsDevice;
            captureTarget = settings.BudsInputDevice;
        }
        else
        {
            if (!settings.SwitchOnDisconnect)
            {
                _logger.Write(LogLevel.Info, "switchOnDisconnect が false のため切り替えません。");
                return;
            }
            playbackTarget = settings.SpeakerDevice;
            captureTarget = settings.SpeakerInputDevice;
        }

        // 再生と録音は互いに独立する。結果として返る失敗では止めない。
        // Switcher 自身の想定外の例外は捕まえずに外へ出す（再生で出れば録音は試みない）
        WriteOutcome(_switcher.SwitchTo(DeviceFlow.Playback, playbackTarget));

        // 録音の切り替え先が未設定なら何もしない（Issue rurute/inzone-auto-switch#8）
        if (string.IsNullOrWhiteSpace(captureTarget))
        {
            return;
        }

        // 取り消しのあとの音声操作を多くとも1回に保つ（段階3の終了の契約）
        if (token.IsCancellationRequested)
        {
            _logger.Write(LogLevel.Info, "終了が要求されたため、録音の切り替えを行いません。");
            return;
        }

        WriteOutcome(_switcher.SwitchTo(DeviceFlow.Capture, captureTarget));
    }

    private void WriteOutcome(SwitchOutcome outcome)
    {
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
