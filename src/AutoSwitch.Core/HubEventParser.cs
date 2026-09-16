using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoSwitch.Core;

public sealed record HubEvent(string LogId, long TimeStampMs, HeadsetState State);

/// <summary>
/// ActionLog.log の行から接続・切断イベントを取り出す。規則は旧版 SonyEventProvider.psm1 に合わせる。
/// </summary>
public static class HubEventParser
{
    // 1 行 1KB の JSON をすべて解析するのは無駄なので、旧版と同じ条件で対象行を先に絞り込む。
    // PowerShell の -match と同じく大文字小文字を区別しない
    private static readonly Regex EventLinePattern = new(
        "\"actionTypeId\"\\s*:\\s*2301[12]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IntegerToken = new("^-?[0-9]+$", RegexOptions.CultureInvariant);

    private const long ConnectDevice = 23011;
    private const long DisconnectDevice = 23012;

    /// <summary>
    /// 追記順を保つ。不正行・対象外・logId なし・既読は除く。既読判定のため <paramref name="seen"/> を更新する。
    /// </summary>
    public static IReadOnlyList<HubEvent> Parse(IEnumerable<string>? lines, SeenLogIdSet seen)
    {
        ArgumentNullException.ThrowIfNull(seen);

        var events = new List<HubEvent>();
        if (lines is null)
        {
            return events;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || !EventLinePattern.IsMatch(line))
            {
                continue;
            }

            var parsed = TryParseLine(line);
            if (parsed is null || !seen.Add(parsed.LogId))
            {
                continue;
            }

            events.Add(parsed);
        }

        return events;
    }

    private static HubEvent? TryParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // 旧版は [string] にした actionTypeId を '23011' / '23012' と照合する。文字列の "23011" は絞り込みで落ちる
            if (!TryGetProperty(root, "actionTypeId", out var actionTypeId)
                || actionTypeId.ValueKind != JsonValueKind.Number
                || !actionTypeId.TryGetInt64(out var actionTypeIdValue))
            {
                return null;
            }

            HeadsetState state;
            switch (actionTypeIdValue)
            {
                case ConnectDevice:
                    state = HeadsetState.Connected;
                    break;
                case DisconnectDevice:
                    state = HeadsetState.Disconnected;
                    break;
                default:
                    return null;
            }

            if (!TryGetProperty(root, "logId", out var logIdElement))
            {
                return null;
            }

            var logId = ConvertLogId(logIdElement);
            if (string.IsNullOrWhiteSpace(logId))
            {
                return null;
            }

            return new HubEvent(logId, ReadTimeStampMs(root), state);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 旧版の [string] 変換のうち、実測で結果を確認した範囲だけを再現する。
    /// 小数・指数表記の数値、オブジェクト、配列は旧版と異なり除く（作者承認の既知の差異）。
    /// </summary>
    private static string? ConvertLogId(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.True:
                return "True";
            case JsonValueKind.False:
                return "False";
            case JsonValueKind.Number:
                var raw = value.GetRawText();
                if (!IntegerToken.IsMatch(raw))
                {
                    return null;
                }
                // long に収まる値は旧版と同じく数値として文字列化する（-0 は 0）。収まらない値は表記どおり
                return value.TryGetInt64(out var number) ? number.ToString(CultureInfo.InvariantCulture) : raw;
            default:
                // null・オブジェクト・配列
                return null;
        }
    }

    // 旧版は [long]::TryParse([string]$obj.timeStamp) で読み、読めなければ 0 にする
    private static long ReadTimeStampMs(JsonElement root)
    {
        if (!TryGetProperty(root, "timeStamp", out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    // 旧版の PowerShell のプロパティ参照は大文字小文字を区別しない。
    // 同名が複数あるときは、旧版の ConvertFrom-Json（Task 5 の旧版実測 dup-exact）と
    // SettingsLoader に合わせて、最後に現れた値を採る
    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        var found = false;
        value = default;
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }
}
