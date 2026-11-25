using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// 新版の保存先。旧版のフォルダとは分け、spec §4 の `%LOCALAPPDATA%\AutoSwitchForInzone` に置く。
/// </summary>
internal static class AppPaths
{
    public static string AppRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSwitchForInzone");

    public static string ConfigPath { get; } = Path.Combine(AppRoot, "config.json");

    public static string LogFolder { get; } = Path.Combine(AppRoot, "logs");

    public static string LogPath { get; } = Path.Combine(LogFolder, "AutoSwitchForInzone.log");

    /// <summary>
    /// 例外のメッセージなど、パスが入りうる文字列をログに書く前に、ユーザー名を含むフォルダの部分を
    /// %LOCALAPPDATA% / %USERPROFILE% の形にする。製品のログ行そのものにはパスを書かない。
    /// </summary>
    public static string ForLog(string text) => LogPathMasker.Mask(
        text,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
