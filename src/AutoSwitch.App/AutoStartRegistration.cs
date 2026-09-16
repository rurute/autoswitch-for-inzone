using Microsoft.Win32;

namespace AutoSwitch.App;

/// <summary>
/// ログオン時の自動起動を HKCU の Run キーで登録・解除する。管理者権限は要らない。
/// ON/OFF の判定は値の有無だけを見る。値に入っているパスが現在の exe と一致するかは見ないし、
/// 直しもしない（spec §4「移動検知・自動修復は作らない」）。
/// </summary>
internal sealed class AutoStartRegistration
{
    /// <summary>本番の Run キー。HKEY_CURRENT_USER からの相対パス</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>値名。旧版のスケジュールタスク名 'INZONE Auto Switch' とは別にする</summary>
    public const string ValueName = "AutoSwitchForInzone";

    private readonly string _exePath;

    public AutoStartRegistration(string exePath)
        : this(exePath, RunKeyPath)
    {
    }

    public AutoStartRegistration(string exePath, string keyPath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("exePath が空です。", nameof(exePath));
        }

        if (string.IsNullOrWhiteSpace(keyPath))
        {
            throw new ArgumentException("keyPath が空です。", nameof(keyPath));
        }

        _exePath = exePath;
        KeyPath = keyPath;
    }

    /// <summary>HKEY_CURRENT_USER からの相対パス</summary>
    public string KeyPath { get; }

    /// <summary>登録する値。パスに空白が入るので引用符で囲む。引数は付けない</summary>
    public string CommandLine => $"\"{_exePath}\"";

    /// <summary>値があれば true。値の中身は見ない</summary>
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    /// <summary>現在の exe のパスで登録する。既に値があっても上書きする</summary>
    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException($@"レジストリキーを開けません: HKCU\{KeyPath}");
        key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
    }

    /// <summary>登録を消す。値もキーも無ければ何もしない</summary>
    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
