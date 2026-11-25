namespace AutoSwitch.Core;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// 起動時の合わせ直しと監視 tick。INZONE Buds のドングルへの問い合わせ（IBudsProbe）で状態を判定する（Issue rurute/inzone-auto-switch#23）。
/// 起動時と、読めない状態から戻ったときは、前の状態と同じでも今の状態に合わせて 1 回切り替える
/// （startupSyncMaxAgeMinutes が 0 なら判定だけ）。それ以外は状態が変わったときだけ切り替える。
/// 読めないときは状態を変えず、idle 間隔で探し直す。ログは読めなくなったとき・戻ったときに 1 回ずつ。
/// 切り替えの例外と、問い合わせの取り消しの例外は外へ出す（監視ループが扱う）。
/// 切り替えの6項目（SwitchSettings）は Settings から切り替えのたびに読み、ほかの項目は起動時の値を使い続ける（Issue rurute/inzone-auto-switch#9）。
/// </summary>
public sealed class AutoSwitchEngine
{
    private const int ResumeGapSeconds = 300;

    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;
    private readonly IAudioSwitcher _switcher;
    private readonly IClock _clock;
    private readonly IBudsProbe _probe;
    private DateTime? _lastTickUtc;

    // 次の判定を合わせ直し（同じ状態でも 1 回切り替える）にする。起動時と、読めない状態のあとに立つ
    private bool _resyncPending = true;

    // 読めない状態のログを書いた（戻るまで同じ Warn を繰り返さない）
    private bool _unavailable;

    public AutoSwitchEngine(AppSettings settings, IAppLogger logger, IAudioSwitcher switcher, IClock clock, IBudsProbe probe)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(switcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(probe);

        _settings = settings;
        _logger = logger;
        _switcher = switcher;
        _clock = clock;
        _probe = probe;
        NextDelaySeconds = settings.PollIntervalSeconds;
        Settings = new LiveSettings(settings);
    }

    /// <summary>
    /// 動作中の設定。入れ替えると、次に始まる切り替えから6項目の新しい値を使う。
    /// ポーリング間隔や起動時の切り替えの有無など、6項目以外はコンストラクタで受け取った値を使い続ける
    /// </summary>
    public LiveSettings Settings { get; }

    public HeadsetState State { get; private set; } = HeadsetState.Unknown;

    /// <summary>状態が実際に変わったときだけ発火する。監視スレッドから呼ばれる</summary>
    public event EventHandler<HeadsetState>? StateChanged;

    /// <summary>ドングルへの問い合わせを始める直前に、監視スレッドから発火する</summary>
    public event Action? QueryStarting;

    /// <summary>問い合わせを終えるたびに（取り消しの例外で終わったときを除く）、監視スレッドから発火する</summary>
    public event Action<ProbeStatus>? QueryCompleted;

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

    public int NextDelaySeconds { get; private set; }

    public void StartupSync() => StartupSync(CancellationToken.None);

    public void StartupSync(CancellationToken token)
    {
        _lastTickUtc = _clock.UtcNow;
        Evaluate(token);
    }

    public void Tick() => Tick(CancellationToken.None);

    public void Tick(CancellationToken token)
    {
        var nowUtc = _clock.UtcNow;
        if (_lastTickUtc is not null && (nowUtc - _lastTickUtc.Value).TotalSeconds > ResumeGapSeconds)
        {
            _logger.Write(LogLevel.Info, "長時間のギャップを検知しました（スリープ復帰とみなします）。監視を通常間隔へ戻します。");
        }
        _lastTickUtc = nowUtc;

        Evaluate(token);
    }

    // 起動時と Tick に共通の手順: 問い合わせて、読めなければ待つ間隔だけを変え、読めれば合わせ直すか変化を見る
    private void Evaluate(CancellationToken token)
    {
        var result = Query(token);

        if (result.Status == ProbeStatus.Unavailable)
        {
            if (!_unavailable)
            {
                _logger.Write(LogLevel.Warn, $"INZONE Buds のドングルを読めません。{_settings.IdlePollIntervalSeconds} 秒おきに探し直します: {result.Error}");
                _unavailable = true;
            }

            // 抜いている間に Windows が既定のデバイスを替えるので、戻ったら差ではなく今の状態に合わせる
            _resyncPending = true;
            NextDelaySeconds = _settings.IdlePollIntervalSeconds;
            return;
        }

        NextDelaySeconds = _settings.PollIntervalSeconds;
        if (_unavailable)
        {
            _logger.Write(LogLevel.Info, "INZONE Buds のドングルを読めるようになりました。");
            _unavailable = false;
        }

        var observed = result.Status == ProbeStatus.Connected ? HeadsetState.Connected : HeadsetState.Disconnected;

        if (_resyncPending)
        {
            _resyncPending = false;
            if (token.IsCancellationRequested)
            {
                _logger.Write(LogLevel.Info, "終了が要求されたため、切り替えを行いません。");
                return;
            }

            SetState(observed);
            if (_settings.StartupSyncMaxAgeMinutes <= 0)
            {
                _logger.Write(LogLevel.Info, $"現在の状態を {observed} と判定しました。startupSyncMaxAgeMinutes が 0 のため切り替えません。");
                return;
            }

            _logger.Write(LogLevel.Info, $"現在の状態を {observed} と判定しました。");
            SwitchFor(observed, token);
            return;
        }

        if (token.IsCancellationRequested)
        {
            _logger.Write(LogLevel.Info, "終了が要求されたため、切り替えを行いません。");
            return;
        }

        var (changed, newState) = StateTransition.Resolve(State, observed);
        if (!changed)
        {
            _logger.Write(LogLevel.Debug, $"状態は {observed} のままです。切り替えません。");
            return;
        }

        SetState(newState);
        _logger.Write(LogLevel.Info, $"状態遷移を検知しました: {newState}");
        SwitchFor(newState, token);
    }

    // 問い合わせの前後でイベントを発火する。取り消しの例外は QueryCompleted を発火せずにそのまま外へ出す
    private ProbeResult Query(CancellationToken token)
    {
        try
        {
            QueryStarting?.Invoke();
        }
        catch (Exception ex)
        {
            // 知らせの扱いの失敗で監視と切り替えを止めない
            _logger.Write(LogLevel.Error, $"問い合わせ開始の通知で予期しない例外が発生しました: {ex.Message}");
        }

        var result = _probe.Query(token);
        _logger.Write(LogLevel.Debug, $"ドングルの問い合わせ: {result.Status}");

        try
        {
            QueryCompleted?.Invoke(result.Status);
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Error, $"問い合わせ完了の通知で予期しない例外が発生しました: {ex.Message}");
        }

        return result;
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
