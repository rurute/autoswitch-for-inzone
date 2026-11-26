using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// タスクバーの残量表示の部品（窓・測定・判定）をまとめ、タイマーで回す（Issue rurute/inzone-auto-switch#27）。UI スレッドで作り、UI スレッドから使う。
/// 手前への置き直し（controller.Tick）は UI スレッドのタイマーで 300ms ごと、
/// 位置の測定（controller.Measure、UI Automation を含む）はスレッドプールのタイマーで 1 秒ごと（前の測定が終わっていなければ飛ばす）。
/// どちらのタイマーも有効のときだけ動かす。公開のメソッドは例外を外へ出さない（表示の失敗で監視と切り替えを止めないため）。
/// </summary>
internal sealed class TaskbarBatteryOverlay : IDisposable
{
    private const int TickIntervalMs = 300;
    private const int MeasureIntervalMs = 1000;

    private readonly IAppLogger _logger;
    private readonly TaskbarOverlayWindow _window;
    private readonly TaskbarOverlayController _controller;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private readonly System.Threading.Timer _measureTimer;

    // 1 なら測定中。測定のタイマーのコールバックが重ならないようにする
    private int _measuring;
    private volatile bool _disposed;

    private TaskbarBatteryOverlay(
        IAppLogger logger,
        TaskbarOverlayWindow window,
        TaskbarOverlayController controller,
        System.Windows.Forms.Timer tickTimer,
        System.Threading.Timer measureTimer)
    {
        _logger = logger;
        _window = window;
        _controller = controller;
        _tickTimer = tickTimer;
        _measureTimer = measureTimer;
        _tickTimer.Tick += OnTick;
        _window.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>UI スレッドで呼ぶ。作れなければ Error を書いて null を返す（例外を外へ出さない）。</summary>
    public static TaskbarBatteryOverlay? TryCreate(IAppLogger logger, bool enabled)
    {
        TaskbarOverlayWindow? window = null;
        TaskbarOverlayController? controller = null;
        System.Windows.Forms.Timer? tickTimer = null;
        System.Threading.Timer? measureTimer = null;
        try
        {
            window = new TaskbarOverlayWindow();
            controller = new TaskbarOverlayController(new Win32TaskbarProbe(), window, logger, enabled);
            tickTimer = new System.Windows.Forms.Timer { Interval = TickIntervalMs };

            // コールバックは作った後でつなぐ（overlay ができる前に走らせない）。始めるのは SetEnabled か下の Start
            TaskbarBatteryOverlay? overlay = null;
            measureTimer = new System.Threading.Timer(_ => overlay?.OnMeasure(), null, Timeout.Infinite, Timeout.Infinite);
            overlay = new TaskbarBatteryOverlay(logger, window, controller, tickTimer, measureTimer);
            if (enabled)
            {
                overlay.StartTimers();
            }

            return overlay;
        }
        catch (Exception ex)
        {
            DisposeQuietly(measureTimer);
            DisposeQuietly(tickTimer);
            DisposeQuietly(controller);
            DisposeQuietly(window);
            try
            {
                logger.Write(LogLevel.Error, $"タスクバーの残量表示を始められません: {ex.Message}");
            }
            catch (Exception)
            {
            }

            return null;
        }
    }

    /// <summary>
    /// true: すぐ 1 回測り（間隔を待たない）、両方のタイマーを始める。
    /// false: すぐ隠し、両方のタイマーを止める。
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _controller.SetEnabled(enabled);
        try
        {
            if (enabled)
            {
                StartTimers();
            }
            else
            {
                StopTimers();
            }
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Error, $"タスクバーの残量表示のタイマーを{(enabled ? "始め" : "止め")}られません: {ex.Message}");
        }
    }

    public void SetLevels(BudsLevels? levels) => _controller.SetLevels(levels);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tickTimer.Tick -= OnTick;
        _window.SettingsChanged -= OnSettingsChanged;
        DisposeQuietly(_tickTimer);
        DisposeQuietly(_measureTimer);
        // 測定のスレッドが残っていても、破棄した controller の Measure は何もしない
        DisposeQuietly(_controller);
        DisposeQuietly(_window);
    }

    // 測定は 0ms 後に 1 回目を走らせ、以後 1 秒ごと
    private void StartTimers()
    {
        _measureTimer.Change(0, MeasureIntervalMs);
        _tickTimer.Start();
    }

    private void StopTimers()
    {
        _tickTimer.Stop();
        _measureTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _controller.Tick();
    }

    private void OnSettingsChanged()
    {
        if (_disposed)
        {
            return;
        }

        _controller.ThemeChanged();
    }

    // スレッドプールから呼ばれる。ここから例外を出すとプロセスが落ちるので、すべて捕まえる
    private void OnMeasure()
    {
        if (_disposed || Interlocked.Exchange(ref _measuring, 1) == 1)
        {
            return;
        }

        try
        {
            _controller.Measure();
        }
        catch (Exception ex)
        {
            try
            {
                _logger.Write(LogLevel.Error, $"タスクバーの測定で予期しない例外が発生しました: {ex.Message}");
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            Volatile.Write(ref _measuring, 0);
        }
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
