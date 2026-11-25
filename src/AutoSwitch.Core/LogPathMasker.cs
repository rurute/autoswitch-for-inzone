using System.Text.RegularExpressions;

namespace AutoSwitch.Core;

// ログに書く文字列から、ユーザー名を含むフォルダの部分を環境変数の形に置き換える。
// 例: C:\Users\<名前>\AppData\Local\AutoSwitchForInzone → %LOCALAPPDATA%\AutoSwitchForInzone
// 置き場所は分かるまま、ユーザー名だけがログに残らないようにする。
// LOCALAPPDATA を先に置き換え、その外に残ったユーザーフォルダを USERPROFILE で置き換える。
// 大文字小文字は区別しない。フォルダ名の途中（C:\Users\ab が C:\Users\abc に当たる）では置き換えない
public static class LogPathMasker
{
    public static string Mask(string text, string? localAppData, string? userProfile)
    {
        var masked = Replace(text, localAppData, "%LOCALAPPDATA%");
        return Replace(masked, userProfile, "%USERPROFILE%");
    }

    private static string Replace(string text, string? folder, string variable)
    {
        var root = folder?.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(root))
        {
            return text;
        }

        var pattern = Regex.Escape(root) + @"(?=[\\/""']|$|\s)";
        return Regex.Replace(text, pattern, variable.Replace("$", "$$"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
