namespace AutoSwitch.Core;

/// <summary>
/// 設定画面で扱う7項目（Issue rurute/inzone-auto-switch#9。7項目目の ShowTaskbarBattery は Issue rurute/inzone-auto-switch#27）。保存と再起動なしの反映はこの7項目に限る。
/// 録音の2項目は空で未設定（AppSettings と同じ）。ShowTaskbarBattery は既存の呼び出しを壊さないよう省略可能な末尾の引数にする。
/// </summary>
public sealed record SwitchSettings(
    string BudsDevice, string SpeakerDevice, string BudsInputDevice, string SpeakerInputDevice,
    bool SwitchOnConnect, bool SwitchOnDisconnect, bool ShowTaskbarBattery = false)
{
    public static SwitchSettings From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SwitchSettings(
            settings.BudsDevice, settings.SpeakerDevice, settings.BudsInputDevice, settings.SpeakerInputDevice,
            settings.SwitchOnConnect, settings.SwitchOnDisconnect, settings.ShowTaskbarBattery);
    }

    /// <summary>running の7項目だけをこの値に置き換えた設定。7項目以外は running のまま</summary>
    public AppSettings ApplyTo(AppSettings running)
    {
        ArgumentNullException.ThrowIfNull(running);
        return running with
        {
            BudsDevice = BudsDevice,
            SpeakerDevice = SpeakerDevice,
            BudsInputDevice = BudsInputDevice,
            SpeakerInputDevice = SpeakerInputDevice,
            SwitchOnConnect = SwitchOnConnect,
            SwitchOnDisconnect = SwitchOnDisconnect,
            ShowTaskbarBattery = ShowTaskbarBattery,
        };
    }

    /// <summary>保存に成功したときのログの1行（spec §5）</summary>
    public string DescribeSaved() =>
        $"設定画面で保存しました: 接続時 再生={BudsDevice} 録音={DescribeCapture(BudsInputDevice)} / " +
        $"切断時 再生={SpeakerDevice} 録音={DescribeCapture(SpeakerInputDevice)} / " +
        $"switchOnConnect={(SwitchOnConnect ? "true" : "false")} switchOnDisconnect={(SwitchOnDisconnect ? "true" : "false")} " +
        $"showTaskbarBattery={(ShowTaskbarBattery ? "true" : "false")}";

    private static string DescribeCapture(string deviceName) =>
        string.IsNullOrWhiteSpace(deviceName) ? "（未設定）" : deviceName;
}
