namespace AutoSwitch.Core.Tests;

public class AudioSwitcherTests
{
    private const string BudsName = "スピーカー (INZONE Buds - Game)";
    private const string RealtekName = "スピーカー (Realtek(R) Audio)";
    private const string BudsMicName = "ヘッドセット マイク (INZONE Buds - Chat)";
    private const string UsbMicName = "マイク (USB Microphone)";

    private static readonly AudioDevice Buds = new("id-buds", BudsName);
    private static readonly AudioDevice Realtek = new("id-realtek", RealtekName);
    private static readonly AudioDevice BudsMic = new("id-buds-mic", BudsMicName);
    private static readonly AudioDevice UsbMic = new("id-usb-mic", UsbMicName);

    private static FakeAudioSession RealtekDefaultWithBuds()
    {
        var session = new FakeAudioSession();
        session.Playback.Default = Realtek;
        session.Playback.Devices.Add(Realtek);
        session.Playback.Devices.Add(Buds);
        return session;
    }

    private static FakeAudioSession UsbMicDefaultWithBudsMic()
    {
        var session = new FakeAudioSession();
        session.Capture.Default = UsbMic;
        session.Capture.Devices.Add(UsbMic);
        session.Capture.Devices.Add(BudsMic);
        return session;
    }

    private static AudioSwitcher SwitcherFor(FakeAudioSession session) => new(() => session);

    // ---- 再生（最小版の PlaybackSwitcherTests から、期待する結果と文言を変えずに移したもの） ----

    [Fact]
    public void SwitchTo_NotDefaultButListed_SwitchesOnceById()
    {
        var session = RealtekDefaultWithBuds();

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, BudsName);

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal($"既定の再生デバイスを切り替えました: {BudsName}", outcome.Message);
        Assert.Equal(["id-buds"], session.Playback.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_AlreadyDefault_DoesNotSwitch()
    {
        var session = new FakeAudioSession();
        session.Playback.Default = Buds;
        session.Playback.Devices.Add(Buds);

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, BudsName);

        Assert.Equal(SwitchResult.AlreadyDefault, outcome.Result);
        Assert.Equal($"既に既定の再生デバイスです: {BudsName}", outcome.Message);
        Assert.Empty(session.Playback.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_NameNotListed_ReturnsDeviceNotFound()
    {
        var session = new FakeAudioSession();
        session.Playback.Default = Realtek;
        session.Playback.Devices.Add(Realtek);

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, "存在しないデバイス");

        Assert.Equal(SwitchResult.DeviceNotFound, outcome.Result);
        Assert.Contains("存在しないデバイス", outcome.Message);
        Assert.Equal("再生デバイスが見つかりません: 存在しないデバイス", outcome.Message);
        Assert.Empty(session.Playback.SetDefaultCalls);
    }

    // 旧版の ModuleUnavailable に相当する
    [Fact]
    public void SwitchTo_OpenSessionThrows_ReturnsUnavailable()
    {
        var switcher = new AudioSwitcher(() => throw new InvalidOperationException("simulated open failure"));

        SwitchOutcome? outcome = null;
        var ex = Record.Exception(() => outcome = switcher.SwitchTo(DeviceFlow.Playback, BudsName));

        Assert.Null(ex);
        Assert.NotNull(outcome);
        Assert.Equal(SwitchResult.Unavailable, outcome.Result);
        Assert.Equal("音声デバイスを操作できません。切り替えを見送ります: simulated open failure", outcome.Message);
    }

    [Fact]
    public void SwitchTo_SetDefaultPlaybackThrows_ReturnsFailed()
    {
        var session = RealtekDefaultWithBuds();
        session.Playback.SetDefaultException = new InvalidOperationException("simulated device failure");

        SwitchOutcome? outcome = null;
        var ex = Record.Exception(() => outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, BudsName));

        Assert.Null(ex);
        Assert.NotNull(outcome);
        Assert.Equal(SwitchResult.Failed, outcome.Result);
        Assert.Contains("simulated device failure", outcome.Message);
        Assert.Equal($"切り替えに失敗しました ({BudsName}): simulated device failure", outcome.Message);
        Assert.Equal(["id-buds"], session.Playback.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_GetDefaultPlaybackThrows_SkipsCheckAndSwitches()
    {
        var session = RealtekDefaultWithBuds();
        session.Playback.GetDefaultException = new InvalidOperationException("no default endpoint");

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, BudsName);

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(["id-buds"], session.Playback.SetDefaultCalls);
    }

    // 旧版は取得結果が $null なら判定せずに先へ進む
    [Fact]
    public void SwitchTo_NoDefaultPlayback_SkipsCheckAndSwitches()
    {
        var session = RealtekDefaultWithBuds();
        session.Playback.Default = null;

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, BudsName);

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(["id-buds"], session.Playback.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_GetPlaybackDevicesThrows_ReturnsFailed()
    {
        var session = RealtekDefaultWithBuds();
        session.Playback.GetDevicesException = new InvalidOperationException("simulated list failure");

        SwitchOutcome? outcome = null;
        var ex = Record.Exception(() => outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, BudsName));

        Assert.Null(ex);
        Assert.NotNull(outcome);
        Assert.Equal(SwitchResult.Failed, outcome.Result);
        Assert.Equal($"切り替えに失敗しました ({BudsName}): simulated list failure", outcome.Message);
        Assert.Empty(session.Playback.SetDefaultCalls);
    }

    // 旧版と同じく、既定の判定は一覧の取得より前に行う
    [Fact]
    public void SwitchTo_AlreadyDefault_IsDecidedBeforeListing()
    {
        var session = new FakeAudioSession();
        session.Playback.Default = Buds;
        session.Playback.GetDevicesException = new InvalidOperationException("simulated list failure");

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, BudsName);

        Assert.Equal(SwitchResult.AlreadyDefault, outcome.Result);
    }

