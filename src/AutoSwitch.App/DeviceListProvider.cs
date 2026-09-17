using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// 設定画面の選択肢にする、有効なデバイスの名前の一覧を流れごとに取る（Issue rurute/inzone-auto-switch#9、spec §7）。
/// 呼んだスレッドでセッションを開いて閉じ、監視スレッドのセッションとは共有しない。失敗は理由の文字列にして返し、例外を外へ出さない。
/// </summary>
internal static class DeviceListProvider
{
    public static (DeviceNameList Playback, DeviceNameList Capture) Load()
    {
        CoreAudioSession session;
        try
        {
            session = new CoreAudioSession();
        }
        catch (Exception ex)
        {
            var unavailable = DeviceNameList.Unavailable(ex.Message);
            return (unavailable, unavailable);
        }

        try
        {
            return (Read(session, DeviceFlow.Playback), Read(session, DeviceFlow.Capture));
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

    private static DeviceNameList Read(IAudioSession session, DeviceFlow flow)
    {
        try
        {
            return DeviceNameList.Available(session.GetDevices(flow).Select(device => device.Name));
        }
        catch (Exception ex)
        {
            return DeviceNameList.Unavailable(ex.Message);
        }
    }
}
