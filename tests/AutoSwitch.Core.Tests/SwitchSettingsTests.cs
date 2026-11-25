namespace AutoSwitch.Core.Tests;

// 設定画面で扱う6項目（Issue rurute/inzone-auto-switch#9）と、動作中の設定の入れ物
public sealed class SwitchSettingsTests
{
    private static readonly AppSettings Running = AppSettings.Default with
    {
        BudsDevice = "BUDS",
        SpeakerDevice = "SPEAKER",
        BudsInputDevice = "BUDS_MIC",
        SpeakerInputDevice = "",
        SwitchOnConnect = true,
        SwitchOnDisconnect = false,
        PollIntervalSeconds = 3,
        IdlePollIntervalSeconds = 7,
        StartupSyncMaxAgeMinutes = 3,
        LogLevel = LogLevel.Debug,
        MaxLogSizeKB = 256,
    };

    [Fact]
    public void From_CopiesTheSixItems()
    {
        var s = SwitchSettings.From(Running);

        Assert.Equal(new SwitchSettings("BUDS", "SPEAKER", "BUDS_MIC", "", true, false), s);
    }

    [Fact]
    public void ApplyTo_ReplacesOnlyTheSixItems()
    {
        var saved = new SwitchSettings("NEW_BUDS", "NEW_SPEAKER", "", "NEW_SPEAKER_MIC", false, true);

        var applied = saved.ApplyTo(Running);

        Assert.Equal(Running with
        {
            BudsDevice = "NEW_BUDS",
            SpeakerDevice = "NEW_SPEAKER",
            BudsInputDevice = "",
            SpeakerInputDevice = "NEW_SPEAKER_MIC",
            SwitchOnConnect = false,
            SwitchOnDisconnect = true,
        }, applied);
    }

    [Fact]
    public void DescribeSaved_WritesUnsetCaptureAsMiseitei()
    {
        var saved = new SwitchSettings("BUDS", "SPEAKER", "BUDS_MIC", " ", true, false);

        Assert.Equal(
            "設定画面で保存しました: 接続時 再生=BUDS 録音=BUDS_MIC / 切断時 再生=SPEAKER 録音=（未設定） / switchOnConnect=true switchOnDisconnect=false",
            saved.DescribeSaved());
    }

    [Fact]
    public void LiveSettings_ReplaceChangesCurrent()
    {
        var live = new LiveSettings(Running);
        var next = Running with { BudsDevice = "NEW_BUDS" };

        live.Replace(next);

        Assert.Same(next, live.Current);
    }

    [Fact]
    public void LiveSettings_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new LiveSettings(null!));
        Assert.Throws<ArgumentNullException>(() => new LiveSettings(Running).Replace(null!));
    }
}
