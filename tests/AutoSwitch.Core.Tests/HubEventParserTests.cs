namespace AutoSwitch.Core.Tests;

public class HubEventParserTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "actionlog-sample.log");

    private static string EventLine(string logId, long timeStamp, int actionTypeId) =>
        EventLineWithRawLogId($"\"{logId}\"", timeStamp, actionTypeId);

    // logId を JSON のトークンそのままで埋め込む（数値・真偽値・null・オブジェクトなどを試すため）
    private static string EventLineWithRawLogId(string rawLogId, long timeStamp = 1000, int actionTypeId = 23011) =>
        $"{{\"logVersion\":\"1.5\",\"logId\":{rawLogId},\"timeStamp\":{timeStamp},\"actionTypeId\":{actionTypeId},\"actionTypeName\":\"ConnectDevice\"}}";

    private static string[] ReadFixtureLines() =>
        File.ReadAllLines(FixturePath).Where(line => line.Trim().Length > 0).ToArray();

    [Fact]
    public void Parse_ConnectAndDisconnect_ReturnsEventsInOrder()
    {
        var lines = new[]
        {
            EventLine("id-1", 1000, 23011),
            EventLine("id-2", 2000, 23012),
        };

        var events = HubEventParser.Parse(lines, new SeenLogIdSet());

        Assert.Equal(2, events.Count);
        Assert.Equal(new HubEvent("id-1", 1000, HeadsetState.Connected), events[0]);
        Assert.Equal(new HubEvent("id-2", 2000, HeadsetState.Disconnected), events[1]);
    }

    [Fact]
    public void Parse_OtherActionTypes_AreIgnored()
    {
        var lines = new[]
        {
            EventLine("id-1", 1000, 23004),
            EventLine("id-2", 2000, 23014),
            EventLine("id-3", 3000, 23001),
        };

        Assert.Empty(HubEventParser.Parse(lines, new SeenLogIdSet()));
    }

    [Fact]
    public void Parse_BrokenJsonLine_IsSkippedAndValidLineIsParsed()
    {
        var lines = new[]
        {
            "{\"actionTypeId\":23011, this is broken",
            EventLine("id-ok", 5000, 23011),
        };

        var events = HubEventParser.Parse(lines, new SeenLogIdSet());

        var single = Assert.Single(events);
        Assert.Equal("id-ok", single.LogId);
    }

    [Fact]
    public void Parse_SameLineInTwoCalls_ReturnsItOnlyOnce()
    {
        var set = new SeenLogIdSet();
        var line = EventLine("dup-1", 1000, 23011);

        Assert.Single(HubEventParser.Parse([line], set));
        Assert.Empty(HubEventParser.Parse([line], set));
    }

    [Fact]
    public void Parse_SameLineTwiceInOneCall_ReturnsItOnlyOnce()
    {
        var line = EventLine("dup-2", 1000, 23011);

        Assert.Single(HubEventParser.Parse([line, line], new SeenLogIdSet()));
    }

    [Fact]
    public void Parse_LineWithoutLogId_IsIgnored()
    {
        var lines = new[] { "{\"timeStamp\":1000,\"actionTypeId\":23011,\"actionTypeName\":\"ConnectDevice\"}" };

        Assert.Empty(HubEventParser.Parse(lines, new SeenLogIdSet()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Parse_EmptyLogId_IsIgnored(string rawLogId)
    {
        Assert.Empty(HubEventParser.Parse([EventLineWithRawLogId(rawLogId)], new SeenLogIdSet()));
    }

    // 旧版の [string] 変換の実測に合わせる（整数はその表記、真偽値は True / False）
    [Theory]
    [InlineData("123", "123")]
    [InlineData("12345678901234567890", "12345678901234567890")]
    [InlineData("true", "True")]
    [InlineData("false", "False")]
    [InlineData("-0", "0")]
    public void Parse_NonStringLogId_IsConvertedLikeOldVersion(string rawLogId, string expectedLogId)
    {
        var events = HubEventParser.Parse([EventLineWithRawLogId(rawLogId)], new SeenLogIdSet());

        var single = Assert.Single(events);
        Assert.Equal(expectedLogId, single.LogId);
    }

    // 旧版との既知の差異（作者承認、2026-09-16）。旧版はこれらを文字列化して受け入れる
    [Theory]
    [InlineData("123.0")]
    [InlineData("1.5e3")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    public void Parse_NonIntegerNumberObjectOrArrayLogId_IsIgnored(string rawLogId)
    {
        Assert.Empty(HubEventParser.Parse([EventLineWithRawLogId(rawLogId)], new SeenLogIdSet()));
    }

    [Fact]
    public void Parse_EmptyOrNullLines_ReturnsNoEvents()
    {
        var set = new SeenLogIdSet();

        Assert.Empty(HubEventParser.Parse([], set));
        Assert.Empty(HubEventParser.Parse(null, set));
    }

    [Fact]
    public void Parse_Fixture_ReturnsFourEventsInOrder()
    {
        var events = HubEventParser.Parse(ReadFixtureLines(), new SeenLogIdSet());

        Assert.Equal(
            [HeadsetState.Connected, HeadsetState.Disconnected, HeadsetState.Connected, HeadsetState.Connected],
            events.Select(e => e.State).ToArray());
        Assert.Equal(1789454166382, events[0].TimeStampMs);
        Assert.Equal(1789454196511, events[^1].TimeStampMs);
    }

    [Fact]
    public void Parse_FixtureTwiceWithSameSet_SecondReturnsNothing()
    {
        var set = new SeenLogIdSet();
        var lines = ReadFixtureLines();

        Assert.Equal(4, HubEventParser.Parse(lines, set).Count);
        Assert.Empty(HubEventParser.Parse(lines, set));
    }

    // 以下は表の外で、旧版 SonyEventProvider.psm1 の実測（証跡 old-parser-edge-probe.txt）に合わせた規則を固定する

    // 旧版は -match とプロパティ参照のどちらも大文字小文字を区別しない
    [Fact]
    public void Parse_PropertyNamesInDifferentCase_AreAccepted()
    {
        var lines = new[] { "{\"ACTIONTYPEID\":23011,\"LOGID\":\"upper-keys\",\"TIMESTAMP\":5}" };

        var single = Assert.Single(HubEventParser.Parse(lines, new SeenLogIdSet()));
        Assert.Equal(new HubEvent("upper-keys", 5, HeadsetState.Connected), single);
    }

    [Theory]
    [InlineData("\"23011\"")]
    [InlineData("23011.0")]
    [InlineData("230110")]
    public void Parse_ActionTypeIdNotExactNumber_IsIgnored(string rawActionTypeId)
    {
        var lines = new[] { $"{{\"actionTypeId\":{rawActionTypeId},\"logId\":\"x\",\"timeStamp\":1}}" };

        Assert.Empty(HubEventParser.Parse(lines, new SeenLogIdSet()));
    }

    [Theory]
    [InlineData(",\"timeStamp\":-5", -5)]
    [InlineData(",\"timeStamp\":\"123\"", 123)]
    [InlineData(",\"timeStamp\":1.5", 0)]
    [InlineData(",\"timeStamp\":123.0", 0)]
    [InlineData(",\"timeStamp\":true", 0)]
    [InlineData(",\"timeStamp\":null", 0)]
    [InlineData(",\"timeStamp\":12345678901234567890", 0)]
    [InlineData("", 0)]
    public void Parse_TimeStamp_UsesIntegerOrZero(string timeStampPart, long expected)
    {
        var lines = new[] { $"{{\"actionTypeId\":23011,\"logId\":\"ts\"{timeStampPart}}}" };

        var single = Assert.Single(HubEventParser.Parse(lines, new SeenLogIdSet()));
        Assert.Equal(expected, single.TimeStampMs);
    }

    [Fact]
    public void Parse_LogIdDifferingOnlyInCase_IsTreatedAsSeen()
    {
        var lines = new[] { EventLine("abc", 1000, 23011), EventLine("ABC", 2000, 23012) };

        Assert.Single(HubEventParser.Parse(lines, new SeenLogIdSet()));
    }

    // 旧版の ConvertFrom-Json は同名のキーを後の値で解決する（Task 5 の旧版実測 dup-exact）。
    // SettingsLoader も同じ規則なので、パーサも最後に現れた値を採る
    [Fact]
    public void Parse_DuplicateActionTypeId_UsesLastValue()
    {
        var lines = new[] { "{\"actionTypeId\":23011,\"actionTypeId\":23012,\"logId\":\"dup-action\",\"timeStamp\":1000}" };

        var single = Assert.Single(HubEventParser.Parse(lines, new SeenLogIdSet()));
        Assert.Equal(new HubEvent("dup-action", 1000, HeadsetState.Disconnected), single);
    }

    [Fact]
    public void Parse_DuplicateLogId_UsesLastValue()
    {
        var lines = new[] { "{\"actionTypeId\":23011,\"logId\":\"first\",\"logId\":\"last\",\"timeStamp\":1000}" };

        var single = Assert.Single(HubEventParser.Parse(lines, new SeenLogIdSet()));
        Assert.Equal("last", single.LogId);
    }

    [Fact]
    public void Parse_DuplicateTimeStamp_UsesLastValue()
    {
        var lines = new[] { "{\"actionTypeId\":23011,\"logId\":\"dup-ts\",\"timeStamp\":1000,\"timeStamp\":2000}" };

        var single = Assert.Single(HubEventParser.Parse(lines, new SeenLogIdSet()));
        Assert.Equal(2000, single.TimeStampMs);
    }

    // 大文字小文字を区別しない照合のまま、最後に現れた値を採る（SettingsLoader と同じ解決）
    [Fact]
    public void Parse_DuplicatePropertiesDifferingOnlyInCase_UseLastValue()
    {
        var lines = new[] { "{\"actionTypeId\":23011,\"ACTIONTYPEID\":23012,\"logId\":\"dup-case\",\"LOGID\":\"dup-case-last\",\"timeStamp\":1000,\"TIMESTAMP\":2000}" };

        var single = Assert.Single(HubEventParser.Parse(lines, new SeenLogIdSet()));
        Assert.Equal(new HubEvent("dup-case-last", 2000, HeadsetState.Disconnected), single);
    }

    [Fact]
    public void Parse_NullOrBlankElementsAndNonObjectJson_AreSkipped()
    {
        var lines = new string?[]
        {
            null,
            "   ",
            "[{\"actionTypeId\":23011,\"logId\":\"in-array\"}]",
            "{\"action\":{\"actionTypeId\":23011,\"logId\":\"nested\"}}",
            EventLine("ok", 1, 23011),
        };

        var single = Assert.Single(HubEventParser.Parse(lines!, new SeenLogIdSet()));
        Assert.Equal("ok", single.LogId);
    }
}
