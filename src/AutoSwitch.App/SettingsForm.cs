using System.Windows.Forms;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// 設定画面（Issue rurute/inzone-auto-switch#9）。SettingsFormModel を表示し、選んだ結果を保存の処理へ渡すだけにする。
/// 保存に成功したら閉じ、失敗したら理由をダイアログで出して閉じない。キャンセルと × は確認せずに閉じる（spec §4）。
/// </summary>
internal sealed class SettingsForm : Form
{
    private const string Caption = "AutoSwitch for INZONE";
    private const int ComboWidth = 360;

    private readonly ComboBox _budsPlayback = NewCombo();
    private readonly ComboBox _budsCapture = NewCombo();
    private readonly ComboBox _speakerPlayback = NewCombo();
    private readonly ComboBox _speakerCapture = NewCombo();
    private readonly CheckBox _switchOnConnect = new() { Text = "接続したら切り替える(&C)", AutoSize = true };
    private readonly CheckBox _switchOnDisconnect = new() { Text = "切断したら切り替える(&D)", AutoSize = true };
    private readonly Func<SwitchSettings, string?> _save;

    /// <param name="fileProblem">null でなければ上部に出し、保存を押せなくする</param>
    /// <param name="save">保存と反映を行い、失敗したら理由、成功したら null を返す</param>
    public SettingsForm(SettingsFormModel model, string? fileProblem, Func<SwitchSettings, string?> save)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(save);
        _save = save;

        Text = "AutoSwitch for INZONE の設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        Bind(_budsPlayback, model.BudsPlayback);
        Bind(_budsCapture, model.BudsCapture);
        Bind(_speakerPlayback, model.SpeakerPlayback);
        Bind(_speakerCapture, model.SpeakerCapture);
        _switchOnConnect.Checked = model.SwitchOnConnect;
        _switchOnDisconnect.Checked = model.SwitchOnDisconnect;

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };

        var notices = model.Notices.ToList();
        if (fileProblem is not null)
        {
            notices.Insert(0, $"{fileProblem}（保存できません。config.json を直してから開き直してください）");
        }
        foreach (var notice in notices)
        {
            layout.Controls.Add(new Label
            {
                Text = notice,
                AutoSize = true,
                MaximumSize = new Size(ComboWidth + 160, 0),
                ForeColor = Color.Firebrick,
                Margin = new Padding(3, 3, 3, 9),
            });
        }

        layout.Controls.Add(Group("Buds を接続したとき", _switchOnConnect, ("再生デバイス(&P)", _budsPlayback), ("録音デバイス(&R)", _budsCapture)));
        layout.Controls.Add(Group("Buds を切断したとき", _switchOnDisconnect, ("再生デバイス(&Y)", _speakerPlayback), ("録音デバイス(&M)", _speakerCapture)));

        var saveButton = new Button { Text = "保存(&S)", AutoSize = true, Enabled = fileProblem is null };
        var cancelButton = new Button { Text = "キャンセル", AutoSize = true };
        saveButton.Click += (_, _) => Save();
        cancelButton.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 6, 0, 0),
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(saveButton);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        CancelButton = cancelButton;
    }

    private static ComboBox NewCombo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = ComboWidth,
    };

    private static void Bind(ComboBox combo, DeviceSelection selection)
    {
        combo.Items.AddRange(selection.Choices.Cast<object>().ToArray());
        combo.SelectedIndex = selection.SelectedIndex;
    }

    private static GroupBox Group(string title, CheckBox toggle, params (string Label, ComboBox Combo)[] rows)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        table.Controls.Add(toggle, 0, 0);
        table.SetColumnSpan(toggle, 2);
        for (var i = 0; i < rows.Length; i++)
        {
            table.Controls.Add(new Label { Text = rows[i].Label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, i + 1);
            table.Controls.Add(rows[i].Combo, 1, i + 1);
        }

        var group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(9),
        };
        group.Controls.Add(table);
        return group;
    }

    private void Save()
    {
        var settings = SettingsFormModel.ToSettings(
            _budsPlayback.SelectedItem as DeviceChoice,
            _budsCapture.SelectedItem as DeviceChoice,
            _speakerPlayback.SelectedItem as DeviceChoice,
            _speakerCapture.SelectedItem as DeviceChoice,
            _switchOnConnect.Checked,
            _switchOnDisconnect.Checked);
        if (settings is null)
        {
            MessageBox.Show(this, "接続時と切断時の再生デバイスを選んでください。", Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var error = _save(settings);
        if (error is not null)
        {
            MessageBox.Show(
                this,
                $"設定を保存できません。{Environment.NewLine}{Environment.NewLine}{error}",
                Caption,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        Close();
    }
}
