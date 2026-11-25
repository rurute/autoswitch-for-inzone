namespace AutoSwitch.Core;

// INZONE Buds の USB ドングルの COL03 との通信。HID の実体（AutoSwitch.Hid）がこれを実装する
public interface IBudsTransport : IDisposable
{
    /// <summary>開いていれば何もせず true。見つからない・開けなければ false と理由</summary>
    bool TryOpen(out string error);

    /// <summary>問い合わせを書く。失敗は IOException</summary>
    void Write(byte[] report);

    /// <summary>
    /// timeout までに match を満たすフレームを返す。満たさないフレームは捨てる。時間切れは null。
    /// 読み取り側の失敗は IOException、取り消しは OperationCanceledException
    /// </summary>
    byte[]? WaitForFrame(Func<byte[], bool> match, TimeSpan timeout, CancellationToken token);

    /// <summary>ハンドルを閉じる。次の TryOpen で開き直す</summary>
    void Close();

    /// <summary>読んだフレームごとに読み取りスレッドから呼ばれる</summary>
    event Action<byte[]>? FrameReceived;
}

public enum ProbeStatus
{
    Connected,
    Disconnected,
    Unavailable,
}

// 問い合わせの結果。Levels は返答が来たときだけ、Error は Unavailable のときだけ入る
public sealed record ProbeResult(ProbeStatus Status, BudsLevels? Levels = null, string? Error = null);

// ドングルに問い合わせて、接続中・切断・読めない、を返す
public interface IBudsProbe
{
    ProbeResult Query(CancellationToken token);
}

// 問い合わせの手順: 開く → 問い合わせる → 自分の TID の返答を待つ。1000ms で時間切れなら新しい TID でもう 1 回だけ。
// 2 回とも時間切れなら切断。書き込み・読み取りの失敗はハンドルを閉じて Unavailable（次の Query で開き直す）。
// 取り消し（OperationCanceledException）は捕まえずに呼び出し元へ渡す。1 本のスレッドから呼ぶ前提
public sealed class DongleProbe : IBudsProbe
{
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(1000);
    public const int Attempts = 2;

    private readonly IBudsTransport _transport;
    private readonly TidSequence _tids;

    public DongleProbe(IBudsTransport transport, TidSequence? tids = null)
    {
        _transport = transport;
        _tids = tids ?? new TidSequence();
    }

    public ProbeResult Query(CancellationToken token)
    {
        if (!_transport.TryOpen(out var error))
        {
            return new ProbeResult(ProbeStatus.Unavailable, Error: error);
        }

        try
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                var tid = _tids.Next();
                _transport.Write(BudsProtocol.BuildBatteryRequest(tid));
                var frame = _transport.WaitForFrame(f => BudsProtocol.TryParseBatteryReply(f, tid, out _), ReplyTimeout, token);
                if (frame is not null && BudsProtocol.TryParseBatteryReply(frame, tid, out var levels))
                {
                    var status = BudsProtocol.Classify(levels) == HeadsetState.Connected
                        ? ProbeStatus.Connected
                        : ProbeStatus.Disconnected;
                    return new ProbeResult(status, levels);
                }
            }
            return new ProbeResult(ProbeStatus.Disconnected);
        }
        catch (IOException ex)
        {
            _transport.Close();
            return new ProbeResult(ProbeStatus.Unavailable, Error: ex.Message);
        }
    }
}
