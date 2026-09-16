using System.Text;
using System.Text.Json;

namespace AutoSwitch.Core.Tests;

// 実際のファイルで検証する。各テストは一時ディレクトリを作り、終了時に削除する
public sealed class SettingsLoaderTests : IDisposable
{
    private readonly string _dir;

    public SettingsLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cfg-" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ExpandedDefaultActionLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Sony\INZONE Hub\ActionLog.log");

    private static AppSettings ExpandedDefault => AppSettings.Default with { ActionLogPath = ExpandedDefaultActionLogPath };

    // 旧版テストと同じく BOM 付き UTF-8 で書く
    private string WriteConfig(string content)
    {
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString() + ".json");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private SettingsLoadResult LoadContent(string content) => SettingsLoader.Load(WriteConfig(content));

    [Fact]
    public void Default_MatchesLegacyDefaults()
    {
        var d = AppSettings.Default;

        Assert.Equal("スピーカー (INZONE Buds - Game)", d.BudsDevice);
        Assert.Equal("スピーカー (Realtek(R) Audio)", d.SpeakerDevice);
        Assert.True(d.SwitchOnConnect);
        Assert.True(d.SwitchOnDisconnect);
        Assert.Equal(@"%APPDATA%\Sony\INZONE Hub\ActionLog.log", d.ActionLogPath);
        Assert.Equal(1, d.PollIntervalSeconds);
        Assert.Equal(5, d.IdlePollIntervalSeconds);
        Assert.Equal(10, d.StartupSyncMaxAgeMinutes);
        Assert.Equal(LogLevel.Info, d.LogLevel);
        Assert.Equal(1024, d.MaxLogSizeKB);
    }

    [Fact]
    public void Load_AllKeys_UsesSpecifiedValuesWithoutWarnings()
    {
        var result = LoadContent("""
            {
              "budsDevice": "Buds A",
              "speakerDevice": "Speaker B",
              "switchOnConnect": false,
              "switchOnDisconnect": false,
              "actionLogPath": "C:\\tmp\\a.log",
              "pollIntervalSeconds": 2,
              "idlePollIntervalSeconds": 9,
              "startupSyncMaxAgeMinutes": 30,
              "logLevel": "Debug",
              "maxLogSizeKB": 512
            }
            """);

        Assert.Empty(result.Warnings);
        Assert.Equal(
            new AppSettings("Buds A", "Speaker B", false, false, @"C:\tmp\a.log", 2, 9, 30, LogLevel.Debug, 512),
            result.Settings);
    }

    [Fact]
    public void Load_OnlyBudsDevice_FillsOthersWithDefaults()
    {
        var result = LoadContent("""{ "budsDevice": "Only This" }""");

        Assert.Empty(result.Warnings);
        Assert.Equal(ExpandedDefault with { BudsDevice = "Only This" }, result.Settings);
    }

    [Fact]
    public void Load_UnknownKey_IsIgnoredWithWarning()
    {
        var result = LoadContent("""{ "budsDevice": "X", "totallyUnknownKey": 1 }""");

        Assert.Equal(ExpandedDefault with { BudsDevice = "X" }, result.Settings);
        Assert.Equal(["未知の設定キーを無視します: totallyUnknownKey"], result.Warnings);
    }

    [Fact]
    public void Load_KeyWithDifferentCase_IsAccepted()
    {
        var result = LoadContent("""{ "POLLINTERVALSECONDS": 5 }""");

        Assert.Empty(result.Warnings);
        Assert.Equal(5, result.Settings.PollIntervalSeconds);
    }

    [Fact]
    public void Load_ActionLogPath_ExpandsEnvironmentVariables()
    {
        var result = LoadContent("""{ "actionLogPath": "%APPDATA%\\Sony\\INZONE Hub\\ActionLog.log" }""");

        Assert.Empty(result.Warnings);
        Assert.DoesNotContain("%APPDATA%", result.Settings.ActionLogPath);
        Assert.Equal(ExpandedDefaultActionLogPath, result.Settings.ActionLogPath);
    }

    [Fact]
    public void Load_OutOfRangeIntegers_FallBackToDefaultsWithWarnings()
    {
        var result = LoadContent("""{ "pollIntervalSeconds": 0, "maxLogSizeKB": 999999999 }""");

        Assert.Equal(1, result.Settings.PollIntervalSeconds);
        Assert.Equal(1024, result.Settings.MaxLogSizeKB);
        Assert.Equal(
            [
                "pollIntervalSeconds は 1 〜 60 の範囲で指定してください。既定値 1 を使います。",
                "maxLogSizeKB は 64 〜 102400 の範囲で指定してください。既定値 1024 を使います。",
            ],
            result.Warnings);
    }

    [Fact]
    public void Load_WrongTypes_FallBackToDefaultsWithWarnings()
    {
        var result = LoadContent("""{ "pollIntervalSeconds": "abc", "switchOnConnect": "yes", "budsDevice": "" }""");

        Assert.Equal(ExpandedDefault, result.Settings);
        Assert.True(result.Warnings.Count >= 3);
        Assert.Equal(
            [
                "pollIntervalSeconds は整数で指定してください。既定値 1 を使います。",
                "switchOnConnect は true / false で指定してください。既定値 True を使います。",
                "budsDevice は空でない文字列で指定してください。既定値を使います。",
            ],
            result.Warnings);
    }

    [Theory]
    [InlineData("\"2\"", 2)]
    [InlineData("\" 2 \"", 2)]   // 旧版の [int]::TryParse は前後の空白と符号を許す（実測）
    [InlineData("\"+2\"", 2)]
    public void Load_IntegerString_IsAccepted(string json, int expected)
    {
        var result = LoadContent($$"""{ "pollIntervalSeconds": {{json}} }""");

        Assert.Empty(result.Warnings);
        Assert.Equal(expected, result.Settings.PollIntervalSeconds);
    }

    // 2.0・null・int に収まらない値・真偽値は旧版でも整数の警告（実測）。
    // [2] と 1e1 は旧版では 2・10 として受け入れるが、plan の規則（JSON の整数または整数として読める文字列）に従い拒否する
    [Theory]
    [InlineData("2.0")]
    [InlineData("null")]
    [InlineData("99999999999")]
    [InlineData("true")]
    [InlineData("[2]")]
    [InlineData("1e1")]
    public void Load_NonIntegerValue_FallsBackWithIntegerWarning(string json)
    {
        var result = LoadContent($$"""{ "pollIntervalSeconds": {{json}} }""");

        Assert.Equal(1, result.Settings.PollIntervalSeconds);
        Assert.Equal(["pollIntervalSeconds は整数で指定してください。既定値 1 を使います。"], result.Warnings);
    }

    [Fact]
    public void Load_StartupSyncMaxAgeZero_IsAccepted()
    {
        var result = LoadContent("""{ "startupSyncMaxAgeMinutes": 0 }""");

        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.Settings.StartupSyncMaxAgeMinutes);
    }

    [Fact]
    public void Load_InvalidLogLevel_FallsBackToInfoWithWarning()
    {
        var result = LoadContent("""{ "logLevel": "Verbose" }""");

        Assert.Equal(LogLevel.Info, result.Settings.LogLevel);
        Assert.Equal(["logLevel が不正です: Verbose。既定値 Info を使います。"], result.Warnings);
    }

    [Fact]
    public void Load_LowercaseLogLevel_IsAccepted()
    {
        var result = LoadContent("""{ "logLevel": "debug" }""");

        Assert.Empty(result.Warnings);
        Assert.Equal(LogLevel.Debug, result.Settings.LogLevel);
    }

    // 旧版は値を加工せずに照合するため、前後の空白は不正、文字列でない値は文字列の警告になる（実測）
    [Theory]
    [InlineData("\" Info \"", "logLevel が不正です:  Info 。既定値 Info を使います。")]
    [InlineData("5", "logLevel は空でない文字列で指定してください。既定値を使います。")]
    public void Load_LogLevelNotExactName_FallsBackWithLegacyWarning(string json, string expectedWarning)
    {
        var result = LoadContent($$"""{ "logLevel": {{json}} }""");

        Assert.Equal(LogLevel.Info, result.Settings.LogLevel);
        Assert.Equal([expectedWarning], result.Warnings);
    }

    [Fact]
    public void Load_WarningUsesKeyNameAsWritten()
    {
        var result = LoadContent("""{ "POLLINTERVALSECONDS": 0 }""");

        Assert.Equal(["POLLINTERVALSECONDS は 1 〜 60 の範囲で指定してください。既定値 1 を使います。"], result.Warnings);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsWithWarning()
    {
        var path = Path.Combine(_dir, "missing.json");

        var result = SettingsLoader.Load(path);

        Assert.Equal(ExpandedDefault, result.Settings);
        Assert.Equal([$"config.json が見つかりません: {path}。既定値で動作します。"], result.Warnings);
    }

    [Fact]
    public void Load_BrokenJson_ReturnsDefaultsWithParseWarning()
    {
        var result = LoadContent("{ this is not json ");

        Assert.Equal(ExpandedDefault, result.Settings);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("解析", warning);
        Assert.StartsWith("config.json を解析できません: ", warning);
        Assert.EndsWith("。既定値で動作します。", warning);
    }

    // 旧版は空・空白だけ・null の内容を警告なしの既定値として扱う（実測）
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void Load_EmptyOrNullContent_ReturnsDefaultsWithoutWarnings(string content)
    {
        var result = LoadContent(content);

        Assert.Empty(result.Warnings);
        Assert.Equal(ExpandedDefault, result.Settings);
    }

    // ルートがオブジェクトでない JSON は解析の警告にする（旧版は PowerShell 固有の型のプロパティ名を未知のキーとして警告する）
    [Theory]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("\"abc\"")]
    public void Load_NonObjectRoot_ReturnsDefaultsWithParseWarning(string content)
    {
        var result = LoadContent(content);

        Assert.Equal(ExpandedDefault, result.Settings);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("config.json を解析できません: ", warning);
    }

    [Fact]
    public void Load_DuplicateKey_UsesLastValue()
    {
        var result = LoadContent("""{ "budsDevice": "a", "budsDevice": "b" }""");

        Assert.Empty(result.Warnings);
        Assert.Equal("b", result.Settings.BudsDevice);
    }

    // plan の規則（キーの大文字小文字を区別しない）に従い後の値を使う。旧版はこの JSON を解析エラーにする
    [Fact]
    public void Load_DuplicateKeyDifferingOnlyInCase_UsesLastValue()
    {
        var result = LoadContent("""{ "budsDevice": "a", "BUDSDEVICE": "b" }""");

        Assert.Empty(result.Warnings);
        Assert.Equal("b", result.Settings.BudsDevice);
    }

    // 旧版は ConvertFrom-Json が重複を後の値で解決してから検証する。重複キーは最後の値だけを1回検証する
    [Fact]
    public void Load_DuplicateKey_ValidThenInvalid_UsesDefaultWithOneWarning()
    {
        var result = LoadContent("""{ "pollIntervalSeconds": 2, "pollIntervalSeconds": 0 }""");

        Assert.Equal(1, result.Settings.PollIntervalSeconds);
        Assert.Equal(["pollIntervalSeconds は 1 〜 60 の範囲で指定してください。既定値 1 を使います。"], result.Warnings);
    }

    [Fact]
    public void Load_DuplicateKey_InvalidThenValid_UsesLastValueWithoutWarnings()
    {
        var result = LoadContent("""{ "pollIntervalSeconds": 0, "pollIntervalSeconds": 2 }""");

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Settings.PollIntervalSeconds);
    }

    [Fact]
    public void Load_DuplicateKey_ValidThenWrongType_UsesDefaultWithOneWarning()
    {
        var result = LoadContent("""{ "switchOnConnect": false, "switchOnConnect": "yes" }""");

        Assert.True(result.Settings.SwitchOnConnect);
        Assert.Equal(["switchOnConnect は true / false で指定してください。既定値 True を使います。"], result.Warnings);
    }

    // 大文字小文字だけが違う重複も同じ規則。警告には最後に現れたキー名を使う
    [Fact]
    public void Load_DuplicateKeyDifferingOnlyInCase_ValidThenInvalid_WarnsWithLastKeyName()
    {
        var result = LoadContent("""{ "pollIntervalSeconds": 2, "POLLINTERVALSECONDS": 0 }""");

        Assert.Equal(1, result.Settings.PollIntervalSeconds);
        Assert.Equal(["POLLINTERVALSECONDS は 1 〜 60 の範囲で指定してください。既定値 1 を使います。"], result.Warnings);
    }

    // 未知のキーの重複は1件の警告にまとめる（完全一致は旧版と同じ。大文字小文字だけの違いも同じ規則で最後のキー名）
    [Theory]
    [InlineData("""{ "unknownKey": 1, "unknownKey": 2 }""", "未知の設定キーを無視します: unknownKey")]
    [InlineData("""{ "unknownKey": 1, "UNKNOWNKEY": 2 }""", "未知の設定キーを無視します: UNKNOWNKEY")]
    public void Load_DuplicateUnknownKey_WarnsOnce(string content, string expectedWarning)
    {
        var result = LoadContent(content);

        Assert.Equal(ExpandedDefault, result.Settings);
        Assert.Equal([expectedWarning], result.Warnings);
    }

    [Fact]
    public void Load_BomUtf8JapaneseDeviceName_IsReadCorrectly()
    {
        var result = LoadContent("""{ "budsDevice": "スピーカー (INZONE Buds - Game)" }""");

        Assert.Empty(result.Warnings);
        Assert.Equal("スピーカー (INZONE Buds - Game)", result.Settings.BudsDevice);
    }

    [Fact]
    public void Load_RepositoryConfigJson_MatchesDefaultsWithoutWarnings()
    {
        // テストプロジェクトがリポジトリの config.json をリンクして出力へコピーする
        var path = Path.Combine(AppContext.BaseDirectory, "repo", "config.json");
        Assert.True(File.Exists(path), path);

        var result = SettingsLoader.Load(path);

        Assert.Empty(result.Warnings);
        Assert.Equal(ExpandedDefault, result.Settings);
    }

    [Fact]
    public void CreateDefaultFileIfMissing_FolderMissing_CreatesLoadableDefaultFile()
    {
        var folder = Path.Combine(_dir, "AutoSwitchForInzone", "nested");
        var path = Path.Combine(folder, "config.json");

        Assert.True(SettingsLoader.CreateDefaultFileIfMissing(path));

        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(path));
        var result = SettingsLoader.Load(path);
        Assert.Empty(result.Warnings);
        Assert.Equal(ExpandedDefault, result.Settings);
    }

    [Fact]
    public void CreateDefaultFileIfMissing_WritesSameKeyOrderAndValuesAsRepositoryConfig()
    {
        var path = Path.Combine(_dir, "config.json");
        Assert.True(SettingsLoader.CreateDefaultFileIfMissing(path));

        using var created = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        using var repository = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "repo", "config.json"), Encoding.UTF8));

        Assert.Equal(
            repository.RootElement.EnumerateObject().Select(p => p.Name),
            created.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.True(JsonElement.DeepEquals(repository.RootElement, created.RootElement));
    }

    [Fact]
    public void CreateDefaultFileIfMissing_ExistingFile_ReturnsFalseAndKeepsContent()
    {
        var path = Path.Combine(_dir, "config.json");
        var original = Encoding.UTF8.GetBytes("""{ "budsDevice": "mine" }""");
        File.WriteAllBytes(path, original);

        Assert.False(SettingsLoader.CreateDefaultFileIfMissing(path));

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    // ---- 録音の切り替え先（Issue rurute/inzone-auto-switch#8） ----

    [Fact]
    public void Default_CaptureTargetsAreUnset()
    {
        Assert.Equal(string.Empty, AppSettings.Default.BudsInputDevice);
        Assert.Equal(string.Empty, AppSettings.Default.SpeakerInputDevice);
    }

    [Fact]
    public void Load_CaptureKeys_UsesSpecifiedValuesWithoutWarnings()
    {
        var result = LoadContent("""
            {
              "budsInputDevice": "ヘッドセット マイク (INZONE Buds - Chat)",
              "speakerInputDevice": "マイク (USB Microphone)"
            }
            """);

        Assert.Empty(result.Warnings);
        Assert.Equal(
            ExpandedDefault with
            {
                BudsInputDevice = "ヘッドセット マイク (INZONE Buds - Chat)",
                SpeakerInputDevice = "マイク (USB Microphone)",
            },
            result.Settings);
    }

    [Fact]
    public void Load_CaptureKeysMissing_AreUnsetWithoutWarnings()
    {
        var result = LoadContent("""{ "budsDevice": "X" }""");

        Assert.Empty(result.Warnings);
        Assert.Equal(string.Empty, result.Settings.BudsInputDevice);
        Assert.Equal(string.Empty, result.Settings.SpeakerInputDevice);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"\\t\"")]
    public void Load_CaptureKeysEmptyOrWhitespace_AreUnsetWithoutWarnings(string json)
    {
        var result = LoadContent($$"""{ "budsInputDevice": {{json}}, "speakerInputDevice": {{json}} }""");

        Assert.Empty(result.Warnings);
        Assert.Equal(string.Empty, result.Settings.BudsInputDevice);
        Assert.Equal(string.Empty, result.Settings.SpeakerInputDevice);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Load_CaptureKeyWrongType_IsUnsetWithWarning(string json)
    {
        var result = LoadContent($$"""{ "budsInputDevice": {{json}} }""");

        Assert.Equal(string.Empty, result.Settings.BudsInputDevice);
        Assert.Equal(["budsInputDevice は文字列で指定してください。未設定として扱います。"], result.Warnings);
    }

    [Fact]
    public void Load_CaptureKeyWithDifferentCase_IsAccepted()
    {
        var result = LoadContent("""{ "SpeakerInputDevice": "Mic" }""");

        Assert.Empty(result.Warnings);
        Assert.Equal("Mic", result.Settings.SpeakerInputDevice);
    }

    [Fact]
    public void Load_CaptureKeyDuplicate_UsesLastValue()
    {
        var result = LoadContent("""{ "budsInputDevice": "First", "BUDSINPUTDEVICE": "Last" }""");

        Assert.Empty(result.Warnings);
        Assert.Equal("Last", result.Settings.BudsInputDevice);
    }

    [Fact]
    public void Load_CaptureKeyDuplicate_ValidThenWrongType_IsUnsetWithOneWarning()
    {
        var result = LoadContent("""{ "budsInputDevice": "Mic", "budsInputDevice": 5 }""");

        Assert.Equal(string.Empty, result.Settings.BudsInputDevice);
        Assert.Equal(["budsInputDevice は文字列で指定してください。未設定として扱います。"], result.Warnings);
    }

    [Fact]
    public void DescribeCaptureSwitching_BothSet_ListsBothNames()
    {
        var settings = AppSettings.Default with { BudsInputDevice = "Buds Mic", SpeakerInputDevice = "PC Mic" };

        Assert.Equal("録音の切り替え: 接続時=Buds Mic / 切断時=PC Mic", settings.DescribeCaptureSwitching());
    }

    [Fact]
    public void DescribeCaptureSwitching_Unset_ShowsUnsetForEachDirection()
    {
        Assert.Equal("録音の切り替え: 接続時=（未設定） / 切断時=（未設定）", AppSettings.Default.DescribeCaptureSwitching());
        Assert.Equal(
            "録音の切り替え: 接続時=Buds Mic / 切断時=（未設定）",
            (AppSettings.Default with { BudsInputDevice = "Buds Mic", SpeakerInputDevice = "  " }).DescribeCaptureSwitching());
    }
}
