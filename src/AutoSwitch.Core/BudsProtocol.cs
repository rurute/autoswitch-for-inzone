namespace AutoSwitch.Core;

// 左・右・ケースの残量（0〜100）。報告なし（0xFF、または 100 を超える値）は null
public sealed record BudsLevels(int? Left, int? Right, int? Case);

// INZONE Buds の USB ドングル（054c:0ec2）の COL03（Usage Page 0xFF04 / Usage 0x0001）で話す
// バッテリーの問い合わせと返答。battery-hub の src/main/hid/drivers/inzone.js と同じ形。
//
// 問い合わせ（report 0x02、64 バイト、足りない分は 0）:
//   02 0C | 01 00 FC 08 96 C3 41 04 01 <TID lo> <TID hi> <checksum>
//   checksum は [6..12] の合計の下位 1 バイト
// 返答:
//   02 12 04 FF 0F 00 96 C3 14 04 10 <TID lo> <TID hi> | LS LL RS RR CS CC | checksum
//   04 = バッテリーの event、10 = 問い合わせへの返答。checksum は [5..18] の合計の下位 1 バイト
// 知らせ: [10] が 0xA0 のフレーム（ドングルが状態の変化で自分から送る）
//
// Hub や battery-hub も同じ collection で話すので、event・type・TID・checksum がすべて合うときだけ
// 自分の返答とみなす。TID が重なって他のソフトの返答を取り違えても、同じ Buds の残量なので判定は変わらない。
public static class BudsProtocol
{
    public const int RequestLength = 64;

    private const byte ReportId = 0x02;
    private const int ReplyLength = 20;
    private const byte BatteryEvent = 0x04;
    private const byte GetAnswer = 0x10;
    private const byte NotificationType = 0xA0;
    private const int MaxLevel = 100;

    private static readonly byte[] RequestHead = [ReportId, 0x0C, 0x01, 0x00, 0xFC, 0x08, 0x96, 0xC3, 0x41, BatteryEvent, 0x01];

    public static byte[] BuildBatteryRequest(ushort tid)
    {
        var request = new byte[RequestLength];
        RequestHead.CopyTo(request, 0);
        request[11] = (byte)(tid & 0xFF);
        request[12] = (byte)(tid >> 8);
        request[13] = LowByteOfSum(request.AsSpan(6, 7));
        return request;
    }

    public static bool TryParseBatteryReply(ReadOnlySpan<byte> frame, ushort tid, out BudsLevels levels)
    {
        levels = new BudsLevels(null, null, null);
        if (frame.Length < ReplyLength || frame[0] != ReportId) return false;
        if (frame[9] != BatteryEvent || frame[10] != GetAnswer) return false;
        if (frame[11] != (byte)(tid & 0xFF) || frame[12] != (byte)(tid >> 8)) return false;
        if (frame[19] != LowByteOfSum(frame[5..19])) return false;

        levels = new BudsLevels(Level(frame[14]), Level(frame[16]), Level(frame[18]));
        return true;
    }

    public static bool IsNotification(ReadOnlySpan<byte> frame) =>
        frame.Length >= 11 && frame[0] == ReportId && frame[10] == NotificationType;

    // 知らせを表示するときの文字列。先頭 13 バイト（レポート id・長さ・宛先・event・type・TID）だけを
    // 16 進で返し、その先（payload）は伏せる。event 0x02 の payload には識別子らしき数字列が入るため
    public const int NotificationHeaderLength = 13;

    public static string DescribeNotification(ReadOnlySpan<byte> frame)
    {
        var head = frame[..Math.Min(frame.Length, NotificationHeaderLength)];
        return $"head={Convert.ToHexString(head).ToLowerInvariant()} payload=hidden";
    }

    // 左右どちらかに残量があれば接続中（片耳だけケースに戻したときも接続中のまま）
    public static HeadsetState Classify(BudsLevels levels) =>
        levels.Left is not null || levels.Right is not null ? HeadsetState.Connected : HeadsetState.Disconnected;

    private static int? Level(byte value) => value <= MaxLevel ? value : null;

    private static byte LowByteOfSum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes) sum += b;
        return (byte)(sum & 0xFF);
    }
}

// 問い合わせの TID の通し番号。最初は 0x8000（Hub の小さな値からも、battery-hub の 0x4000 からも離す）。
// 1 ずつ増やし、0xFFFF の次は 1（0 は使わない）。1 本のスレッドから呼ぶ前提で、ロックしない
public sealed class TidSequence
{
    private ushort _next;

    public TidSequence(ushort first = 0x8000)
    {
        _next = first;
    }

    public ushort Next()
    {
        var tid = _next;
        _next = tid == 0xFFFF ? (ushort)1 : (ushort)(tid + 1);
        return tid;
    }
}
