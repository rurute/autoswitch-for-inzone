namespace AutoSwitch.Core;

/// <summary>
/// ドングルの知らせ（type 0xA0 のフレーム）で監視ループの待機を起こす。返答（type 0x10）では起こさない。
/// 知らせのあとに始まった問い合わせの結果が Connected だったときだけ、FollowUpDelay 後にもう 1 回起こす
/// （知らせが届いてから返答が止まるまで約 1 秒あったため）。Connected 以外の結果が出たら、その予約を取り消す
/// （読めない間の探し直しの間隔を縮めないため）。有効な予約は常に多くとも 1 件。
/// どのメソッドもどのスレッドから呼んでもよい（読み取りスレッドとタイマーのスレッド）。wake の例外は外へ出さない
/// </summary>
public sealed class NotificationWaker : IDisposable
{
    public static readonly TimeSpan FollowUpDelay = TimeSpan.FromSeconds(1);

    private readonly Action _wake;
    private readonly Func<TimeSpan, Action, IDisposable> _scheduleOnce;

    // 下の状態と wake の呼び出しはこの lock の中で行う。Dispose が戻ったあとに wake が呼ばれないようにするため
    private readonly object _gate = new();

    // 知らせが来て、まだどの問い合わせにも引き渡していない
    private bool _pending;

    // 今の問い合わせは知らせのあとに始まった（結果が Connected なら追いかける）
    private bool _armed;

    // 予約済みの追いかけ。_followUpId は、取り消したあとに遅れて走った予約を見分けるために使う
    private IDisposable? _followUp;
    private long _followUpId;

    private bool _disposed;

    public NotificationWaker(Action wake, Func<TimeSpan, Action, IDisposable> scheduleOnce)
    {
        ArgumentNullException.ThrowIfNull(wake);
        ArgumentNullException.ThrowIfNull(scheduleOnce);
        _wake = wake;
        _scheduleOnce = scheduleOnce;
    }

    /// <summary>ドングルから届いたフレーム。知らせなら、予約済みの追いかけを取り消し、印を立てて起こす</summary>
    public void OnFrame(byte[] frame)
    {
        if (!BudsProtocol.IsNotification(frame))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CancelFollowUpLocked();
            _pending = true;
            WakeLocked();
        }
    }

    /// <summary>エンジンが問い合わせを始める直前に呼ぶ。知らせの印を、これから始まる問い合わせに引き渡す</summary>
    public void OnQueryStarting()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _armed = _pending;
            _pending = false;
        }
    }

    /// <summary>エンジンが問い合わせを終えるたびに呼ぶ</summary>
    public void OnQueryCompleted(ProbeStatus status)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (status != ProbeStatus.Connected)
            {
                CancelFollowUpLocked();
            }
            else if (_armed)
            {
                CancelFollowUpLocked();
                var id = ++_followUpId;
                _followUp = _scheduleOnce(FollowUpDelay, () => RunFollowUp(id));
            }

            // 追いかけの問い合わせの結果では、さらに追いかけない
            _armed = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelFollowUpLocked();
        }
    }

    private void RunFollowUp(long id)
    {
        lock (_gate)
        {
            // 取り消したあと・破棄したあとに遅れて走った予約は何もしない
            if (_disposed || id != _followUpId || _followUp is null)
            {
                return;
            }

            _followUp.Dispose();
            _followUp = null;
            WakeLocked();
        }
    }

    private void CancelFollowUpLocked()
    {
        if (_followUp is null)
        {
            return;
        }

        _followUp.Dispose();
        _followUp = null;
    }

    private void WakeLocked()
    {
        try
        {
            _wake();
        }
        catch (Exception)
        {
            // 読み取りスレッドやタイマーのスレッドを止めないため、外へ出さない
        }
    }
}
