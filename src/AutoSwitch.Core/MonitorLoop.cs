namespace AutoSwitch.Core;

public interface IWaiter
{
    /// <summary>duration だけ待つ。取り消されたら false</summary>
    bool Wait(TimeSpan duration, CancellationToken token);
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
                // 固定 5 秒待つのは、待機自体が失敗し続けた場合に高頻度ループへ落ちないため
                logger.Write(LogLevel.Error, $"予期しない例外が発生しました: {ex.Message}");
                useIdleDelay = true;
                try
                {
                    if (!waiter.Wait(RecoveryDelay, token))
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
