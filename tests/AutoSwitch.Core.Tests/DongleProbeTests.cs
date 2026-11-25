namespace AutoSwitch.Core.Tests;

public class DongleProbeTests
{
    // 2026-11-25 の記録（知らせ）
    private const string NotificationEvent01 = "020e04ff0b0096c31201a0000000010d";

    private static readonly TimeSpan Timeout1000 = TimeSpan.FromMilliseconds(1000);

    private static ushort TidOf(byte[] request) => (ushort)(request[11] | (request[12] << 8));

    // 問い合わせへの返答（Task 1 の参照フレームと同じ形）。0xFF は報告なし。[19] は [5..18] の合計の下位 1 バイト
    private static byte[] ReplyWithTid(ushort tid, int left, int right, int @case)
    {
        // [13] LS, [14] 左, [15] RS, [16] 右, [17] CS, [18] ケース
        var frame = new byte[]
        {
            0x02, 0x12, 0x04, 0xFF, 0x0F, 0x00, 0x96, 0xC3, 0x14, 0x04, 0x10,
            (byte)(tid & 0xFF), (byte)(tid >> 8),
            0x00, (byte)left, 0x00, (byte)right, 0xFF, (byte)@case,
            0x00,
        };
        var sum = 0;
        for (var i = 5; i <= 18; i++) sum += frame[i];
        frame[19] = (byte)(sum & 0xFF);
        return frame;
    }

    private static byte[] Reply(byte[] request, int left, int right, int @case) =>
        ReplyWithTid(TidOf(request), left, right, @case);

    private static FakeBudsTransport TransportWith(params Func<byte[], byte[][]?>[] replies)
    {
        var transport = new FakeBudsTransport();
        foreach (var reply in replies)
        {
            transport.Replies.Enqueue(reply);
        }
        return transport;
    }

    [Fact]
    public void ReplyHelper_TidOneAnd99And99And96_MatchesBatteryHubReference()
    {
        // ヘルパーが Task 1 の参照フレーム（battery-hub の spec の実測値）と同じバイト列を組むこと
        Assert.Equal(Convert.FromHexString("021204ff0f0096c3140410010000630063ff60a7"), ReplyWithTid(1, 99, 99, 96));
    }

    [Fact]
    public void Constants_MatchPlan()
    {
        Assert.Equal(Timeout1000, DongleProbe.ReplyTimeout);
        Assert.Equal(2, DongleProbe.Attempts);
    }

