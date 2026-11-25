namespace AutoSwitch.Core.Tests;

public class BudsProtocolTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    // battery-hub の spec の実測値（TID=1、左 99 / 右 99 / ケース 96）
    private const string ReferenceReply = "021204ff0f0096c3140410010000630063ff60a7";

    // 2026-11-25 の記録（Hub の返答、TID=1、左 100 / 右 100 / ケース 100）
    private const string HubReplyCaptured = "021204ff0f0096c3140410010000640064ff64ad";

    // 2026-11-25 の記録（知らせ）
    private const string NotificationEvent01 = "020e04ff0b0096c31201a0000000010d";
    private const string NotificationEvent41 = "021004ff0d0096c31441a001000014ff0062";

    [Fact]
    public void BuildBatteryRequest_Tid1_MatchesReference()
    {
        var r = BudsProtocol.BuildBatteryRequest(1);

        Assert.Equal(64, r.Length);
        Assert.Equal(BudsProtocol.RequestLength, r.Length);
        Assert.Equal(Hex("020c0100fc0896c34104010100a0"), r[..14]);
        Assert.All(r[14..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildBatteryRequest_Tid8000_PutsLowByteFirst()
    {
        var r = BudsProtocol.BuildBatteryRequest(0x8000);

        Assert.Equal(0x00, r[11]);
        Assert.Equal(0x80, r[12]);
        Assert.Equal((byte)((0x96 + 0xC3 + 0x41 + 0x04 + 0x01 + 0x00 + 0x80) & 0xFF), r[13]);
        Assert.All(r[14..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void TryParseBatteryReply_BatteryHubReference_Reads99And99And96()
    {
        Assert.True(BudsProtocol.TryParseBatteryReply(Hex(ReferenceReply), 1, out var l));
        Assert.Equal(new BudsLevels(99, 99, 96), l);
    }

    [Fact]
    public void TryParseBatteryReply_HubReplyCaptured_Reads100s()
    {
        Assert.True(BudsProtocol.TryParseBatteryReply(Hex(HubReplyCaptured), 1, out var l));
        Assert.Equal(new BudsLevels(100, 100, 100), l);
    }

    [Fact]
    public void TryParseBatteryReply_LeftNotReported_LeftNull()
    {
        // 参照フレームの [14] を 0xFF にし、[19] を [5..18] の合計の下位 1 バイト（0x43）に直したもの
        Assert.True(BudsProtocol.TryParseBatteryReply(Hex("021204ff0f0096c3140410010000ff0063ff6043"), 1, out var l));
        Assert.Equal(new BudsLevels(null, 99, 96), l);
    }

    [Fact]
    public void TryParseBatteryReply_LevelAbove100_TreatedAsNotReported()
    {
        // 参照フレームの [16] を 0x65（101）にし、[19] を直したもの（0xA9）
        Assert.True(BudsProtocol.TryParseBatteryReply(Hex("021204ff0f0096c3140410010000630065ff60a9"), 1, out var l));
        Assert.Equal(new BudsLevels(99, null, 96), l);
    }

    [Fact]
    public void TryParseBatteryReply_LongerFrame_ReadsFirst20Bytes()
    {
        // HID の入力レポートは 64 バイトで届く。20 バイトより後ろの 0 は照合に関係しない
        var frame = new byte[64];
        Hex(ReferenceReply).CopyTo(frame, 0);

        Assert.True(BudsProtocol.TryParseBatteryReply(frame, 1, out var l));
        Assert.Equal(new BudsLevels(99, 99, 96), l);
    }

    [Theory]
    // 参照フレームを別の TID で照合（下位バイト違い / 下位は同じで上位バイト違い）
    [InlineData(ReferenceReply, (ushort)2)]
    [InlineData(ReferenceReply, (ushort)0x0101)]
    // 長さ 19 に切ったもの
    [InlineData("021204ff0f0096c3140410010000630063ff60", (ushort)1)]
    // [0] = 0x01
    [InlineData("011204ff0f0096c3140410010000630063ff60a7", (ushort)1)]
    // [9] = 0x05（checksum は直してある。照合の条件そのもので落ちることを確かめる）
    [InlineData("021204ff0f0096c3140510010000630063ff60a8", (ushort)1)]
    // [10] = 0x20（checksum は直してある）
    [InlineData("021204ff0f0096c3140420010000630063ff60b7", (ushort)1)]
    // [19] を 1 ずらしたもの
    [InlineData("021204ff0f0096c3140410010000630063ff60a8", (ushort)1)]
    // 知らせ
    [InlineData(NotificationEvent01, (ushort)1)]
    [InlineData(NotificationEvent41, (ushort)1)]
    // 空
    [InlineData("", (ushort)1)]
    public void TryParseBatteryReply_Rejects(string hex, ushort tid)
    {
        Assert.False(BudsProtocol.TryParseBatteryReply(Hex(hex), tid, out _));
    }

    [Fact]
    public void IsNotification_CapturedNotifications_True()
    {
        Assert.True(BudsProtocol.IsNotification(Hex(NotificationEvent01)));
        Assert.True(BudsProtocol.IsNotification(Hex(NotificationEvent41)));
        Assert.True(BudsProtocol.IsNotification(Hex("020e04ff0b0096c31201a0000000000c")));
    }

    [Fact]
    public void IsNotification_ReplyOrShort_False()
    {
        Assert.False(BudsProtocol.IsNotification(Hex(ReferenceReply)));
        Assert.False(BudsProtocol.IsNotification(Hex(NotificationEvent01)[..10]));
        Assert.False(BudsProtocol.IsNotification([]));
    }

    [Fact]
    public void IsNotification_OtherReportId_False()
    {
        var frame = Hex(NotificationEvent01);
        frame[0] = 0x01;

        Assert.False(BudsProtocol.IsNotification(frame));
    }

    [Fact]
    public void DescribeNotification_ShowsHeaderAndHidesPayload()
    {
        // event 0x02 の知らせ。13 バイト目より先に ASCII 数字列（ここでは架空の "1234567"）が入る
        var frame = Hex("022c04ff290096c31402a00100" + "0403ffffff00" + "31323334353637" + "00");

        var text = BudsProtocol.DescribeNotification(frame);

        Assert.Equal("head=022c04ff290096c31402a00100 payload=hidden", text);
        Assert.DoesNotContain("31323334", text);
    }

    [Fact]
    public void DescribeNotification_ShortFrame_ShowsWhatExists()
    {
        Assert.Equal("head=021004ff0d0096c31441a0 payload=hidden", BudsProtocol.DescribeNotification(Hex("021004ff0d0096c31441a0")));
    }

    [Fact]
    public void Classify_Cases()
    {
        Assert.Equal(HeadsetState.Connected, BudsProtocol.Classify(new BudsLevels(99, 99, 96)));
        Assert.Equal(HeadsetState.Connected, BudsProtocol.Classify(new BudsLevels(null, 100, 96)));
        Assert.Equal(HeadsetState.Connected, BudsProtocol.Classify(new BudsLevels(100, null, 96)));
        Assert.Equal(HeadsetState.Connected, BudsProtocol.Classify(new BudsLevels(0, null, null)));
        Assert.Equal(HeadsetState.Disconnected, BudsProtocol.Classify(new BudsLevels(null, null, 96)));
        Assert.Equal(HeadsetState.Disconnected, BudsProtocol.Classify(new BudsLevels(null, null, null)));
    }

    [Fact]
    public void TidSequence_StartsAt8000AndWrapsTo1()
    {
        var s = new TidSequence(0xFFFE);
        Assert.Equal(0xFFFE, s.Next());
        Assert.Equal(0xFFFF, s.Next());
        Assert.Equal(1, s.Next());
        Assert.Equal(2, s.Next());

        var d = new TidSequence();
        Assert.Equal(0x8000, d.Next());
        Assert.Equal(0x8001, d.Next());
    }
}
