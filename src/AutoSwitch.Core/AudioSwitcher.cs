namespace AutoSwitch.Core;

/// <summary>音声デバイスの流れ。Playback = 再生、Capture = 録音。</summary>
public enum DeviceFlow { Playback, Capture }

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// 音声デバイスの一覧・既定の取得と変更。流れ（再生・録音）ごとに扱う。切り替えのたびに開いて破棄する。
/// </summary>
public interface IAudioSession : IDisposable
{
    IReadOnlyList<AudioDevice> GetDevices(DeviceFlow flow);

    AudioDevice? GetDefault(DeviceFlow flow);

    void SetDefault(DeviceFlow flow, string id);
}

public enum SwitchResult { Switched, AlreadyDefault, DeviceNotFound, Unavailable, Failed }

public sealed record SwitchOutcome(SwitchResult Result, string Message);

public interface IAudioSwitcher
{
    SwitchOutcome SwitchTo(DeviceFlow flow, string deviceName);
}

/// <summary>
/// 名前で既定の音声デバイスを切り替える。判定の順序は流れによらず同じで、再生の判定と文言は旧版 AudioSwitcher.psm1 の
/// Set-DefaultPlaybackDeviceByName に合わせる。録音の文言は再生の文言の「再生」を「録音」に置き換えたもの。
/// 音声操作の失敗は結果に変換し、例外を外へ出さない。
/// </summary>
public sealed class AudioSwitcher : IAudioSwitcher
{
    private readonly Func<IAudioSession> _openSession;

    public AudioSwitcher(Func<IAudioSession> openSession)
    {
        ArgumentNullException.ThrowIfNull(openSession);
        _openSession = openSession;
    }

    public SwitchOutcome SwitchTo(DeviceFlow flow, string deviceName)
    {
        // 範囲外の値はプログラムの誤りなので、結果に変換せず例外にする
        if (!Enum.IsDefined(flow))
        {
            throw new ArgumentOutOfRangeException(nameof(flow), flow, "未知の DeviceFlow です。");
        }

        IAudioSession session;
        try
        {
            session = _openSession();
        }
        catch (Exception ex)
        {
            // 旧版の ModuleUnavailable に相当する
            var message = flow == DeviceFlow.Playback
                ? $"音声デバイスを操作できません。切り替えを見送ります: {ex.Message}"
                : $"音声デバイスを操作できません。録音の切り替えを見送ります: {ex.Message}";
            return new SwitchOutcome(SwitchResult.Unavailable, message);
        }

        try
        {
            return SwitchCore(session, flow, deviceName);
        }
        finally
        {
            try
            {
                session.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    private static SwitchOutcome SwitchCore(IAudioSession session, DeviceFlow flow, string deviceName)
    {
        var kind = flow == DeviceFlow.Playback ? "再生" : "録音";

        // 既に目的のデバイスなら何もしない。取得に失敗した場合は判定せずに先へ進む
        try
        {
            var current = session.GetDefault(flow);
            if (current is not null && NameEquals(current.Name, deviceName))
            {
                return new SwitchOutcome(SwitchResult.AlreadyDefault, $"既に既定の{kind}デバイスです: {deviceName}");
            }
        }
        catch (Exception)
        {
        }

        try
        {
            // 同名の候補が複数あれば一覧の先頭を使う
            var target = session.GetDevices(flow).FirstOrDefault(d => NameEquals(d.Name, deviceName));
            if (target is null)
            {
                return new SwitchOutcome(SwitchResult.DeviceNotFound, $"{kind}デバイスが見つかりません: {deviceName}");
            }

            session.SetDefault(flow, target.Id);
            return new SwitchOutcome(SwitchResult.Switched, $"既定の{kind}デバイスを切り替えました: {deviceName}");
        }
        catch (Exception ex)
        {
            var message = flow == DeviceFlow.Playback
                ? $"切り替えに失敗しました ({deviceName}): {ex.Message}"
                : $"録音の切り替えに失敗しました ({deviceName}): {ex.Message}";
            return new SwitchOutcome(SwitchResult.Failed, message);
        }
    }

    // 旧版は -eq で比べるため、大文字小文字を区別しない
    private static bool NameEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
