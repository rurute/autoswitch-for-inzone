using System.Diagnostics;

namespace AutoSwitch.Core;

public interface IWaiter
{
    /// <summary>duration だけ待つ。取り消されたら false</summary>
    bool Wait(TimeSpan duration, CancellationToken token);

    /// <summary>起こされても途中で終わらずに duration だけ待つ。取り消されたら false。起こせない待機では Wait と同じ</summary>
    bool WaitIgnoringWake(TimeSpan duration, CancellationToken token) => Wait(duration, token);
}

public sealed class SystemWaiter : IWaiter
{
    // WaitHandle.WaitOne が受け付ける最大の待機時間（int.MaxValue ミリ秒）
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMilliseconds(int.MaxValue);

    public bool Wait(TimeSpan duration, CancellationToken token)
    {
        try
        {
            // 範囲外の待機時間で例外を出さないよう 0〜上限に収める
            var clamped = duration < TimeSpan.Zero ? TimeSpan.Zero : duration > MaxDuration ? MaxDuration : duration;
            return !token.WaitHandle.WaitOne(clamped);
        }
        catch (ObjectDisposedException)
        {
            // 取り消し元が破棄されていれば、それ以上待てないので取り消しと同じ扱いにする
            return false;
        }
    }
}

/// <summary>
/// 外から起こせる待機。ActionLog.log の変更通知で待機を打ち切り、すぐ次の Tick を行うために使う。
/// 起こされたのは 1 回分にまとめ、待機の外で起こされていれば次の Wait はすぐ終わる。
/// 起こされて終わるときも、前回の戻りから最小間隔を空ける（Hub がまとめて書く間に読み取りが続きすぎないため）
/// </summary>
public sealed class WakeableWaiter : IWaiter, IDisposable
{
    /// <summary>起こされたときの、前回の戻りからの最小間隔。計画の Task 1 の計測で初期値のまま決めた</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(100);

    // WaitHandle.WaitOne が受け付ける最大の待機時間（int.MaxValue ミリ秒）
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly AutoResetEvent _woken = new(false);
    private readonly TimeSpan _minInterval;
    private readonly Func<long> _getTimestamp;
    private volatile bool _disposed;

    // 前回 Wait / WaitIgnoringWake が戻った時刻（Stopwatch のタイムスタンプ）。まだ戻っていなければ null。
    // 待機は監視ループのスレッドだけが呼ぶので、ロックしない
    private long? _lastReturnTimestamp;

    public WakeableWaiter()
        : this(DefaultMinInterval)
    {
    }

    public WakeableWaiter(TimeSpan minInterval)
        : this(minInterval, Stopwatch.GetTimestamp)
    {
    }

    // テストから時刻の読み取り（Stopwatch のタイムスタンプ）を差し替える
    internal WakeableWaiter(TimeSpan minInterval, Func<long> getTimestamp)
    {
        _minInterval = minInterval;
        _getTimestamp = getTimestamp;
    }

    /// <summary>待機を起こす。どのスレッドからでも呼べる。破棄後に呼んでも例外を出さない</summary>
    public void Wake()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _woken.Set();
        }
        catch (ObjectDisposedException)
        {
            // 終了と変更通知が重なって破棄後に届いたもの。起こす相手はもういない
        }
    }

    public bool Wait(TimeSpan duration, CancellationToken token)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            // 取り消しと起こすが重なったら、添え字の小さい取り消しが選ばれる
            var signaled = WaitHandle.WaitAny([token.WaitHandle, _woken], Clamp(duration));
            if (signaled == 0)
            {
                return Returned(false);
            }

            if (signaled == 1 && _lastReturnTimestamp is long last)
            {
                // 起こされたときだけ最小間隔を守る。この間も取り消しは効かせる。
                // WaitOne はミリ秒未満を切り捨てるので切り上げて待ち、待ったあとも経過を確かめ直す
                while (true)
                {
                    var remaining = _minInterval - Stopwatch.GetElapsedTime(last, _getTimestamp());
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    if (token.WaitHandle.WaitOne(RoundUpToMilliseconds(remaining)))
                    {
                        return Returned(false);
                    }
                }
            }

            return Returned(true);
        }
        catch (ObjectDisposedException)
        {
            // 取り消し元かこの待機が破棄されていれば、それ以上待てないので取り消しと同じ扱いにする
            return false;
        }
    }

    public bool WaitIgnoringWake(TimeSpan duration, CancellationToken token)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            // 起こされた印は消さずに残し、次の Wait をすぐ終わらせる
            return Returned(!token.WaitHandle.WaitOne(Clamp(duration)));
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _woken.Dispose();
    }

    // 範囲外の待機時間で例外を出さないよう 0〜上限に収める
    private static TimeSpan Clamp(TimeSpan duration) =>
        duration < TimeSpan.Zero ? TimeSpan.Zero : duration > MaxDuration ? MaxDuration : duration;

    // 正の待機時間をミリ秒単位に切り上げ、上限に収める
    private static TimeSpan RoundUpToMilliseconds(TimeSpan duration) =>
        duration >= MaxDuration ? MaxDuration : TimeSpan.FromMilliseconds(Math.Ceiling(duration.TotalMilliseconds));

    private bool Returned(bool result)
    {
        _lastReturnTimestamp = _getTimestamp();
        return result;
    }
}

/// <summary>
/// 待機と tick の繰り返し。手順とログの文言は旧版 INZONE-AutoSwitch.ps1 の監視ループに合わせる。
/// </summary>
public static class MonitorLoop
{
    private const int MinDelaySeconds = 1;
    private const int MaxDelaySeconds = 3600;
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(5);

    // token が取り消されるか Wait が false を返すまで、待機と Tick を繰り返す。StartupSync は呼ばない
    public static void Run(AutoSwitchEngine engine, AppSettings settings, IAppLogger logger, IWaiter waiter, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(waiter);

        // 旧版は例外時に context.NextDelaySeconds を idle 間隔へ書き換える。エンジンの値は外から変えられないため、ここで持つ
        var useIdleDelay = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var delaySeconds = useIdleDelay ? settings.IdlePollIntervalSeconds : engine.NextDelaySeconds;
                useIdleDelay = false;
                delaySeconds = Math.Clamp(delaySeconds, MinDelaySeconds, MaxDelaySeconds);
                if (!waiter.Wait(TimeSpan.FromSeconds(delaySeconds), token))
                {
                    return;
                }

                // 待機中に取り消されていたら、この tick は行わない
                if (token.IsCancellationRequested)
                {
                    return;
                }

                engine.Tick(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // tick も待機も原則として例外を投げない設計だが、万一漏れても常駐を止めない。
                // 固定 5 秒待つのは、待機自体が失敗し続けた場合に高頻度ループへ落ちないため。
                // 同じ理由で、変更通知で起こされてもこの待機は打ち切らない
                logger.Write(LogLevel.Error, $"予期しない例外が発生しました: {ex.Message}");
                useIdleDelay = true;
                try
                {
                    if (!waiter.WaitIgnoringWake(RecoveryDelay, token))
                    {
                        return;
                    }
                }
                catch (Exception)
                {
                    // 旧版は失敗しない Thread.Sleep で待つ。この待機の失敗ではループを止めない
                }
            }
        }
    }
}
