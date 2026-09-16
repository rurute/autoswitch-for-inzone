namespace AutoSwitch.Core;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 設定値。既定値と整数の範囲は旧版 Config.psm1 に合わせる。
/// </summary>
public sealed record AppSettings(
    string BudsDevice, string SpeakerDevice, bool SwitchOnConnect, bool SwitchOnDisconnect,
    string ActionLogPath, int PollIntervalSeconds, int IdlePollIntervalSeconds,
    int StartupSyncMaxAgeMinutes, LogLevel LogLevel, int MaxLogSizeKB)
{
    /// <summary>ActionLogPath は展開前の %APPDATA% 表記</summary>
    public static AppSettings Default { get; } = new(
        BudsDevice: "スピーカー (INZONE Buds - Game)",
        SpeakerDevice: "スピーカー (Realtek(R) Audio)",
        SwitchOnConnect: true,
        SwitchOnDisconnect: true,
        ActionLogPath: @"%APPDATA%\Sony\INZONE Hub\ActionLog.log",
        PollIntervalSeconds: 1,
        IdlePollIntervalSeconds: 5,
        StartupSyncMaxAgeMinutes: 10,
        LogLevel: LogLevel.Info,
        MaxLogSizeKB: 1024);

    internal static readonly (int Min, int Max) PollIntervalSecondsRange = (1, 60);
    internal static readonly (int Min, int Max) IdlePollIntervalSecondsRange = (1, 300);
    internal static readonly (int Min, int Max) StartupSyncMaxAgeMinutesRange = (0, 1440);
    internal static readonly (int Min, int Max) MaxLogSizeKBRange = (64, 102400);
}
