namespace AutoSwitch.Core.Tests;

public class PlaybackSwitcherTests
{
    private const string BudsName = "スピーカー (INZONE Buds - Game)";
    private const string RealtekName = "スピーカー (Realtek(R) Audio)";

    private static readonly PlaybackDevice Buds = new("id-buds", BudsName);
    private static readonly PlaybackDevice Realtek = new("id-realtek", RealtekName);

    private static FakePlaybackSession RealtekDefaultWithBuds()
    {
        var session = new FakePlaybackSession { Default = Realtek };
        session.Devices.Add(Realtek);
        session.Devices.Add(Buds);
        return session;
    }

    private static PlaybackSwitcher SwitcherFor(FakePlaybackSession session) => new(() => session);

    [Fact]
    public void SwitchTo_NotDefaultButListed_SwitchesOnceById()
    {
        var session = RealtekDefaultWithBuds();

        var outcome = SwitcherFor(session).SwitchTo(BudsName);

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal($"既定の再生デバイスを切り替えました: {BudsName}", outcome.Message);
        Assert.Equal(["id-buds"], session.SetDefaultPlaybackCalls);
    }

    [Fact]
    public void SwitchTo_AlreadyDefault_DoesNotSwitch()
    {
        var session = new FakePlaybackSession { Default = Buds };
        session.Devices.Add(Buds);

        var outcome = SwitcherFor(session).SwitchTo(BudsName);

        Assert.Equal(SwitchResult.AlreadyDefault, outcome.Result);
        Assert.Equal($"既に既定の再生デバイスです: {BudsName}", outcome.Message);
        Assert.Empty(session.SetDefaultPlaybackCalls);
    }

    [Fact]
    public void SwitchTo_NameNotListed_ReturnsDeviceNotFound()
    {
        var session = new FakePlaybackSession { Default = Realtek };
        session.Devices.Add(Realtek);

        var outcome = SwitcherFor(session).SwitchTo("存在しないデバイス");

        Assert.Equal(SwitchResult.DeviceNotFound, outcome.Result);
        Assert.Contains("存在しないデバイス", outcome.Message);
        Assert.Equal("再生デバイスが見つかりません: 存在しないデバイス", outcome.Message);
        Assert.Empty(session.SetDefaultPlaybackCalls);
    }

    // 旧版の ModuleUnavailable に相当する
    [Fact]
    public void SwitchTo_OpenSessionThrows_ReturnsUnavailable()
    {
        var switcher = new PlaybackSwitcher(() => throw new InvalidOperationException("simulated open failure"));

        SwitchOutcome? outcome = null;
        var ex = Record.Exception(() => outcome = switcher.SwitchTo(BudsName));

        Assert.Null(ex);
        Assert.NotNull(outcome);
        Assert.Equal(SwitchResult.Unavailable, outcome.Result);
        Assert.Equal("音声デバイスを操作できません。切り替えを見送ります: simulated open failure", outcome.Message);
    }

    [Fact]
    public void SwitchTo_SetDefaultPlaybackThrows_ReturnsFailed()
    {
        var session = RealtekDefaultWithBuds();
        session.SetDefaultPlaybackException = new InvalidOperationException("simulated device failure");

        SwitchOutcome? outcome = null;
        var ex = Record.Exception(() => outcome = SwitcherFor(session).SwitchTo(BudsName));

        Assert.Null(ex);
        Assert.NotNull(outcome);
        Assert.Equal(SwitchResult.Failed, outcome.Result);
        Assert.Contains("simulated device failure", outcome.Message);
        Assert.Equal($"切り替えに失敗しました ({BudsName}): simulated device failure", outcome.Message);
        Assert.Equal(["id-buds"], session.SetDefaultPlaybackCalls);
    }

