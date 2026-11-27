using System.Reflection;

namespace AutoSwitch.App;

/// <summary>
/// アプリのバージョン（Issue #2）。番号の元は Directory.Build.props の &lt;Version&gt;。
/// SDK は informational version の「+」の後ろに 40 文字のコミット SHA を付ける（git のないところでのビルドでは付かない）。
/// </summary>
internal static class AppVersion
{
    private const int ShortShaLength = 7;

    /// <summary>実行中のアセンブリの informational version。読めなければ「不明」</summary>
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "不明";

    /// <summary>ログ用。「+」の後ろのコミットの印を先頭 7 文字に切る（例: 0.1.0+adba3fe）</summary>
    public static string ForLog(string informational)
    {
        var plus = informational.IndexOf('+');
        if (plus < 0 || informational.Length - (plus + 1) <= ShortShaLength)
        {
            return informational;
        }
        return informational[..(plus + 1 + ShortShaLength)];
    }

    /// <summary>メニュー用。「+」より前だけ（例: 0.1.0）</summary>
    public static string ForDisplay(string informational)
    {
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
