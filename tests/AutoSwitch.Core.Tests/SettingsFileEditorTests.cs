using System.Text;

namespace AutoSwitch.Core.Tests;

// 実際のファイルで検証する（spec §5・§8）。各テストは一時ディレクトリを作り、終了時に属性を戻して削除する
public sealed class SettingsFileEditorTests : IDisposable
{
    private static readonly SwitchSettings Saved = new(
        "スピーカー (NEW Buds)", "スピーカー (NEW Speaker)", "マイク (NEW Buds)", "", false, true);

    private readonly string _dir;
    private readonly string _path;

    public SettingsFileEditorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cfgedit-" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.json");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_dir))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string TempPath => _path + ".tmp";

    // 手で編集した config.json を想定し、BOM 付き UTF-8・CRLF で書く
    private void WriteConfig(string content) =>
        File.WriteAllText(_path, content.ReplaceLineEndings("\r\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    private byte[] ReadBytes() => File.ReadAllBytes(_path);

    private string ReadText() => Encoding.UTF8.GetString(ReadBytes().AsSpan(3));

    // ---- 保存: 書き換えの規則 ----

    [Fact]
    public void Save_ReplacesOnlySevenKeys_KeepsOtherKeysOrderAndValues()
    {
        WriteConfig("""
            {
              "budsDevice": "スピーカー (INZONE Buds - Game)",
              "extra": { "nested": [1, 2.50, "x"] },
              "speakerDevice": "スピーカー (Realtek(R) Audio)",
              "switchOnConnect": true,
              "switchOnDisconnect": false,
              "actionLogPath": "%APPDATA%\\Sony\\INZONE Hub\\ActionLog.log",
              "pollIntervalSeconds": 1E0,
              "idlePollIntervalSeconds": "5",
              "logLevel": "info",
              "budsInputDevice": "ヘッドセット マイク (INZONE Buds - Chat)",
              "speakerInputDevice": "マイク (USB Microphone)",
              "maxLogSizeKB": 1024
            }
            """);

        var result = SettingsFileEditor.Save(_path, Saved);

        Assert.Equal(SettingsSaveResult.Success, result);
        Assert.Equal(
            "{\n" +
            "  \"budsDevice\": \"スピーカー (NEW Buds)\",\n" +
            "  \"extra\": {\n" +
            "    \"nested\": [\n" +
            "      1,\n" +
            "      2.50,\n" +
            "      \"x\"\n" +
            "    ]\n" +
            "  },\n" +
            "  \"speakerDevice\": \"スピーカー (NEW Speaker)\",\n" +
            "  \"switchOnConnect\": false,\n" +
            "  \"switchOnDisconnect\": true,\n" +
            "  \"actionLogPath\": \"%APPDATA%\\\\Sony\\\\INZONE Hub\\\\ActionLog.log\",\n" +
            "  \"pollIntervalSeconds\": 1E0,\n" +
            "  \"idlePollIntervalSeconds\": \"5\",\n" +
            "  \"logLevel\": \"info\",\n" +
            "  \"budsInputDevice\": \"マイク (NEW Buds)\",\n" +
            "  \"speakerInputDevice\": \"\",\n" +
            "  \"maxLogSizeKB\": 1024,\n" +
            "  \"showTaskbarBattery\": false\n" +
            "}\n",
            ReadText());
    }

    [Fact]
    public void Save_CaseVariantAndDuplicateSwitchKeys_WritesCanonicalKeyOnceAtFirstPosition()
    {
        WriteConfig("""
            {
              "BUDSDEVICE": "old 1",
              "logLevel": "Info",
              "budsDevice": "old 2",
              "SwitchOnConnect": true,
              "logLevel": "Debug"
            }
            """);

        var result = SettingsFileEditor.Save(_path, Saved);

        Assert.True(result.Succeeded);
        // 7項目以外の重複（logLevel）はそのまま残す。無かった5キーは最後に spec §3 の順（showTaskbarBattery は最後）で足す
        Assert.Equal(
            "{\n" +
            "  \"budsDevice\": \"スピーカー (NEW Buds)\",\n" +
            "  \"logLevel\": \"Info\",\n" +
            "  \"switchOnConnect\": false,\n" +
            "  \"logLevel\": \"Debug\",\n" +
            "  \"budsInputDevice\": \"マイク (NEW Buds)\",\n" +
            "  \"speakerDevice\": \"スピーカー (NEW Speaker)\",\n" +
            "  \"speakerInputDevice\": \"\",\n" +
            "  \"switchOnDisconnect\": true,\n" +
            "  \"showTaskbarBattery\": false\n" +
            "}\n",
            ReadText());
    }

    [Fact]
    public void Save_EmptyObject_AppendsSevenKeysInSpecOrder()
    {
        WriteConfig("{}");

        var result = SettingsFileEditor.Save(_path, Saved with { SpeakerInputDevice = "   " });

        Assert.True(result.Succeeded);
        Assert.Equal(
            "{\n" +
            "  \"budsDevice\": \"スピーカー (NEW Buds)\",\n" +
            "  \"budsInputDevice\": \"マイク (NEW Buds)\",\n" +
            "  \"speakerDevice\": \"スピーカー (NEW Speaker)\",\n" +
            "  \"speakerInputDevice\": \"\",\n" +
            "  \"switchOnConnect\": false,\n" +
            "  \"switchOnDisconnect\": true,\n" +
            "  \"showTaskbarBattery\": false\n" +
            "}\n",
            ReadText());
    }

    [Fact]
    public void Save_WritesBomUtf8WithLfAndUnescapedJapanese()
    {
        WriteConfig("{}");

        SettingsFileEditor.Save(_path, Saved);

        var bytes = ReadBytes();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Contains("スピーカー (NEW Buds)", ReadText());
        Assert.EndsWith("}\n", ReadText());
        Assert.False(ReadText().EndsWith("}\n\n"));
    }

    [Fact]
    public void Save_ThenLoad_SixItemsAreSavedValuesAndOthersUnchanged()
    {
        WriteConfig("""
            {
              "budsDevice": "A",
              "speakerDevice": "B",
              "pollIntervalSeconds": 3,
              "idlePollIntervalSeconds": 9,
              "startupSyncMaxAgeMinutes": 0,
              "logLevel": "Debug",
              "maxLogSizeKB": 128,
              "actionLogPath": "C:\\logs\\a.log",
              "unknownKey": 1
            }
            """);
        var before = SettingsLoader.Load(_path);

        SettingsFileEditor.Save(_path, Saved);
        var after = SettingsLoader.Load(_path);

        Assert.Equal(Saved.ApplyTo(before.Settings), after.Settings);
        Assert.Equal(before.Warnings, after.Warnings);
    }

    [Fact]
    public void Save_ShowTaskbarBattery_WritesKeyAndKeepsOthers()
    {
        WriteConfig("""
            { "budsDevice": "A", "unknownKey": [1, 2], "showTaskbarBattery": false, "maxLogSizeKB": 2.0E3 }
            """);

        var result = SettingsFileEditor.Save(_path, Saved with { ShowTaskbarBattery = true });

        Assert.True(result.Succeeded);
        var text = ReadText();
        Assert.Contains("\"unknownKey\": [", text);
        Assert.Contains("\"maxLogSizeKB\": 2.0E3", text);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(2, names.IndexOf("showTaskbarBattery"));
        Assert.True(doc.RootElement.GetProperty("showTaskbarBattery").GetBoolean());
        Assert.True(SettingsLoader.Load(_path).Settings.ShowTaskbarBattery);
    }

    [Fact]
    public void Save_ShowTaskbarBatteryMissing_AppendsAtEnd()
    {
        WriteConfig("""
            { "budsDevice": "A", "speakerDevice": "B", "budsInputDevice": "", "speakerInputDevice": "",
              "switchOnConnect": true, "switchOnDisconnect": true, "logLevel": "Info" }
            """);

        var result = SettingsFileEditor.Save(_path, Saved with { ShowTaskbarBattery = true });

        Assert.True(result.Succeeded);
        using var doc = System.Text.Json.JsonDocument.Parse(ReadText());
        var last = doc.RootElement.EnumerateObject().Last();
        Assert.Equal("showTaskbarBattery", last.Name);
        Assert.Equal(System.Text.Json.JsonValueKind.True, last.Value.ValueKind);
    }

    // ---- 保存: 読み直しで止まる場合 ----

    [Fact]
    public void Save_FileMissing_FailsWithoutCreatingFiles()
    {
        var result = SettingsFileEditor.Save(_path, Saved);

        Assert.False(result.Succeeded);
        Assert.Equal($"config.json が見つかりません: {_path}", result.Error);
        Assert.False(File.Exists(_path));
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public void Save_FailureError_MaskedForLog_HidesFolderPath()
    {
        // 保存の失敗理由はパスを含む。ログへは LogPathMasker（AppPaths.ForLog）を通してから書く
        var result = SettingsFileEditor.Save(_path, Saved);

        var forLog = LogPathMasker.Mask(result.Error!, _dir, null);

        Assert.Equal(@"config.json が見つかりません: %LOCALAPPDATA%" + Path.DirectorySeparatorChar + "config.json", forLog);
        Assert.DoesNotContain(_dir, forLog, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{ \"budsDevice\": ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    public void Save_BrokenOrNonObjectFile_FailsWithoutChangingFile(string content)
    {
        WriteConfig(content);
        var before = ReadBytes();

        var result = SettingsFileEditor.Save(_path, Saved);

        Assert.False(result.Succeeded);
        Assert.StartsWith("config.json ", result.Error);
        Assert.Equal(before, ReadBytes());
        Assert.False(File.Exists(TempPath));
    }

    [Theory]
    [InlineData("", "SPEAKER")]
    [InlineData("BUDS", " ")]
    public void Save_EmptyPlaybackDevice_FailsWithoutChangingFile(string buds, string speaker)
    {
        WriteConfig("{}");
        var before = ReadBytes();

        var result = SettingsFileEditor.Save(_path, Saved with { BudsDevice = buds, SpeakerDevice = speaker });

        Assert.Equal(SettingsSaveResult.Failure("再生デバイスが選ばれていません。"), result);
        Assert.Equal(before, ReadBytes());
    }

    // ---- 保存: 書き込み・置き換えの失敗 ----

    [Fact]
    public void Save_ReadOnlyConfig_FailsWithoutChangingFileAndRemovesTemp()
    {
        WriteConfig("{}");
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        var before = ReadBytes();

        var result = SettingsFileEditor.Save(_path, Saved);

        Assert.False(result.Succeeded);
        Assert.StartsWith("config.json を書き込めません: ", result.Error);
        Assert.DoesNotContain("一時ファイルが残っています", result.Error);
        Assert.Equal(before, ReadBytes());
        Assert.False(File.Exists(TempPath));
    }

    // 削除を共有しない開き方（FileShare.Delete なし）で別のハンドルが開いていると、名前変更の上書きは失敗する
    [Fact]
    public void Save_ConfigOpenWithoutDeleteSharing_FailsWithoutChangingFileAndRemovesTemp()
    {
        WriteConfig("{}");
        var before = ReadBytes();
        SettingsSaveResult result;
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = SettingsFileEditor.Save(_path, Saved);
        }

        Assert.False(result.Succeeded);
        Assert.StartsWith("config.json を書き込めません: ", result.Error);
        Assert.Equal(before, ReadBytes());
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public void Save_ReadOnlyTempLeftBehind_FailsWithoutChangingFileAndReportsTemp()
    {
        WriteConfig("{}");
        File.WriteAllText(TempPath, "stale");
        File.SetAttributes(TempPath, FileAttributes.ReadOnly);
        var before = ReadBytes();

        var result = SettingsFileEditor.Save(_path, Saved);

        Assert.False(result.Succeeded);
        Assert.StartsWith("config.json を書き込めません: ", result.Error);
        Assert.EndsWith($" 一時ファイルが残っています: {TempPath}", result.Error);
        Assert.Equal(before, ReadBytes());
    }

    [Fact]
    public void Save_WritableTempLeftBehind_OverwritesAndSucceeds()
    {
        WriteConfig("{}");
        File.WriteAllText(TempPath, "stale");

        var result = SettingsFileEditor.Save(_path, Saved);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(TempPath));
        Assert.Contains("スピーカー (NEW Buds)", ReadText());
    }

    // ---- 開いてから保存までの外部の編集（spec §5 手順5） ----

    [Fact]
    public void Save_AfterExternalEdit_SixItemsFromWindowOthersFromFileAtSaveTime()
    {
        WriteConfig("""
            { "budsDevice": "A", "speakerDevice": "B", "logLevel": "Info", "maxLogSizeKB": 1024 }
            """);
        var opened = SettingsFileEditor.Open(_path);
        WriteConfig("""
            { "budsDevice": "EDITED", "speakerDevice": "B", "logLevel": "Debug", "maxLogSizeKB": 2048, "added": true }
            """);

        var result = SettingsFileEditor.Save(_path, opened.Values with { BudsDevice = "FROM WINDOW" });

        Assert.True(result.Succeeded);
        var loaded = SettingsLoader.Load(_path);
        Assert.Equal("FROM WINDOW", loaded.Settings.BudsDevice);
        Assert.Equal(LogLevel.Debug, loaded.Settings.LogLevel);
        Assert.Equal(2048, loaded.Settings.MaxLogSizeKB);
        Assert.Contains("\"added\": true", ReadText());
    }

    [Fact]
    public void Save_FileDeletedAfterOpen_FailsWithoutCreatingFile()
    {
        WriteConfig("{}");
        var opened = SettingsFileEditor.Open(_path);
        File.Delete(_path);

        var result = SettingsFileEditor.Save(_path, opened.Values);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Save_FileBrokenAfterOpen_FailsWithoutChangingFile()
    {
        WriteConfig("{}");
        var opened = SettingsFileEditor.Open(_path);
        WriteConfig("{ broken");
        var before = ReadBytes();

        var result = SettingsFileEditor.Save(_path, opened.Values);

        Assert.False(result.Succeeded);
        Assert.Equal(before, ReadBytes());
    }

    // 引数の誤りはプログラムの誤りなので、結果に変換せず例外にする（AudioSwitcher の範囲外の DeviceFlow と同じ扱い）
    [Fact]
    public void OpenAndSave_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentException>(() => SettingsFileEditor.Open(""));
        Assert.Throws<ArgumentNullException>(() => SettingsFileEditor.Open(null!));
        Assert.Throws<ArgumentException>(() => SettingsFileEditor.Save("", Saved));
        Assert.Throws<ArgumentNullException>(() => SettingsFileEditor.Save(_path, null!));
    }

    // ---- 開く ----

    [Fact]
    public void Open_ValidFile_ReturnsLoaderValuesAndCanSave()
    {
        WriteConfig("""
            {
              "budsDevice": "A",
              "speakerDevice": "",
              "budsInputDevice": "MIC",
              "switchOnDisconnect": false
            }
            """);

        var opened = SettingsFileEditor.Open(_path);

        Assert.Equal(SwitchSettings.From(SettingsLoader.Load(_path).Settings), opened.Values);
        Assert.Null(opened.Problem);
        Assert.True(opened.CanSave);
    }

    [Fact]
    public void Open_ReadsShowTaskbarBattery()
    {
        WriteConfig("""{ "showTaskbarBattery": true }""");

        var opened = SettingsFileEditor.Open(_path);

        Assert.True(opened.Values.ShowTaskbarBattery);
    }

    [Fact]
    public void Open_FileMissing_CannotSave()
    {
        var opened = SettingsFileEditor.Open(_path);

        Assert.Equal($"config.json が見つかりません: {_path}", opened.Problem);
        Assert.False(opened.CanSave);
        Assert.Equal(SwitchSettings.From(AppSettings.Default), opened.Values);
        Assert.False(File.Exists(_path));
    }

    [Theory]
    [InlineData("{ \"budsDevice\": ", "config.json を解析できません: ")]
    [InlineData("   ", "config.json を解析できません: ")]
    [InlineData("[]", "config.json のルートがオブジェクトではありません。")]
    [InlineData("null", "config.json のルートがオブジェクトではありません。")]
    public void Open_BrokenOrNonObjectFile_CannotSave(string content, string expectedPrefix)
    {
        WriteConfig(content);

        var opened = SettingsFileEditor.Open(_path);

        Assert.False(opened.CanSave);
        Assert.StartsWith(expectedPrefix, opened.Problem);
    }
}
