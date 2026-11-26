using System.Drawing;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>残量表示を出す先（実体は最前面のレイヤード窓）。偽物に差し替えてテストするための入口。</summary>
internal interface IOverlaySurface
{
    /// <summary>出す先の dpi（96 が 100%）。</summary>
    int Dpi { get; }

    /// <summary>bounds（物理ピクセルの画面座標）に image を出し、手前へ置き直す。image はこの呼び出しの間だけ使い、持ち続けない。</summary>
    void ShowAt(Rectangle bounds, Bitmap image);

    void Hide();
}

/// <summary>
/// タスクバーの測定の結果を持ち、TaskbarOverlayPolicy で判定し、表示先へ出す／隠す（Issue rurute/inzone-auto-switch#27）。
/// Measure は測定用のスレッドから、それ以外は UI スレッドから呼ぶ。
/// 公開のメソッドはどれも例外を外へ出さない（表示の失敗で監視と切り替えを止めないため）。
/// </summary>
internal sealed class TaskbarOverlayController : IDisposable
{
    private const string UnexpectedErrorPrefix = "タスクバーの残量表示で予期しない例外が発生しました: ";

    private readonly ITaskbarProbe _probe;
    private readonly IOverlaySurface _surface;
    private readonly IAppLogger _logger;
    private readonly TaskbarOverlayPolicy _policy;
    private readonly object _errorGate = new();

    // 有効・無効を切り替えるたびに進める世代。測定は始めたときの世代を持ち、世代が変わっていたら結果を捨てる。
    // 切り替えの前に始まった測定が、切り替えで消した結果を後から書き戻さないため。_measured の書き込みと一緒にこの錠で守る
    private readonly object _measureGate = new();
    private int _generation;

    // 測定用のスレッドが書き、UI スレッドが読む。null はまだ測っていない（または無効にした後）
    private MeasureState? _measured;
    private BudsLevels? _levels;
    private volatile bool _enabled;
    private volatile bool _themeStale = true;
    private volatile bool _disposed;

    // ここから下は UI スレッドだけが触る
    private bool _lightTheme;
    private Bitmap? _image;
    private int _imageLevel;
    private bool _imageLightTheme;
    private int _imageDpi;

    // 直前に書いた予期しない例外の文言（同じ文言を書き続けないため）
    private string? _lastError;

    public TaskbarOverlayController(ITaskbarProbe probe, IOverlaySurface surface, IAppLogger logger, bool enabled)
    {
        _probe = probe;
        _surface = surface;
        _logger = logger;
        _policy = new TaskbarOverlayPolicy(logger);
        _enabled = enabled;
    }

    /// <summary>
    /// 測定用のスレッドから呼ぶ。前の結果は捨てる（失敗した測定も、前に成功した測定を上書きする）。
    /// probe が例外を投げたら、そのメッセージを Error に入れた結果として持つ。
    /// 測っている間に有効・無効が切り替わったら、その結果は持たない。
    /// </summary>
    public void Measure()
    {
        if (_disposed)
        {
            return;
        }

        int generation;
        lock (_measureGate)
        {
            generation = _generation;
        }

        try
        {
            TaskbarProbeResult result;
            try
            {
                result = _probe.Measure();
            }
            catch (Exception ex)
            {
                result = new TaskbarProbeResult(null, ex.Message);
            }

            bool fullscreen;
            try
            {
                fullscreen = _probe.IsFullscreenForeground();
            }
            catch (Exception)
            {
                fullscreen = false;
            }

            StoreMeasured(generation, new MeasureState(result, fullscreen));
        }
        catch (Exception ex)
        {
            // 測定用のスレッドからは窓に触らない。失敗した測定として持ち、次の Tick で隠す
            Fail(ex, hide: false);
            StoreMeasured(generation, new MeasureState(new TaskbarProbeResult(null, ex.Message), false));
        }
    }

    /// <summary>UI スレッドから呼ぶ。持っている測定の結果と残量で判定し、出すなら手前へ置き直す。</summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var enabled = _enabled;
            var level = BatteryDisplay.LevelToShow(Volatile.Read(ref _levels));
            var measured = Volatile.Read(ref _measured);

            // まだ測っていないときは、位置が分からないのではないので、Warn を書かずに隠す
            if (enabled && level is not null && measured is null)
            {
                _surface.Hide();
                return;
            }

            var dpi = _surface.Dpi;
            var image = enabled && level is not null ? EnsureImage(level.Value, dpi) : null;
            var decision = _policy.Evaluate(enabled, level, measured?.Result, measured?.Fullscreen ?? false, image?.Size ?? Size.Empty, dpi);
            if (decision.Bounds is { } bounds && image is not null)
            {
                _surface.ShowAt(bounds, image);
            }
            else
            {
                _surface.Hide();
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// false なら Tick を待たずにすぐ隠す。無効にしたとき・無効から有効に戻したときは、前の測定の結果を捨て、
    /// 測っている途中の測定の結果も持たない（有効に戻したあとに始めた測定が終わるまで、古い位置に出さない）。
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!enabled || !_enabled)
            {
                lock (_measureGate)
                {
                    _generation++;
                    Volatile.Write(ref _measured, null);
                }
            }

            _enabled = enabled;
            if (!enabled)
            {
                _surface.Hide();
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public void SetLevels(BudsLevels? levels)
    {
        if (_disposed)
        {
            return;
        }

        Volatile.Write(ref _levels, levels);
    }

    /// <summary>次の Tick でテーマを読み直し、変わっていれば描き直す。</summary>
    public void ThemeChanged()
    {
        if (_disposed)
        {
            return;
        }

        _themeStale = true;
    }

    /// <summary>UI スレッドから呼ぶ。作ったビットマップを捨てる。窓は持ち主が破棄する。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _image?.Dispose();
            _image = null;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>出す値・テーマ・dpi が変わったときだけ描き直し、前のビットマップを捨てる。</summary>
    private Bitmap EnsureImage(int level, int dpi)
    {
        if (_themeStale)
        {
            _lightTheme = _probe.IsLightTheme();
            _themeStale = false;
        }

        if (_image is not null && _imageLevel == level && _imageLightTheme == _lightTheme && _imageDpi == dpi)
        {
            return _image;
        }

        var image = BatteryOverlayRenderer.Render(level, _lightTheme, dpi);
        _image?.Dispose();
        _image = image;
        _imageLevel = level;
        _imageLightTheme = _lightTheme;
        _imageDpi = dpi;
        return image;
    }

    private void StoreMeasured(int generation, MeasureState state)
    {
        lock (_measureGate)
        {
            if (generation == _generation)
            {
                Volatile.Write(ref _measured, state);
            }
        }
    }

    private void Fail(Exception ex, bool hide = true)
    {
        try
        {
            var message = UnexpectedErrorPrefix + ex.Message;
            bool write;
            lock (_errorGate)
            {
                write = message != _lastError;
                _lastError = message;
            }

            if (write)
            {
                _logger.Write(LogLevel.Error, message);
            }
        }
        catch (Exception)
        {
        }

        if (!hide)
        {
            return;
        }

        try
        {
            _surface.Hide();
        }
        catch (Exception)
        {
        }
    }

    private sealed record MeasureState(TaskbarProbeResult Result, bool Fullscreen);
}
