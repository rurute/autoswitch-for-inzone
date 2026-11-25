using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// トレイ常駐の本体。UI スレッドで動き、監視は MonitorRunner のスレッドが回す。
/// 状態変更は監視スレッドから届くので、ハンドルを作った隠し Control 経由で UI スレッドへ渡す。
/// 後片付けは Dispose に集める（購読の解除 → 設定画面を閉じる → 監視の停止 → アイコンとメニューの解放の順）。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly TrayIcons _icons;
    private readonly NotifyIcon _notifyIcon = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly Control _marshaller = new();
    private readonly AutoSwitchEngine _engine;
    private MonitorRunner? _runner;
    private readonly IAppLogger _logger;

    private const string AutoStartMenuText = "Windows ログオン時の自動起動(&A)";

    private readonly AutoStartRegistration _autoStart;
    // コンストラクタの try の中で作る。作る前に失敗した経路では null のままになる
    private ToolStripMenuItem? _autoStartItem;

    // 同時に1つだけ開く設定画面（Issue rurute/inzone-auto-switch#9）。閉じたら null に戻す
    private SettingsForm? _settingsForm;

    private bool _stopped;

    public TrayApplicationContext(
        AutoSwitchEngine engine,
        AppSettings settings,
        IAppLogger logger,
        TrayIcons icons,
        AutoStartRegistration autoStart,
        IBudsTransport transport)
    {
        _engine = engine;
        _logger = logger;
        _autoStart = autoStart;

        // icons の所有権はここで受け取る。構築に失敗したら、受け取った分ごと捨てて投げ直す
        _icons = icons;

        try
        {
            // UI スレッドでウィンドウハンドルを作る。BeginInvoke の行き先になる
            _ = _marshaller.Handle;

            _menu.Items.Add("設定(&S)...", null, (_, _) => OpenSettings());
            _menu.Items.Add("設定ファイルを開く(&C)", null, (_, _) => Open(AppPaths.ConfigPath, "設定ファイル"));
            _menu.Items.Add("ログフォルダを開く(&L)", null, (_, _) => Open(AppPaths.LogFolder, "ログフォルダ"));

            // ToolStripMenuItem はこの時点では OS の資源を作らないので、途中で失敗しても取り残しは生じない
            _autoStartItem = new ToolStripMenuItem(AutoStartMenuText, null, (_, _) => ToggleAutoStart())
            {
                CheckOnClick = false,
            };
            _menu.Items.Add(_autoStartItem);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("終了(&X)", null, (_, _) => ExitApplication());

            // レジストリは regedit など外からも変えられるので、開くたびに読み直す
            _menu.Opening += OnMenuOpening;
            SyncAutoStartCheck();

            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.Icon = _icons.For(engine.State);
            _notifyIcon.Text = TooltipFor(engine.State);
            _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
            _notifyIcon.Visible = true;

            _engine.StateChanged += OnStateChanged;

            _runner = new MonitorRunner(engine, settings, logger, transport);
            _runner.Start();
        }
        catch
        {
            Release();
            throw;
        }
    }

    /// <summary>監視スレッドの終了を確認できたか。Dispose のあとに読む</summary>
    public bool MonitorStoppedCleanly => _runner?.StoppedCleanly ?? false;

    internal static string TooltipFor(HeadsetState state) => state switch
    {
        HeadsetState.Connected => "AutoSwitch for INZONE: 接続中",
        HeadsetState.Disconnected => "AutoSwitch for INZONE: 未接続",
        _ => "AutoSwitch for INZONE: 不明",
    };

    private void OnStateChanged(object? sender, HeadsetState state)
    {
        try
        {
            if (_stopped || _marshaller.IsDisposed || !_marshaller.IsHandleCreated)
            {
                return;
            }

            _marshaller.BeginInvoke(() => Apply(state));
        }
        catch (Exception ex)
        {
            // 表示の更新に失敗しても監視は続ける
            _logger.Write(LogLevel.Warn, $"トレイの状態表示を更新できません: {ex.Message}");
        }
    }

    private void Apply(HeadsetState state)
    {
        if (_stopped)
        {
            return;
        }

        _notifyIcon.Icon = _icons.For(state);
        _notifyIcon.Text = TooltipFor(state);
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e) => SyncAutoStartCheck();

    /// <summary>
    /// Run キーの実際の登録状態をチェック表示へ映す。読み取れなかったときは Checked を変えず、
    /// 項目を選べなくして文言で知らせる。読めないことを OFF として見せない（spec §4）
    /// </summary>
    private void SyncAutoStartCheck()
    {
        if (_autoStartItem is null)
        {
            return;
        }

        try
        {
            _autoStartItem.Checked = _autoStart.IsEnabled();
            _autoStartItem.Enabled = true;
            _autoStartItem.Text = AutoStartMenuText;
        }
        catch (Exception ex)
        {
            // Checked は触らない。OFF にしようとして失敗し、読み取りも失敗した場合に、
            // 要求どおり OFF になったかのような表示を残さないため
            _autoStartItem.Enabled = false;
            _autoStartItem.Text = $"{AutoStartMenuText}（状態を読み取れません）";
            _logger.Write(LogLevel.Warn, $"自動起動の登録状態を読み取れません: {ex.Message}");
        }
    }

    /// <summary>
    /// メニューの選択で ON/OFF を切り替える。成否にかかわらず最後に実際の登録状態を読み直すので、
    /// 失敗したときに成功した表示は残らない（spec §4）。ダイアログは出さない
    /// </summary>
    private void ToggleAutoStart()
    {
        if (_autoStartItem is null)
        {
            return;
        }

        var enable = !_autoStartItem.Checked;
        try
        {
            if (enable)
            {
                _autoStart.Enable();
                _logger.Write(LogLevel.Info, "自動起動を有効にしました。");
            }
            else
            {
                _autoStart.Disable();
                _logger.Write(LogLevel.Info, "自動起動を無効にしました。");
            }
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Error, $"自動起動を{(enable ? "有効" : "無効")}にできません: {ex.Message}");
        }
        finally
        {
            SyncAutoStartCheck();
        }
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e) => OpenSettings();

    /// <summary>
    /// 設定画面を開く。開いていれば前面に出すだけにする。開くたびに config.json とデバイスの一覧を読み直す（spec §4）
    /// </summary>
    private void OpenSettings()
    {
        if (_stopped)
        {
            return;
        }

        SettingsForm? form = null;
        try
        {
            if (_settingsForm is { IsDisposed: false } opened)
            {
                if (opened.WindowState == FormWindowState.Minimized)
                {
                    opened.WindowState = FormWindowState.Normal;
                }
                opened.Activate();
                return;
            }

            var source = SettingsFileEditor.Open(AppPaths.ConfigPath);
            var (playback, capture) = DeviceListProvider.Load();
            var model = SettingsFormModel.Create(source.Values, playback, capture);
            form = new SettingsForm(model, source.Problem, SaveSettings);
            var created = form;
            // 表示中のフォームは閉じると破棄される。後から開いた画面の参照を消さないよう、自分のときだけ null に戻す
            created.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_settingsForm, created))
                {
                    _settingsForm = null;
                }
            };
            created.Show();
            created.Activate();
            _settingsForm = created;
        }
        catch (Exception ex)
        {
            // 設定画面を開けなくても常駐は続ける（UI スレッドの未処理例外にすると Program の致命的エラーの経路で終了してしまう）
            // 作りかけの画面の破棄で失敗しても、元の失敗の記録と表示は続ける
            try
            {
                form?.Dispose();
            }
            catch (Exception)
            {
            }
            _logger.Write(LogLevel.Error, $"設定画面を開けません: {ex.Message}");
            MessageBox.Show(
                $"設定画面を開けません。{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "AutoSwitch for INZONE",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 設定画面の保存。config.json の6項目を書き換え、成功したら動作中の設定へ反映する（spec §5・§6）。
    /// その場ではデバイスを切り替えない。失敗したら理由を返し、動作中の設定は変えない
    /// </summary>
    private string? SaveSettings(SwitchSettings values)
    {
        var result = SettingsFileEditor.Save(AppPaths.ConfigPath, values);
        if (!result.Succeeded)
        {
            var error = result.Error ?? "詳細不明";
            _logger.Write(LogLevel.Warn, $"設定を保存できません: {error}");
            return error;
        }

        // 入れ替えるのは UI スレッドだけなので、読んでから入れ替えるまでに他から変わることはない
        _engine.Settings.Replace(values.ApplyTo(_engine.Settings.Current));
        _logger.Write(LogLevel.Info, values.DescribeSaved());
        return null;
    }

    // ログにはパスを書かず、項目の名前（設定ファイル・ログフォルダ）で書く。ダイアログにはパスを出す
    private void Open(string path, string itemName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Warn, $"{itemName}を開けません: {AppPaths.ForLog(ex.Message)}");
            MessageBox.Show(
                $"開けません。{Environment.NewLine}{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "AutoSwitch for INZONE",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ExitApplication()
    {
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_stopped)
        {
            Release();
        }

        base.Dispose(disposing);
    }

    // 構築の失敗と通常の終了の両方から呼ぶ。作られていない資源は null なので飛ばす。
    // 購読していない状態での -= は何もしないので、そのまま呼んでよい
    private void Release()
    {
        _stopped = true;
        _engine.StateChanged -= OnStateChanged;
        _menu.Opening -= OnMenuOpening;
        _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
        // 設定画面は保存せずに閉じる。監視の停止より前に行う（spec §4「終了との関係」）
        _settingsForm?.Dispose();
        _settingsForm = null;
        _runner?.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _marshaller.Dispose();
        _icons.Dispose();
    }
}
