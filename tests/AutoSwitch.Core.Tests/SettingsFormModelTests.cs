namespace AutoSwitch.Core.Tests;

// 設定画面のモデル（spec §4・§8）
public sealed class SettingsFormModelTests
{
    private static readonly SwitchSettings Current = new(
        "スピーカー (INZONE Buds - Game)", "スピーカー (Realtek(R) Audio)",
        "ヘッドセット マイク (INZONE Buds - Chat)", "", true, false);

    private static readonly DeviceNameList Playback = DeviceNameList.Available(
        ["スピーカー (Realtek(R) Audio)", "スピーカー (INZONE Buds - Game)", "HDMI"]);

    private static readonly DeviceNameList Capture = DeviceNameList.Available(
        ["マイク (USB Microphone)", "ヘッドセット マイク (INZONE Buds - Chat)"]);

    private static DeviceChoice Device(string name) => new(name, name, DeviceChoiceKind.Device);

    private static readonly DeviceChoice NotSet = new("（未設定）", "", DeviceChoiceKind.NotSet);

    private static DeviceChoice Selected(DeviceSelection selection) => selection.Choices[selection.SelectedIndex];

    [Fact]
    public void Create_ConfiguredDevicesInList_AreSelectedInListOrder()
    {
        var model = SettingsFormModel.Create(Current, Playback, Capture);

        Assert.Equal(
            [Device("スピーカー (Realtek(R) Audio)"), Device("スピーカー (INZONE Buds - Game)"), Device("HDMI")],
            model.BudsPlayback.Choices);
        Assert.Equal(Device("スピーカー (INZONE Buds - Game)"), Selected(model.BudsPlayback));
        Assert.Equal(Device("スピーカー (Realtek(R) Audio)"), Selected(model.SpeakerPlayback));
        Assert.Equal(Device("ヘッドセット マイク (INZONE Buds - Chat)"), Selected(model.BudsCapture));
        Assert.True(model.SwitchOnConnect);
        Assert.False(model.SwitchOnDisconnect);
        Assert.Empty(model.Notices);
    }

    [Fact]
    public void Create_NameDiffersOnlyInCase_SelectsListSpelling()
    {
        var model = SettingsFormModel.Create(Current with { BudsDevice = "スピーカー (inzone buds - game)" }, Playback, Capture);

        Assert.Equal(Device("スピーカー (INZONE Buds - Game)"), Selected(model.BudsPlayback));
        Assert.Equal(3, model.BudsPlayback.Choices.Count);
    }

    [Fact]
    public void Create_ConfiguredPlaybackNotInList_AddsNotConnectedChoiceFirstAndSelectsIt()
    {
        var model = SettingsFormModel.Create(Current with { BudsDevice = "スピーカー (Old Buds)" }, Playback, Capture);

        var expected = new DeviceChoice("スピーカー (Old Buds)（未接続）", "スピーカー (Old Buds)", DeviceChoiceKind.NotConnected);
        Assert.Equal(expected, model.BudsPlayback.Choices[0]);
        Assert.Equal(0, model.BudsPlayback.SelectedIndex);
        Assert.Equal(4, model.BudsPlayback.Choices.Count);
        // 反対の向きには足さない
        Assert.Equal(3, model.SpeakerPlayback.Choices.Count);
    }

    [Fact]
    public void Create_DuplicateNames_AreMergedCaseInsensitively()
    {
        var playback = DeviceNameList.Available(["Speaker", "SPEAKER", "Other", "speaker"]);

        var model = SettingsFormModel.Create(Current with { BudsDevice = "Other", SpeakerDevice = "speaker" }, playback, Capture);

        Assert.Equal([Device("Speaker"), Device("Other")], model.BudsPlayback.Choices);
        Assert.Equal(Device("Speaker"), Selected(model.SpeakerPlayback));
    }

