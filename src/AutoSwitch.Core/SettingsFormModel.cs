namespace AutoSwitch.Core;

/// <summary>選択肢の種類。Device = 一覧にある、NotConnected = 一覧に無い設定、Unlisted = 一覧を取得できなかった設定、NotSet = 録音の未設定</summary>
public enum DeviceChoiceKind { Device, NotConnected, Unlisted, NotSet }

/// <summary>選択肢1つ。Label は画面に出す文字列、Value は保存する値（NotSet は空文字）</summary>
public sealed record DeviceChoice(string Label, string Value, DeviceChoiceKind Kind)
{
    public override string ToString() => Label;
}

/// <summary>流れ（再生・録音）ひとつ分のデバイス名の一覧。取得に失敗したら Error に理由を入れ、Names は空</summary>
public sealed record DeviceNameList(IReadOnlyList<string> Names, string? Error)
{
    public static DeviceNameList Available(IEnumerable<string> names) => new(names.ToList(), null);

    public static DeviceNameList Unavailable(string error) => new([], error);
}

/// <summary>選択項目ひとつ分の選択肢と初期の選択。SelectedIndex は選ぶものが無ければ -1</summary>
public sealed record DeviceSelection(IReadOnlyList<DeviceChoice> Choices, int SelectedIndex);

/// <summary>
/// 設定画面のモデル（Issue rurute/inzone-auto-switch#9、spec §4・§7）。画面の見た目に依存しない部分を持ち、フォームはこれを表示して選択結果を返すだけにする。
/// </summary>
public sealed class SettingsFormModel
{
    private const string NotSetLabel = "（未設定）";

    private SettingsFormModel(
        DeviceSelection budsPlayback, DeviceSelection budsCapture, DeviceSelection speakerPlayback, DeviceSelection speakerCapture,
        bool switchOnConnect, bool switchOnDisconnect, IReadOnlyList<string> notices)
    {
        BudsPlayback = budsPlayback;
        BudsCapture = budsCapture;
        SpeakerPlayback = speakerPlayback;
        SpeakerCapture = speakerCapture;
        SwitchOnConnect = switchOnConnect;
        SwitchOnDisconnect = switchOnDisconnect;
        Notices = notices;
    }

    public DeviceSelection BudsPlayback { get; }

    public DeviceSelection BudsCapture { get; }

    public DeviceSelection SpeakerPlayback { get; }

    public DeviceSelection SpeakerCapture { get; }

    public bool SwitchOnConnect { get; }

    public bool SwitchOnDisconnect { get; }

    /// <summary>画面の上部に出す、一覧を取得できなかった流れの知らせ（再生 → 録音の順）</summary>
    public IReadOnlyList<string> Notices { get; }

    public static SettingsFormModel Create(SwitchSettings current, DeviceNameList playback, DeviceNameList capture)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(capture);

        var notices = new List<string>();
        if (playback.Error is not null)
        {
            notices.Add($"再生デバイスの一覧を取得できません: {playback.Error}");
        }
        if (capture.Error is not null)
        {
            notices.Add($"録音デバイスの一覧を取得できません: {capture.Error}");
        }

        return new SettingsFormModel(
            Select(current.BudsDevice, playback, allowNotSet: false),
            Select(current.BudsInputDevice, capture, allowNotSet: true),
            Select(current.SpeakerDevice, playback, allowNotSet: false),
            Select(current.SpeakerInputDevice, capture, allowNotSet: true),
            current.SwitchOnConnect,
            current.SwitchOnDisconnect,
            notices);
    }

    /// <summary>
    /// 選んだ結果から保存する6項目を作る。再生の2項目が選ばれていない・空なら null（spec §7）。録音の未選択は未設定として扱う
    /// </summary>
    public static SwitchSettings? ToSettings(
        DeviceChoice? budsPlayback, DeviceChoice? budsCapture, DeviceChoice? speakerPlayback, DeviceChoice? speakerCapture,
        bool switchOnConnect, bool switchOnDisconnect)
    {
        if (string.IsNullOrWhiteSpace(budsPlayback?.Value) || string.IsNullOrWhiteSpace(speakerPlayback?.Value))
        {
            return null;
        }

        return new SwitchSettings(
            budsPlayback.Value,
            speakerPlayback.Value,
            CaptureValue(budsCapture),
            CaptureValue(speakerCapture),
            switchOnConnect,
            switchOnDisconnect);
    }

    private static string CaptureValue(DeviceChoice? choice) =>
        choice is null || string.IsNullOrWhiteSpace(choice.Value) ? string.Empty : choice.Value;

    // 並びは「{名前}（未接続）」（あるときだけ）→「（未設定）」（録音だけ）→ 一覧のデバイス（spec §4）
    private static DeviceSelection Select(string configured, DeviceNameList list, bool allowNotSet)
    {
        // 切り替えは名前を大文字小文字を区別せずに照合し、一致した先頭を使うので、同じ名前は1つにまとめる
        var devices = list.Names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new DeviceChoice(name, name, DeviceChoiceKind.Device))
            .ToList();

        var choices = new List<DeviceChoice>();
        DeviceChoice? selected = null;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            selected = devices.FirstOrDefault(d => string.Equals(d.Value, configured, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                selected = list.Error is null
                    ? new DeviceChoice($"{configured}（未接続）", configured, DeviceChoiceKind.NotConnected)
                    : new DeviceChoice(configured, configured, DeviceChoiceKind.Unlisted);
                choices.Add(selected);
            }
        }

        if (allowNotSet)
        {
            var notSet = new DeviceChoice(NotSetLabel, string.Empty, DeviceChoiceKind.NotSet);
            choices.Add(notSet);
            if (string.IsNullOrWhiteSpace(configured))
            {
                selected = notSet;
            }
        }

        choices.AddRange(devices);
        return new DeviceSelection(choices, selected is null ? -1 : choices.IndexOf(selected));
    }
}
