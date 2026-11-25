using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoSwitch.Core;

public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Warnings);

/// <summary>
/// config.json の読み込みと既定ファイルの作成。規則と警告の文言は旧版 Config.psm1 に合わせる。
/// </summary>
public static class SettingsLoader
{
    /// <summary>
    /// 項目ごとに検証し、不正な項目だけ既定値へ戻して警告を残す。例外を外へ出さない。
    /// </summary>
    public static SettingsLoadResult Load(string path)
    {
        var warnings = new List<string>();
        var values = new Values();

        if (!File.Exists(path))
        {
            warnings.Add($"config.json が見つかりません: {path}。既定値で動作します。");
        }
        else
        {
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                // 旧版の ConvertFrom-Json は空・空白だけ・null の内容を $null にし、警告なしで既定値を使う
                if (!string.IsNullOrWhiteSpace(text))
                {
                    using var document = JsonDocument.Parse(text);
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        Apply(root, values, warnings);
                    }
                    else if (root.ValueKind != JsonValueKind.Null)
                    {
                        throw new JsonException("JSON のルートがオブジェクトではありません");
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"config.json を解析できません: {ex.Message}。既定値で動作します。");
                values = new Values();
            }
        }

        // 旧版は -notcontains で照合するため、logLevel の値は大文字小文字を区別しない
        var logLevel = Enum.GetValues<LogLevel>()
            .Where(level => string.Equals(level.ToString(), values.LogLevel, StringComparison.OrdinalIgnoreCase))
            .Select(level => (LogLevel?)level)
            .FirstOrDefault();
        if (logLevel is null)
        {
            warnings.Add($"logLevel が不正です: {values.LogLevel}。既定値 {AppSettings.Default.LogLevel} を使います。");
        }