    [Fact]
    public void SwitchTo_DefaultNameDiffersOnlyInCase_ReturnsAlreadyDefault()
    {
        var session = new FakeAudioSession();
        session.Playback.Default = new AudioDevice("id-buds", "inzone buds");
        session.Playback.Devices.Add(session.Playback.Default);

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, "INZONE Buds");

        Assert.Equal(SwitchResult.AlreadyDefault, outcome.Result);
        Assert.Empty(session.Playback.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_ListedNameDiffersOnlyInCase_Switches()
    {
        var session = new FakeAudioSession();
        session.Playback.Default = Realtek;
        session.Playback.Devices.Add(Realtek);
        session.Playback.Devices.Add(new AudioDevice("id-buds", "inzone buds"));

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, "INZONE Buds");

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(["id-buds"], session.Playback.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_TwoCandidatesWithSameName_UsesFirstInList()
    {
        var session = new FakeAudioSession();
        session.Playback.Default = Realtek;
        session.Playback.Devices.Add(Realtek);
        session.Playback.Devices.Add(new AudioDevice("id-first", BudsName));
        session.Playback.Devices.Add(new AudioDevice("id-second", BudsName));

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, BudsName);

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(["id-first"], session.Playback.SetDefaultCalls);
    }

    [Theory]
    [InlineData("Switched", SwitchResult.Switched)]
    [InlineData("AlreadyDefault", SwitchResult.AlreadyDefault)]
    [InlineData("DeviceNotFound", SwitchResult.DeviceNotFound)]
    [InlineData("FailedOnSet", SwitchResult.Failed)]
    [InlineData("FailedOnList", SwitchResult.Failed)]
    public void SwitchTo_SessionOpened_DisposesExactlyOnce(string scenario, SwitchResult expected)
    {
        var session = RealtekDefaultWithBuds();
        var name = BudsName;
        switch (scenario)
        {
            case "AlreadyDefault":
                name = RealtekName;
                break;
            case "DeviceNotFound":
                name = "存在しないデバイス";
                break;
            case "FailedOnSet":
                session.Playback.SetDefaultException = new InvalidOperationException("simulated device failure");
                break;
            case "FailedOnList":
                session.Playback.GetDevicesException = new InvalidOperationException("simulated list failure");
                break;
        }

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, name);

        Assert.Equal(expected, outcome.Result);
        Assert.Equal(1, session.DisposeCount);
    }

    // 破棄の失敗で結果を失わず、例外も外へ出さない
    [Fact]
    public void SwitchTo_DisposeThrows_KeepsOutcomeWithoutException()
    {
        var session = RealtekDefaultWithBuds();
        session.DisposeException = new InvalidOperationException("simulated dispose failure");

        SwitchOutcome? outcome = null;
        var ex = Record.Exception(() => outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, BudsName));

