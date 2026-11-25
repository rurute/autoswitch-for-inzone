namespace AutoSwitch.Core;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 設定値。既定値と整数の範囲は旧版 Config.psm1 に合わせる。ただし PollIntervalSeconds の既定は
/// ドングルへの問い合わせの間隔として 2 にする（Issue rurute/inzone-auto-switch#23）。旧版の actionLogPath は使わないので持たない。
/// BudsInputDevice / SpeakerInputDevice は録音の切り替え先（Issue rurute/inzone-auto-switch#8）。空は未設定で、その向きの録音を切り替えない。
/// 旧版に無い項目なので、既存の呼び出しを壊さないよう省略可能な末尾の引数にする。
/// </summary>
public sealed record AppSettings(
    string BudsDevice, string SpeakerDevice, bool SwitchOnConnect, bool SwitchOnDisconnect,
    int PollIntervalSeconds, int IdlePollIntervalSeconds,
    int StartupSyncMaxAgeMinutes, LogLevel LogLevel, int MaxLogSizeKB,
    string BudsInputDevice = "", string SpeakerInputDevice = "")
{
    public static AppSettings Default { get; } = new(
        BudsDevice: "スピーカー (INZONE Buds - Game)",
        SpeakerDevice: "スピーカー (Realtek(R) Audio)",
        SwitchOnConnect: true,
        SwitchOnDisconnect: true,
        PollIntervalSeconds: 2,
        IdlePollIntervalSeconds: 5,
        StartupSyncMaxAgeMinutes: 10,
        LogLevel: LogLevel.Info,
        MaxLogSizeKB: 1024,
        BudsInputDevice: "",
        SpeakerInputDevice: "");

    /// <summary>起動時のログに出す、録音の切り替え先の要約。未設定の向きは「（未設定）」</summary>
    public string DescribeCaptureSwitching() =>
        $"録音の切り替え: 接続時={DescribeTarget(BudsInputDevice)} / 切断時={DescribeTarget(SpeakerInputDevice)}";

    private static string DescribeTarget(string deviceName) =>
        string.IsNullOrWhiteSpace(deviceName) ? "（未設定）" : deviceName;

    internal static readonly (int Min, int Max) PollIntervalSecondsRange = (1, 60);
    internal static readonly (int Min, int Max) IdlePollIntervalSecondsRange = (1, 300);
    internal static readonly (int Min, int Max) StartupSyncMaxAgeMinutesRange = (0, 1440);
    internal static readonly (int Min, int Max) MaxLogSizeKBRange = (64, 102400);
}