    [Fact]
    public void SwitchTo_GetDefaultPlaybackThrows_SkipsCheckAndSwitches()
    {
        var session = RealtekDefaultWithBuds();
        session.GetDefaultPlaybackException = new InvalidOperationException("no default endpoint");

        var outcome = SwitcherFor(session).SwitchTo(BudsName);

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(["id-buds"], session.SetDefaultPlaybackCalls);
    }

    // 旧版は取得結果が $null なら判定せずに先へ進む
    [Fact]
    public void SwitchTo_NoDefaultPlayback_SkipsCheckAndSwitches()
    {
        var session = RealtekDefaultWithBuds();
        session.Default = null;

        var outcome = SwitcherFor(session).SwitchTo(BudsName);

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(["id-buds"], session.SetDefaultPlaybackCalls);
    }

    [Fact]
    public void SwitchTo_GetPlaybackDevicesThrows_ReturnsFailed()
    {
        var session = RealtekDefaultWithBuds();
        session.GetPlaybackDevicesException = new InvalidOperationException("simulated list failure");

        SwitchOutcome? outcome = null;
        var ex = Record.Exception(() => outcome = SwitcherFor(session).SwitchTo(BudsName));

        Assert.Null(ex);
        Assert.NotNull(outcome);
        Assert.Equal(SwitchResult.Failed, outcome.Result);
        Assert.Equal($"切り替えに失敗しました ({BudsName}): simulated list failure", outcome.Message);
        Assert.Empty(session.SetDefaultPlaybackCalls);
    }

    // 旧版と同じく、既定の判定は一覧の取得より前に行う
    [Fact]
    public void SwitchTo_AlreadyDefault_IsDecidedBeforeListing()
    {
        var session = new FakePlaybackSession { Default = Buds };
        session.GetPlaybackDevicesException = new InvalidOperationException("simulated list failure");

        var outcome = SwitcherFor(session).SwitchTo(BudsName);

        Assert.Equal(SwitchResult.AlreadyDefault, outcome.Result);
    }

    [Fact]
    public void SwitchTo_DefaultNameDiffersOnlyInCase_ReturnsAlreadyDefault()
    {
        var session = new FakePlaybackSession { Default = new PlaybackDevice("id-buds", "inzone buds") };
        session.Devices.Add(session.Default);

        var outcome = SwitcherFor(session).SwitchTo("INZONE Buds");

        Assert.Equal(SwitchResult.AlreadyDefault, outcome.Result);
        Assert.Empty(session.SetDefaultPlaybackCalls);
    }

    [Fact]
    public void SwitchTo_ListedNameDiffersOnlyInCase_Switches()
    {
        var session = new FakePlaybackSession { Default = Realtek };
        session.Devices.Add(Realtek);
        session.Devices.Add(new PlaybackDevice("id-buds", "inzone buds"));

        var outcome = SwitcherFor(session).SwitchTo("INZONE Buds");

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(["id-buds"], session.SetDefaultPlaybackCalls);
    }

    [Fact]
    public void SwitchTo_TwoCandidatesWithSameName_UsesFirstInList()
    {
        var session = new FakePlaybackSession { Default = Realtek };
        session.Devices.Add(Realtek);
        session.Devices.Add(new PlaybackDevice("id-first", BudsName));
        session.Devices.Add(new PlaybackDevice("id-second", BudsName));

        var outcome = SwitcherFor(session).SwitchTo(BudsName);

        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(["id-first"], session.SetDefaultPlaybackCalls);
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
                session.SetDefaultPlaybackException = new InvalidOperationException("simulated device failure");
                break;
            case "FailedOnList":
                session.GetPlaybackDevicesException = new InvalidOperationException("simulated list failure");
                break;
        }

        var outcome = SwitcherFor(session).SwitchTo(name);

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
        var ex = Record.Exception(() => outcome = SwitcherFor(session).SwitchTo(BudsName));

        Assert.Null(ex);
        Assert.NotNull(outcome);
        Assert.Equal(SwitchResult.Switched, outcome.Result);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public void Constructor_NullOpenSession_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PlaybackSwitcher(null!));
    }
}