    [Fact]
    public void Query_BothEarbudsReport_Connected()
    {
        var transport = TransportWith(req => [Reply(req, 100, 100, 100)]);

        var result = new DongleProbe(transport).Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Connected, result.Status);
        Assert.Equal(new BudsLevels(100, 100, 100), result.Levels);
        Assert.Null(result.Error);
        Assert.Single(transport.Writes);
        Assert.Equal(BudsProtocol.BuildBatteryRequest(0x8000), transport.Writes[0]);
        Assert.Equal([Timeout1000], transport.Timeouts);
    }

    [Fact]
    public void Query_OneEarbudReports_Connected()
    {
        var transport = TransportWith(req => [Reply(req, 0xFF, 100, 100)]);

        var result = new DongleProbe(transport).Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Connected, result.Status);
        Assert.Equal(new BudsLevels(null, 100, 100), result.Levels);
    }

    [Fact]
    public void Query_NeitherEarbudReports_Disconnected()
    {
        var transport = TransportWith(req => [Reply(req, 0xFF, 0xFF, 96)]);

        var result = new DongleProbe(transport).Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Disconnected, result.Status);
        Assert.Equal(new BudsLevels(null, null, 96), result.Levels);
        Assert.Single(transport.Writes);
        Assert.Equal(0, transport.CloseCount);
    }

    [Fact]
    public void Query_FirstTimesOut_RetriesWithNewTid()
    {
        var transport = TransportWith(_ => null, req => [Reply(req, 99, 99, 96)]);

        var result = new DongleProbe(transport).Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Connected, result.Status);
        Assert.Equal(new BudsLevels(99, 99, 96), result.Levels);
        Assert.Equal(2, transport.Writes.Count);
        Assert.NotEqual(TidOf(transport.Writes[0]), TidOf(transport.Writes[1]));
        Assert.Equal(0x8000, TidOf(transport.Writes[0]));
        Assert.Equal(0x8001, TidOf(transport.Writes[1]));
        Assert.Equal([Timeout1000, Timeout1000], transport.Timeouts);
        Assert.Equal(0, transport.CloseCount);
    }

    [Fact]
    public void Query_FirstReplyHasOldTidOnSecondAttempt_IgnoresIt()
    {
        // 1 回目の返答が 2 回目の問い合わせのあとに遅れて届いても、2 回目の TID の返答だけを使う
        var transport = TransportWith(
            _ => null,
            req => [ReplyWithTid((ushort)(TidOf(req) - 1), 0xFF, 0xFF, 96), Reply(req, 99, 99, 96)]);

        var result = new DongleProbe(transport).Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Connected, result.Status);
        Assert.Equal(new BudsLevels(99, 99, 96), result.Levels);
    }

    [Fact]
    public void Query_BothTimeOut_Disconnected()
    {
        var transport = TransportWith(_ => null, _ => null);

        var result = new DongleProbe(transport).Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Disconnected, result.Status);
        Assert.Null(result.Levels);
        Assert.Null(result.Error);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal([Timeout1000, Timeout1000], transport.Timeouts);
        Assert.Equal(0, transport.CloseCount);
    }

    [Fact]
    public void Query_OtherFramesBeforeReply_UsesOwnReply()
    {
        var transport = TransportWith(req =>
        [
            ReplyWithTid((ushort)(TidOf(req) + 1), 0xFF, 0xFF, 96),
            Convert.FromHexString(NotificationEvent01),
            Reply(req, 99, 99, 96),
        ]);

        var result = new DongleProbe(transport).Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Connected, result.Status);
        Assert.Equal(new BudsLevels(99, 99, 96), result.Levels);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public void Query_OpenFails_UnavailableWithError()
    {
        var transport = new FakeBudsTransport { OpenError = "not found" };
        transport.OpenResults.Enqueue(false);

        var result = new DongleProbe(transport).Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Unavailable, result.Status);
        Assert.Equal("not found", result.Error);
        Assert.Null(result.Levels);
        Assert.Empty(transport.Writes);
        Assert.Empty(transport.Timeouts);
    }

    [Fact]
    public void Query_WriteFails_ClosesAndReturnsUnavailable()
    {
        var transport = new FakeBudsTransport { WriteException = new IOException("gone") };

        var result = new DongleProbe(transport).Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Unavailable, result.Status);
        Assert.Contains("gone", result.Error);
        Assert.Null(result.Levels);
        Assert.Equal(1, transport.CloseCount);
        Assert.Single(transport.Writes);
        Assert.Empty(transport.Timeouts);
    }

    [Fact]
    public void Query_ReadFails_ClosesAndReturnsUnavailable()
    {
        var transport = new FakeBudsTransport { ReadException = new IOException("gone") };

        var result = new DongleProbe(transport).Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Unavailable, result.Status);
        Assert.Contains("gone", result.Error);
        Assert.Null(result.Levels);
        Assert.Equal(1, transport.CloseCount);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public void Query_SecondWriteFails_ClosesAndReturnsUnavailable()
    {
        // 1 回目が時間切れのあと、2 回目の書き込みでドングルが抜けていたら、切断ではなく読めないとする
        var transport = new FakeBudsTransport();
        transport.Replies.Enqueue(_ =>
        {
            transport.WriteException = new IOException("gone");
            return null;
        });
        var probe = new DongleProbe(transport);

        var result = probe.Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Unavailable, result.Status);
        Assert.Contains("gone", result.Error);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(1, transport.CloseCount);
    }

    [Fact]
    public void Query_AfterUnavailable_OpensAgainAndQueries()
    {
        // 読めなかった次の問い合わせでは開き直して問い合わせる（ハンドルを閉じたままにしない）
        var transport = new FakeBudsTransport { WriteException = new IOException("gone") };
        var probe = new DongleProbe(transport);
        Assert.Equal(ProbeStatus.Unavailable, probe.Query(CancellationToken.None).Status);

        transport.WriteException = null;
        transport.Replies.Enqueue(req => [Reply(req, 99, 99, 96)]);
        var result = probe.Query(CancellationToken.None);

        Assert.Equal(ProbeStatus.Connected, result.Status);
        Assert.Equal(2, transport.Writes.Count);
        Assert.NotEqual(TidOf(transport.Writes[0]), TidOf(transport.Writes[1]));
    }

    [Fact]
    public void Query_UsesGivenTidSequence()
    {
        var transport = TransportWith(req => [Reply(req, 99, 99, 96)]);

        new DongleProbe(transport, new TidSequence(0xFFFF)).Query(CancellationToken.None);

        Assert.Equal(0xFFFF, TidOf(transport.Writes[0]));
    }

    [Fact]
    public void Query_Cancelled_Throws()
    {
        var transport = TransportWith(req => [Reply(req, 99, 99, 96)]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new DongleProbe(transport).Query(cts.Token));
        Assert.Equal(0, transport.CloseCount);
    }
}
