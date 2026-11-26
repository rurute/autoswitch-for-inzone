using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoSwitch.Core;

/// <summary>設定画面を開くときの読み込み結果。Problem が null でなければ保存できない（spec §4）</summary>
public sealed record SettingsEditSource(SwitchSettings Values, string? Problem)
{
    public bool CanSave => Problem is null;
}

public sealed record SettingsSaveResult(bool Succeeded, string? Error)
{
    public static SettingsSaveResult Success { get; } = new(true, null);

    public static SettingsSaveResult Failure(string error) => new(false, error);
}

/// <summary>
/// 設定画面のための config.json の読み込みと、7項目だけの書き換え（Issue rurute/inzone-auto-switch#9、spec §5。7項目目の showTaskbarBattery は Issue rurute/inzone-auto-switch#27）。
/// ファイルの読み書きの失敗は結果に変換し、例外を外へ出さない。引数の誤り（null・空のパス、null の値）はプログラムの誤りなので例外にする。
/// </summary>
public static class SettingsFileEditor
{
    // spec §3 の表の順。showTaskbarBattery（Issue rurute/inzone-auto-switch#27）は最後。ファイルに無いキーはこの順で最後に足す
    private static readonly string[] SwitchKeys =
    [
        "budsDevice", "budsInputDevice", "speakerDevice", "speakerInputDevice", "switchOnConnect", "switchOnDisconnect",
        "showTaskbarBattery",
    ];

    /// <summary>
    /// 7項目は SettingsLoader.Load と同じ値を返す。ファイルが無い・読めない・解析できない・ルートがオブジェクトでないときは
    /// Problem に理由を入れる（Load はこれらの場合も既定値を返すため、保存できるかは別に判定する）
    /// </summary>
    public static SettingsEditSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var values = SwitchSettings.From(SettingsLoader.Load(path).Settings);
        string? problem = null;
        try
        {
            using var _ = ReadRoot(path);
        }
        catch (SettingsFileException ex)
        {
            problem = ex.Message;
        }
        return new SettingsEditSource(values, problem);
    }

    /// <summary>
    /// 保存の直前に読み直し、7項目のキーだけを書き換える。同じフォルダの一時ファイルに書いてから名前変更で置き換える。
    /// 失敗しても config.json は変わらない
    /// </summary>
    public static SettingsSaveResult Save(string path, SwitchSettings values)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(values);

        if (string.IsNullOrWhiteSpace(values.BudsDevice) || string.IsNullOrWhiteSpace(values.SpeakerDevice))
        {
            return SettingsSaveResult.Failure("再生デバイスが選ばれていません。");
        }

        JsonDocument document;
        try
        {
            document = ReadRoot(path);
        }
        catch (SettingsFileException ex)
        {
            return SettingsSaveResult.Failure(ex.Message);
        }

        var tempPath = path + ".tmp";
        using (document)
        {
            try
            {
                WriteTemp(tempPath, document.RootElement, values);
                // File.Replace（ReplaceFileW）は失敗の段階によって元のファイルが元のパスに残らないことがあるため使わない。
                // 上書きの名前変更は、失敗したら config.json を変えずに終わる（spec §5 手順4）
                File.Move(tempPath, path, overwrite: true);
                return SettingsSaveResult.Success;
            }
            catch (Exception ex)
            {
                var error = $"config.json を書き込めません: {ex.Message}";
                if (!TryDeleteTemp(tempPath))
                {
                    error += $" 一時ファイルが残っています: {tempPath}";
                }
                return SettingsSaveResult.Failure(error);
            }
        }
    }

    private static JsonDocument ReadRoot(string path)
    {
        if (!File.Exists(path))
        {
            throw new SettingsFileException($"config.json が見つかりません: {path}");
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new SettingsFileException($"config.json を読めません: {ex.Message}");
        }

        JsonDocument document;
        try
        {
            // SettingsLoader.Load と同じ既定の解析オプション（コメント・末尾のカンマを受け入れない）
            document = JsonDocument.Parse(text);
        }
        catch (Exception ex)
        {
            throw new SettingsFileException($"config.json を解析できません: {ex.Message}");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new SettingsFileException("config.json のルートがオブジェクトではありません。");
        }
        return document;
    }

    private static void WriteTemp(string tempPath, JsonElement root, SwitchSettings values)
    {
        using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        // 既定ファイル（SettingsLoader.CreateDefaultFileIfMissing）と同じ BOM・インデント・改行・エスケープ
        stream.Write(Encoding.UTF8.GetPreamble());
        var options = new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
            {
                var key = SwitchKeys.FirstOrDefault(k => string.Equals(k, property.Name, StringComparison.OrdinalIgnoreCase));
                if (key is null)
                {
                    // 7項目以外はキー名も値もそのまま（数値の表記、未知のキー、重複を含む）
                    property.WriteTo(writer);
                }
                else if (written.Add(key))
                {
                    // 最初に現れた位置に、正式な表記で1回だけ書く。2回目以降の出現は書かない
                    WriteSwitchValue(writer, key, values);
                }
            }

            foreach (var key in SwitchKeys.Where(k => !written.Contains(k)))
            {
                WriteSwitchValue(writer, key, values);
            }
            writer.WriteEndObject();
        }
        stream.Write("\n"u8);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteSwitchValue(Utf8JsonWriter writer, string key, SwitchSettings values)
    {
        switch (key)
        {
            case "budsDevice":
                writer.WriteString(key, values.BudsDevice);
                break;
            case "budsInputDevice":
                writer.WriteString(key, CaptureValue(values.BudsInputDevice));
                break;
            case "speakerDevice":
                writer.WriteString(key, values.SpeakerDevice);
                break;
            case "speakerInputDevice":
                writer.WriteString(key, CaptureValue(values.SpeakerInputDevice));
                break;
            case "switchOnConnect":
                writer.WriteBoolean(key, values.SwitchOnConnect);
                break;
            case "switchOnDisconnect":
                writer.WriteBoolean(key, values.SwitchOnDisconnect);
                break;
            case "showTaskbarBattery":
                writer.WriteBoolean(key, values.ShowTaskbarBattery);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, "7項目のキーではありません。");
        }
    }

    // 録音の未設定は空文字で書く（キーは残す）
    private static string CaptureValue(string deviceName) =>
        string.IsNullOrWhiteSpace(deviceName) ? string.Empty : deviceName;

    private static bool TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class SettingsFileException(string message) : Exception(message);
}
