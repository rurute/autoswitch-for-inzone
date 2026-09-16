namespace AutoSwitch.App;

/// <summary>
/// 新版の保存先。旧版のフォルダとは分け、spec §4 の `%LOCALAPPDATA%\AutoSwitchForInzone` に置く。
/// </summary>
internal static class AppPaths
{
    public static string AppRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSwitchForInzone");

    public static string ConfigPath { get; } = Path.Combine(AppRoot, "config.json");

    public static string LogPath { get; } = Path.Combine(AppRoot, "logs", "AutoSwitchForInzone.log");
}
