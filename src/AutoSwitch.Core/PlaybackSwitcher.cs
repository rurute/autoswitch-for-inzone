namespace AutoSwitch.Core;

public sealed record PlaybackDevice(string Id, string Name);

/// <summary>
/// 再生デバイスの一覧・既定の取得と変更。切り替えのたびに開いて破棄する。
/// </summary>
public interface IPlaybackSession : IDisposable
{
    IReadOnlyList<PlaybackDevice> GetPlaybackDevices();

    PlaybackDevice? GetDefaultPlayback();

    void SetDefaultPlayback(string id);
}

public enum SwitchResult { Switched, AlreadyDefault, DeviceNotFound, Unavailable, Failed }

public sealed record SwitchOutcome(SwitchResult Result, string Message);

public interface IPlaybackSwitcher
{
    SwitchOutcome SwitchTo(string deviceName);
}

/// <summary>
/// 名前で既定の再生デバイスを切り替える。判定の順序と文言は旧版 AudioSwitcher.psm1 の Set-DefaultPlaybackDeviceByName に合わせる。
/// 例外を外へ出さない。
/// </summary>
public sealed class PlaybackSwitcher : IPlaybackSwitcher
{
    private readonly Func<IPlaybackSession> _openSession;

    public PlaybackSwitcher(Func<IPlaybackSession> openSession)
    {
        ArgumentNullException.ThrowIfNull(openSession);
        _openSession = openSession;
    }

    public SwitchOutcome SwitchTo(string deviceName)
    {
        IPlaybackSession session;
        try
        {
            session = _openSession();
        }
        catch (Exception ex)
        {
            // 旧版の ModuleUnavailable に相当する
            return new SwitchOutcome(SwitchResult.Unavailable, $"音声デバイスを操作できません。切り替えを見送ります: {ex.Message}");
        }

        try
        {
            return SwitchCore(session, deviceName);
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

    private static SwitchOutcome SwitchCore(IPlaybackSession session, string deviceName)
    {
        // 既に目的のデバイスなら何もしない。取得に失敗した場合は判定せずに先へ進む
        try
        {
            var current = session.GetDefaultPlayback();
            if (current is not null && NameEquals(current.Name, deviceName))
            {
                return new SwitchOutcome(SwitchResult.AlreadyDefault, $"既に既定の再生デバイスです: {deviceName}");
            }
        }
        catch (Exception)
        {
        }

        try
        {
            // 同名の候補が複数あれば一覧の先頭を使う
            var target = session.GetPlaybackDevices().FirstOrDefault(d => NameEquals(d.Name, deviceName));
            if (target is null)
            {
                return new SwitchOutcome(SwitchResult.DeviceNotFound, $"再生デバイスが見つかりません: {deviceName}");
            }

            session.SetDefaultPlayback(target.Id);
            return new SwitchOutcome(SwitchResult.Switched, $"既定の再生デバイスを切り替えました: {deviceName}");
        }
        catch (Exception ex)
        {
            return new SwitchOutcome(SwitchResult.Failed, $"切り替えに失敗しました ({deviceName}): {ex.Message}");
        }
    }

    // 旧版は -eq で比べるため、大文字小文字を区別しない
    private static bool NameEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