        Assert.Null(ex);
        Assert.NotNull(outcome);
        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public void Constructor_NullOpenSession_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AudioSwitcher(null!));
    }

    // ---- 録音（Issue rurute/inzone-auto-switch#8） ----

    [Fact]
    public void SwitchTo_Capture_NotDefaultButListed_SwitchesOnceById()
    {
        var session = UsbMicDefaultWithBudsMic();

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Capture, BudsMicName);

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal($"既定の録音デバイスを切り替えました: {BudsMicName}", outcome.Message);
        Assert.Equal(["id-buds-mic"], session.Capture.SetDefaultCalls);
        Assert.Empty(session.Playback.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_Capture_AlreadyDefault_DoesNotSwitch()
    {
        var session = UsbMicDefaultWithBudsMic();

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Capture, UsbMicName);

        Assert.Equal(SwitchResult.AlreadyDefault, outcome.Result);
        Assert.Equal($"既に既定の録音デバイスです: {UsbMicName}", outcome.Message);
        Assert.Empty(session.Capture.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_Capture_NameNotListed_ReturnsDeviceNotFound()
    {
        var session = UsbMicDefaultWithBudsMic();

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Capture, "存在しないマイク");

        Assert.Equal(SwitchResult.DeviceNotFound, outcome.Result);
        Assert.Equal("録音デバイスが見つかりません: 存在しないマイク", outcome.Message);
        Assert.Empty(session.Capture.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_Capture_OpenSessionThrows_ReturnsUnavailable()
    {
        var switcher = new AudioSwitcher(() => throw new InvalidOperationException("simulated open failure"));

        SwitchOutcome? outcome = null;
        var ex = Record.Exception(() => outcome = switcher.SwitchTo(DeviceFlow.Capture, BudsMicName));

        Assert.Null(ex);
        Assert.NotNull(outcome);
        Assert.Equal(SwitchResult.Unavailable, outcome.Result);
        Assert.Equal("音声デバイスを操作できません。録音の切り替えを見送ります: simulated open failure", outcome.Message);
    }

    [Fact]
    public void SwitchTo_Capture_SetDefaultThrows_ReturnsFailed()
    {
        var session = UsbMicDefaultWithBudsMic();
        session.Capture.SetDefaultException = new InvalidOperationException("simulated device failure");

        SwitchOutcome? outcome = null;
        var ex = Record.Exception(() => outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Capture, BudsMicName));

        Assert.Null(ex);
        Assert.NotNull(outcome);
        Assert.Equal(SwitchResult.Failed, outcome.Result);
        Assert.Equal($"録音の切り替えに失敗しました ({BudsMicName}): simulated device failure", outcome.Message);
        Assert.Equal(["id-buds-mic"], session.Capture.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_Capture_GetDevicesThrows_ReturnsFailed()
    {
        var session = UsbMicDefaultWithBudsMic();
        session.Capture.GetDevicesException = new InvalidOperationException("simulated list failure");

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Capture, BudsMicName);

        Assert.Equal(SwitchResult.Failed, outcome.Result);
        Assert.Equal($"録音の切り替えに失敗しました ({BudsMicName}): simulated list failure", outcome.Message);
        Assert.Empty(session.Capture.SetDefaultCalls);
    }

    // 再生の一覧にだけある名前は、録音では見つからない（流れの取り違えを防ぐ）
    [Fact]
    public void SwitchTo_Capture_NameOnlyInPlaybackList_ReturnsDeviceNotFound()
    {
        var session = RealtekDefaultWithBuds();
        session.Capture.Default = UsbMic;
        session.Capture.Devices.Add(UsbMic);

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Capture, BudsName);

        Assert.Equal(SwitchResult.DeviceNotFound, outcome.Result);
        Assert.Empty(session.Capture.SetDefaultCalls);
        Assert.Empty(session.Playback.SetDefaultCalls);
    }

    // 録音の既定が目的の名前でも、再生の既定を見て「既に既定」にしない
    [Fact]
    public void SwitchTo_Playback_UsesPlaybackDefaultNotCaptureDefault()
    {
        var session = RealtekDefaultWithBuds();
        session.Capture.Default = new AudioDevice("id-capture-same-name", BudsName);

        var outcome = SwitcherFor(session).SwitchTo(DeviceFlow.Playback, BudsName);

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(["id-buds"], session.Playback.SetDefaultCalls);
        Assert.Empty(session.Capture.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_Capture_SessionOpened_DisposesExactlyOnce()
    {
        var session = UsbMicDefaultWithBudsMic();

        SwitcherFor(session).SwitchTo(DeviceFlow.Capture, BudsMicName);

        Assert.Equal(1, session.DisposeCount);
    }

    // セッションは切り替えのたびに開くので、再生で開けなくても録音は別に開いて試みる
    [Fact]
    public void SwitchTo_OpensSeparateSessionPerCall_CaptureSucceedsAfterPlaybackOpenFailure()
    {
        var capture = UsbMicDefaultWithBudsMic();
        var opens = 0;
        var switcher = new AudioSwitcher(() =>
        {
            opens++;
            return opens == 1 ? throw new InvalidOperationException("simulated open failure") : capture;
        });

        var playback = switcher.SwitchTo(DeviceFlow.Playback, BudsName);
        var recording = switcher.SwitchTo(DeviceFlow.Capture, BudsMicName);

        Assert.Equal(SwitchResult.Unavailable, playback.Result);
        Assert.Equal(SwitchResult.Switched, recording.Result);
        Assert.Equal(2, opens);
        Assert.Equal(["id-buds-mic"], capture.Capture.SetDefaultCalls);
    }

    [Fact]
    public void SwitchTo_UndefinedFlow_ThrowsWithoutOpeningSession()
    {
        var opens = 0;
        var switcher = new AudioSwitcher(() =>
        {
            opens++;
            return new FakeAudioSession();
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => switcher.SwitchTo((DeviceFlow)99, BudsName));
        Assert.Equal(0, opens);
    }
}