        var settings = new AppSettings(
            values.BudsDevice,
            values.SpeakerDevice,
            values.SwitchOnConnect,
            values.SwitchOnDisconnect,
            values.PollIntervalSeconds,
            values.IdlePollIntervalSeconds,
            values.StartupSyncMaxAgeMinutes,
            logLevel ?? AppSettings.Default.LogLevel,
            values.MaxLogSizeKB,
            values.BudsInputDevice,
            values.SpeakerInputDevice);
        return new SettingsLoadResult(settings, warnings);
    }

    /// <summary>
    /// ファイルが無ければ、旧版 config.json のキー順から actionLogPath を抜き、AppSettings.Default の値で BOM 付き UTF-8 の既定ファイルを作る。
    /// 作成したら true。既存は上書きしない。失敗は例外。
    /// </summary>
    public static bool CreateDefaultFileIfMissing(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (File.Exists(path))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var d = AppSettings.Default;
        // 作者が手で編集するため、日本語をエスケープせず、旧版 config.json と同じ BOM・改行にする
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
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
            writer.WriteString("budsDevice", d.BudsDevice);
            writer.WriteString("speakerDevice", d.SpeakerDevice);
            writer.WriteBoolean("switchOnConnect", d.SwitchOnConnect);
            writer.WriteBoolean("switchOnDisconnect", d.SwitchOnDisconnect);
            writer.WriteNumber("pollIntervalSeconds", d.PollIntervalSeconds);
            writer.WriteNumber("idlePollIntervalSeconds", d.IdlePollIntervalSeconds);
            writer.WriteNumber("startupSyncMaxAgeMinutes", d.StartupSyncMaxAgeMinutes);
            writer.WriteString("logLevel", d.LogLevel.ToString());
            writer.WriteNumber("maxLogSizeKB", d.MaxLogSizeKB);
            writer.WriteEndObject();
        }
        stream.Write("\n"u8);
        return true;
    }

    // 旧版は ConvertFrom-Json が同じキーを後の値で解決してから、キーごとに1回だけ検証する。
    // キーは hashtable で照合するため大文字小文字を区別しない。
    // 検証の前に、大文字小文字を区別せず最後に現れた値だけを残す（位置は最初の出現、警告のキー名は最後の出現の表記）
    private static void Apply(JsonElement root, Values values, List<string> warnings)
    {
        var d = AppSettings.Default;
        var order = new List<string>();
        var lastByKey = new Dictionary<string, JsonProperty>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!lastByKey.ContainsKey(property.Name))
            {
                order.Add(property.Name);
            }
            lastByKey[property.Name] = property;
        }

        foreach (var property in order.Select(name => lastByKey[name]))
        {
            var key = property.Name;
            var value = property.Value;

            if (Is(key, "budsDevice"))
            {
                values.BudsDevice = ReadString(key, value, values.BudsDevice, warnings);
            }
            else if (Is(key, "speakerDevice"))
            {
                values.SpeakerDevice = ReadString(key, value, values.SpeakerDevice, warnings);
            }
            else if (Is(key, "switchOnConnect"))
            {
                values.SwitchOnConnect = ReadBool(key, value, values.SwitchOnConnect, d.SwitchOnConnect, warnings);
            }
            else if (Is(key, "switchOnDisconnect"))
            {
                values.SwitchOnDisconnect = ReadBool(key, value, values.SwitchOnDisconnect, d.SwitchOnDisconnect, warnings);
            }
            else if (Is(key, "actionLogPath"))
            {
                // 使わなくなった項目（Issue rurute/inzone-auto-switch#23）。作者の既存の config.json にあるため、検証も警告もせずに無視する
            }
            else if (Is(key, "pollIntervalSeconds"))
            {
                values.PollIntervalSeconds = ReadInt(key, value, values.PollIntervalSeconds, d.PollIntervalSeconds, AppSettings.PollIntervalSecondsRange, warnings);
            }
            else if (Is(key, "idlePollIntervalSeconds"))
            {
                values.IdlePollIntervalSeconds = ReadInt(key, value, values.IdlePollIntervalSeconds, d.IdlePollIntervalSeconds, AppSettings.IdlePollIntervalSecondsRange, warnings);
            }
            else if (Is(key, "startupSyncMaxAgeMinutes"))
            {
                values.StartupSyncMaxAgeMinutes = ReadInt(key, value, values.StartupSyncMaxAgeMinutes, d.StartupSyncMaxAgeMinutes, AppSettings.StartupSyncMaxAgeMinutesRange, warnings);
            }
            else if (Is(key, "logLevel"))
            {
                // 値の検証はすべてのキーを処理した後に行う（旧版と同じ順序）
                values.LogLevel = ReadString(key, value, values.LogLevel, warnings);
            }
            else if (Is(key, "maxLogSizeKB"))
            {
                values.MaxLogSizeKB = ReadInt(key, value, values.MaxLogSizeKB, d.MaxLogSizeKB, AppSettings.MaxLogSizeKBRange, warnings);
            }
            else if (Is(key, "budsInputDevice"))
            {
                values.BudsInputDevice = ReadOptionalString(key, value, warnings);
            }
            else if (Is(key, "speakerInputDevice"))
            {
                values.SpeakerInputDevice = ReadOptionalString(key, value, warnings);
            }
            else
            {
                warnings.Add($"未知の設定キーを無視します: {key}");
            }
        }
    }

    private static bool Is(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    // 旧版は $value -is [string] かつ Trim 後が空でないときだけ受け入れる。値そのものは加工しない
    private static string ReadString(string key, JsonElement value, string current, List<string> warnings)
    {
        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        warnings.Add($"{key} は空でない文字列で指定してください。既定値を使います。");
        return current;
    }

    // 録音の切り替え先（Issue rurute/inzone-auto-switch#8）。空と空白だけは未設定として警告なしで受け入れ、空文字にそろえる。
    // 文字列以外は警告して未設定にする。旧版に無い項目なので、既存の文字列の項目とは空の扱いが違う
    private static string ReadOptionalString(string key, JsonElement value, List<string> warnings)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
        }

        warnings.Add($"{key} は文字列で指定してください。未設定として扱います。");
        return string.Empty;
    }

    private static bool ReadBool(string key, JsonElement value, bool current, bool defaultValue, List<string> warnings)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        warnings.Add($"{key} は true / false で指定してください。既定値 {defaultValue} を使います。");
        return current;
    }

    private static int ReadInt(string key, JsonElement value, int current, int defaultValue, (int Min, int Max) range, List<string> warnings)
    {
        if (!TryReadInt(value, out var parsed))
        {
            warnings.Add($"{key} は整数で指定してください。既定値 {defaultValue} を使います。");
            return current;
        }

        if (parsed < range.Min || parsed > range.Max)
        {
            warnings.Add($"{key} は {range.Min} 〜 {range.Max} の範囲で指定してください。既定値 {defaultValue} を使います。");
            return current;
        }

        return parsed;
    }

    // 旧版は [int]::TryParse([string]$value) で読む。JSON の整数と、前後の空白・符号を含む整数の文字列を受け入れる。
    // 真偽値・null・配列・オブジェクト・小数や指数の表記は受け入れない
    private static bool TryReadInt(JsonElement value, out int parsed)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt32(out parsed);
            case JsonValueKind.String:
                return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
            default:
                parsed = 0;
                return false;
        }
    }

    private sealed class Values
    {
        public string BudsDevice { get; set; } = AppSettings.Default.BudsDevice;
        public string SpeakerDevice { get; set; } = AppSettings.Default.SpeakerDevice;
        public bool SwitchOnConnect { get; set; } = AppSettings.Default.SwitchOnConnect;
        public bool SwitchOnDisconnect { get; set; } = AppSettings.Default.SwitchOnDisconnect;
        public int PollIntervalSeconds { get; set; } = AppSettings.Default.PollIntervalSeconds;
        public int IdlePollIntervalSeconds { get; set; } = AppSettings.Default.IdlePollIntervalSeconds;
        public int StartupSyncMaxAgeMinutes { get; set; } = AppSettings.Default.StartupSyncMaxAgeMinutes;
        public string LogLevel { get; set; } = AppSettings.Default.LogLevel.ToString();
        public int MaxLogSizeKB { get; set; } = AppSettings.Default.MaxLogSizeKB;
        public string BudsInputDevice { get; set; } = AppSettings.Default.BudsInputDevice;
        public string SpeakerInputDevice { get; set; } = AppSettings.Default.SpeakerInputDevice;
    }
}
