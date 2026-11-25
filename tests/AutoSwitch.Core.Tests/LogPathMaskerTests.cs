namespace AutoSwitch.Core.Tests;

public sealed class LogPathMaskerTests
{
    private const string Local = @"C:\Users\alice\AppData\Local";
    private const string Profile = @"C:\Users\alice";

    [Fact]
    public void Mask_AppRootUnderLocalAppData_UsesLocalAppDataVariable()
    {
        Assert.Equal(
            @"appRoot=%LOCALAPPDATA%\AutoSwitchForInzone",
            LogPathMasker.Mask(@"appRoot=C:\Users\alice\AppData\Local\AutoSwitchForInzone", Local, Profile));
    }

    [Fact]
    public void Mask_QuotedCommandLine_KeepsQuotes()
    {
        Assert.Equal(
            @"""%LOCALAPPDATA%\AutoSwitchForInzone\bin\AutoSwitch.App.exe""",
            LogPathMasker.Mask(@"""C:\Users\alice\AppData\Local\AutoSwitchForInzone\bin\AutoSwitch.App.exe""", Local, Profile));
    }

    [Fact]
    public void Mask_OtherFolderUnderProfile_UsesUserProfileVariable()
    {
        Assert.Equal(
            @"""%USERPROFILE%\Tools\AutoSwitch.App.exe""",
            LogPathMasker.Mask(@"""C:\Users\alice\Tools\AutoSwitch.App.exe""", Local, Profile));
    }

    [Fact]
    public void Mask_IgnoresCaseAndTrailingSeparator()
    {
        Assert.Equal(
            @"%LOCALAPPDATA%\AutoSwitchForInzone\config.json",
            LogPathMasker.Mask(@"c:\users\ALICE\appdata\local\AutoSwitchForInzone\config.json", Local + @"\", Profile));
    }

    [Fact]
    public void Mask_PathInsideExceptionMessage_IsReplaced()
    {
        Assert.Equal(
            @"Access to the path '%LOCALAPPDATA%\AutoSwitchForInzone\config.json' is denied.",
            LogPathMasker.Mask(@"Access to the path 'C:\Users\alice\AppData\Local\AutoSwitchForInzone\config.json' is denied.", Local, Profile));
    }

    [Fact]
    public void Mask_LongerUserNameWithSamePrefix_NotReplaced()
    {
        const string text = @"C:\Users\alice2\Tools\a.exe";
        Assert.Equal(text, LogPathMasker.Mask(text, Local, Profile));
    }

    [Fact]
    public void Mask_PathOutsideProfileOrMissingFolders_Unchanged()
    {
        const string text = @"D:\Apps\AutoSwitch.App.exe";
        Assert.Equal(text, LogPathMasker.Mask(text, Local, Profile));
        Assert.Equal(@"C:\Users\alice\x", LogPathMasker.Mask(@"C:\Users\alice\x", null, ""));
    }
}