    [Fact]
    public void Create_Capture_HasNotSetBeforeDevices_AndEmptyConfigSelectsIt()
    {
        var model = SettingsFormModel.Create(Current, Playback, Capture);

        Assert.Equal(
            [NotSet, Device("マイク (USB Microphone)"), Device("ヘッドセット マイク (INZONE Buds - Chat)")],
            model.SpeakerCapture.Choices);
        Assert.Equal(NotSet, Selected(model.SpeakerCapture));
    }

    [Fact]
    public void Create_ConfiguredCaptureNotInList_OrderIsNotConnectedThenNotSetThenDevices()
    {
        var model = SettingsFormModel.Create(Current with { BudsInputDevice = "マイク (Old Buds)" }, Playback, Capture);

        Assert.Equal(
            [
                new DeviceChoice("マイク (Old Buds)（未接続）", "マイク (Old Buds)", DeviceChoiceKind.NotConnected),
                NotSet,
                Device("マイク (USB Microphone)"),
                Device("ヘッドセット マイク (INZONE Buds - Chat)"),
            ],
            model.BudsCapture.Choices);
        Assert.Equal(0, model.BudsCapture.SelectedIndex);
    }

    [Fact]
    public void Create_ListUnavailable_OffersOnlyConfiguredNamesAndReportsNotice()
    {
        var model = SettingsFormModel.Create(
            Current,
            DeviceNameList.Unavailable("E_FAIL"),
            DeviceNameList.Unavailable("E_ACCESSDENIED"));

        Assert.Equal(
            [new DeviceChoice("スピーカー (INZONE Buds - Game)", "スピーカー (INZONE Buds - Game)", DeviceChoiceKind.Unlisted)],
            model.BudsPlayback.Choices);
        Assert.Equal(0, model.BudsPlayback.SelectedIndex);
        Assert.Equal(
            [
                new DeviceChoice("ヘッドセット マイク (INZONE Buds - Chat)", "ヘッドセット マイク (INZONE Buds - Chat)", DeviceChoiceKind.Unlisted),
                NotSet,
            ],
            model.BudsCapture.Choices);
        Assert.Equal([NotSet], model.SpeakerCapture.Choices);
        Assert.Equal(0, model.SpeakerCapture.SelectedIndex);
        Assert.Equal(
            ["再生デバイスの一覧を取得できません: E_FAIL", "録音デバイスの一覧を取得できません: E_ACCESSDENIED"],
            model.Notices);
    }

    [Fact]
    public void Create_EmptyPlaybackConfigured_SelectsNothing()
    {
        var model = SettingsFormModel.Create(Current with { BudsDevice = "" }, Playback, Capture);

        Assert.Equal(-1, model.BudsPlayback.SelectedIndex);
        Assert.Equal(3, model.BudsPlayback.Choices.Count);
    }

    [Fact]
    public void ToSettings_InitialSelectionsUnchanged_RoundTripsCurrentValues()
    {
        var current = Current with { BudsDevice = "スピーカー (Old Buds)", SpeakerInputDevice = "マイク (Old)" };
        var model = SettingsFormModel.Create(current, Playback, Capture);

        var settings = SettingsFormModel.ToSettings(
            Selected(model.BudsPlayback), Selected(model.BudsCapture), Selected(model.SpeakerPlayback), Selected(model.SpeakerCapture),
            model.SwitchOnConnect, model.SwitchOnDisconnect);

        Assert.Equal(current, settings);
    }

    [Fact]
    public void ToSettings_NotSetCapture_SavesEmpty()
    {
        var settings = SettingsFormModel.ToSettings(
            Device("BUDS"), NotSet, Device("SPEAKER"), null, false, true);

        Assert.Equal(new SwitchSettings("BUDS", "SPEAKER", "", "", false, true), settings);
    }

    [Fact]
    public void ToSettings_PlaybackNotSelectedOrEmpty_ReturnsNull()
    {
        Assert.Null(SettingsFormModel.ToSettings(null, NotSet, Device("SPEAKER"), NotSet, true, true));
        Assert.Null(SettingsFormModel.ToSettings(Device("BUDS"), NotSet, NotSet, NotSet, true, true));
    }
}
