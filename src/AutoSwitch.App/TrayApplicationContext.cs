using System.Diagnostics;
using System.Windows.Forms;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// トレイ常駐の本体。UI スレッドで動き、監視は MonitorRunner のスレッドが回す。
/// 状態変更は監視スレッドから届くので、ハンドルを作った隠し Control 経由で UI スレッドへ渡す。
/// 後片付けは Dispose に集める（購読の解除 → 監視の停止 → アイコンとメニューの解放の順）。
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
    private bool _stopped;

    public TrayApplicationContext(AutoSwitchEngine engine, AppSettings settings, IAppLogger logger, TrayIcons icons)
    {
        _engine = engine;
        _logger = logger;

        // icons の所有権はここで受け取る。構築に失敗したら、受け取った分ごと捨てて投げ直す
        _icons = icons;

        try
        {
            // UI スレッドでウィンドウハンドルを作る。BeginInvoke の行き先になる
            _ = _marshaller.Handle;

            _menu.Items.Add("設定ファイルを開く(&C)", null, (_, _) => Open(AppPaths.ConfigPath));
            _menu.Items.Add("ログフォルダを開く(&L)", null, (_, _) => Open(AppPaths.LogFolder));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("終了(&X)", null, (_, _) => ExitApplication());

            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.Icon = _icons.For(engine.State);
            _notifyIcon.Text = TooltipFor(engine.State);
            _notifyIcon.Visible = true;

            _engine.StateChanged += OnStateChanged;

            _runner = new MonitorRunner(engine, settings, logger);
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
        HeadsetState.Connected => "INZONE Auto Switch: 接続中",
        HeadsetState.Disconnected => "INZONE Auto Switch: 未接続",
        _ => "INZONE Auto Switch: 不明",
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

    private void Open(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Warn, $"開けません: {path}: {ex.Message}");
            MessageBox.Show(
                $"開けません。{Environment.NewLine}{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "INZONE Auto Switch",
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
        _runner?.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _marshaller.Dispose();
        _icons.Dispose();
    }
}
