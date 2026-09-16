using Microsoft.Win32;

namespace AutoSwitch.App.Tests;

/// <summary>
/// HKCU の使い捨てサブキーに対して登録・解除を往復させる。
/// 本番の Run キー（AutoStartRegistration.RunKeyPath）には一切書かない。
/// </summary>
public sealed class AutoStartRegistrationTests : IDisposable
{
    private const string ExePath = @"C:\Users\Someone\AppData\Local\AutoSwitch For Inzone\bin\AutoSwitch.App.exe";

    private readonly string _keyPath = $@"Software\AutoSwitchForInzone\Tests\Run-{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

    private AutoStartRegistration Create(string exePath = ExePath) => new(exePath, _keyPath);

    private object? ReadValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        return key?.GetValue(AutoStartRegistration.ValueName);
    }

    [Fact]
    public void IsEnabled_キーが無ければ_false()
    {
        Assert.False(Create().IsEnabled());
    }

    [Fact]
    public void Enable_引用符つきのフルパスを書く()
    {
        Create().Enable();

        Assert.Equal($"\"{ExePath}\"", ReadValue());
    }

    [Fact]
    public void Enable_のあと_IsEnabled_が_true()
    {
        var registration = Create();

        registration.Enable();

        Assert.True(registration.IsEnabled());
    }

    [Fact]
    public void Enable_二回目でも同じ値のまま()
    {
        var registration = Create();

        registration.Enable();
        registration.Enable();

        Assert.Equal($"\"{ExePath}\"", ReadValue());
    }

    [Fact]
    public void Enable_古いパスが入っていても現在のパスで上書きする()
    {
        Create(@"C:\old\AutoSwitch.App.exe").Enable();

        Create().Enable();

        Assert.Equal($"\"{ExePath}\"", ReadValue());
    }

    [Fact]
    public void Disable_値を消して_IsEnabled_が_false()
    {
        var registration = Create();
        registration.Enable();

        registration.Disable();

        Assert.Null(ReadValue());
        Assert.False(registration.IsEnabled());
    }

    [Fact]
    public void Disable_登録が無くても例外にならない()
    {
        var registration = Create();

        registration.Disable();

        Assert.False(registration.IsEnabled());
    }
}
