using System.Drawing;
using AutoSwitch.Core;

namespace AutoSwitch.App.Tests;

/// <summary>
/// 測定と判定と表示をつなぐ TaskbarOverlayController を、偽の測定と偽の表示先で確かめる（Issue rurute/inzone-auto-switch#27）。
/// 測定の値は TaskbarOverlayPolicyTests と同じ。位置ではなく「出したか・隠したか」を見る。
/// </summary>
public sealed class TaskbarOverlayControllerTests
{
    private static readonly TaskbarProbeResult Measured = new(
        new TaskbarSnapshot(new Rectangle(0, 1392, 3440, 48), new Rectangle(0, 0, 3440, 1440), 3183, 2029), null);

    private static readonly BudsLevels Connected = new(80, 60, 90);

    private static (TaskbarOverlayController Controller, FakeProbe Probe, FakeSurface Surface, RecordingLogger Logger) Create(bool enabled = true)
    {
        var probe = new FakeProbe { Result = Measured };
        var surface = new FakeSurface();
        var logger = new RecordingLogger();
        return (new TaskbarOverlayController(probe, surface, logger, enabled), probe, surface, logger);
    }

    [Fact]
    public void Tick_ConnectedAndMeasured_Shows()
    {
        var (controller, _, surface, logger) = Create();

        controller.SetLevels(Connected);
        controller.Measure();
        controller.Tick();

        var call = Assert.Single(surface.Calls);
        Assert.Equal("ShowAt", call.Kind);
        // 左右の低いほう 60 を、ダークのテーマ・96 dpi で描いたものと同じ絵
        using var expected = BatteryOverlayRenderer.Render(60, false, 96);
        Assert.True(SamePixels(expected, call.Image!));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Measure_ProbeError_HidesAndWarnsOnce()
    {
        var (controller, probe, surface, logger) = Create();
        controller.SetLevels(Connected);
        controller.Measure();
        controller.Tick();
        Assert.Equal("ShowAt", surface.Calls[^1].Kind);

        probe.Result = new TaskbarProbeResult(null, "UIA failed");
        controller.Measure();
        controller.Tick();
        controller.Tick();
        controller.Tick();

        Assert.Equal("Hide", surface.Calls[^1].Kind);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Contains("UIA failed", entry.Message);
    }

    [Fact]
    public void Measure_ProbeThrows_HidesAndWarnsOnce()
    {
        var (controller, probe, surface, logger) = Create();
        controller.SetLevels(Connected);
        controller.Measure();
        controller.Tick();
        Assert.Equal("ShowAt", surface.Calls[^1].Kind);

        probe.Throw = new InvalidOperationException("probe exploded");
        controller.Measure();
        controller.Tick();

        Assert.Equal("Hide", surface.Calls[^1].Kind);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Contains("probe exploded", entry.Message);
    }

    [Fact]
    public void Measure_RecoversAfterError_ShowsAndInfoOnce()
    {
        var (controller, probe, surface, logger) = Create();
        controller.SetLevels(Connected);
        probe.Result = new TaskbarProbeResult(null, "UIA failed");
        controller.Measure();
        controller.Tick();

        probe.Result = Measured;
        controller.Measure();
        controller.Tick();
        controller.Tick();

        Assert.Equal("ShowAt", surface.Calls[^1].Kind);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warn);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Info);
        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public void SetEnabledFalse_HidesImmediately()
    {
        var (controller, _, surface, _) = Create();
        controller.SetLevels(Connected);
        controller.Measure();
        controller.Tick();
        Assert.Equal("ShowAt", surface.Calls[^1].Kind);

        controller.SetEnabled(false);

        Assert.Equal("Hide", surface.Calls[^1].Kind);
        controller.Tick();
        Assert.DoesNotContain(surface.Calls.Skip(1), c => c.Kind == "ShowAt");
    }

    [Fact]
    public void SetLevelsNull_HidesOnNextTick()
    {
        var (controller, _, surface, logger) = Create();
        controller.SetLevels(Connected);
        controller.Measure();
        controller.Tick();
        Assert.Equal("ShowAt", surface.Calls[^1].Kind);

        controller.SetLevels(null);
        controller.Tick();

        Assert.Equal("Hide", surface.Calls[^1].Kind);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Tick_SurfaceThrows_LogsErrorOnceAndDoesNotThrow()
    {
        var (controller, _, surface, logger) = Create();
        surface.ThrowOnShow = new InvalidOperationException("surface broken");
        controller.SetLevels(Connected);
        controller.Measure();

        controller.Tick();
        controller.Tick();

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("タスクバーの残量表示で予期しない例外が発生しました: surface broken", entry.Message);
        Assert.Equal("Hide", surface.Calls[^1].Kind);
    }

    [Fact]
    public void Tick_BeforeFirstMeasure_HidesWithoutLog()
    {
        var (controller, _, surface, logger) = Create();
        controller.SetLevels(Connected);

        controller.Tick();

        Assert.Equal("Hide", surface.Calls[^1].Kind);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Tick_SameValue_DoesNotRender_ChangedValueOrTheme_Renders()
    {
        var (controller, probe, surface, _) = Create();
        controller.SetLevels(Connected);
        controller.Measure();

        controller.Tick();
        controller.Tick();
        Assert.Same(surface.Calls[0].Image, surface.Calls[1].Image);

        controller.SetLevels(new BudsLevels(18, 50, null));
        controller.Tick();
        Assert.NotSame(surface.Calls[1].Image, surface.Calls[2].Image);
        Assert.Equal(1, probe.LightThemeReads);

        probe.LightTheme = true;
        controller.ThemeChanged();
        controller.Tick();
        Assert.NotSame(surface.Calls[2].Image, surface.Calls[3].Image);
        Assert.Equal(2, probe.LightThemeReads);

        surface.Dpi = 144;
        controller.Tick();
        Assert.NotSame(surface.Calls[3].Image, surface.Calls[4].Image);
        Assert.Equal(48, surface.Calls[4].ImageHeight);
    }

    [Fact]
    public void Dispose_ThenPublicMethods_DoNotThrowOrShow()
    {
        var (controller, _, surface, _) = Create();
        controller.SetLevels(Connected);
        controller.Measure();
        controller.Tick();

        controller.Dispose();
        controller.Tick();
        controller.Measure();
        controller.SetEnabled(true);
        controller.ThemeChanged();
        controller.Dispose();

        Assert.Single(surface.Calls, c => c.Kind == "ShowAt");
    }

    // 測っている途中で OFF → その測定が終わる → ON、の順。ON のあとに始めた測定が終わるまで、OFF 前に始めた測定の位置で出さない
    [Fact]
    public void MeasureFinishingAfterDisable_IsDiscarded_UntilMeasuredAfterReenable()
    {
        var (controller, probe, surface, _) = Create();
        controller.SetLevels(Connected);
        probe.DuringMeasure = () => controller.SetEnabled(false);

        controller.Measure();
        probe.DuringMeasure = null;
        controller.SetEnabled(true);
        controller.Tick();

        Assert.DoesNotContain(surface.Calls, c => c.Kind == "ShowAt");

        controller.Measure();
        controller.Tick();

        Assert.Equal("ShowAt", surface.Calls[^1].Kind);
    }

    // 測っている途中で OFF → ON → その測定が終わる、の順でも、ON のあとに始めた測定が終わるまで出さない
    [Fact]
    public void MeasureStartedBeforeDisableAndReenable_IsDiscarded()
    {
        var (controller, probe, surface, _) = Create();
        controller.SetLevels(Connected);
        probe.DuringMeasure = () =>
        {
            controller.SetEnabled(false);
            controller.SetEnabled(true);
        };

        controller.Measure();
        probe.DuringMeasure = null;
        controller.Tick();

        Assert.DoesNotContain(surface.Calls, c => c.Kind == "ShowAt");

        controller.Measure();
        controller.Tick();

        Assert.Equal("ShowAt", surface.Calls[^1].Kind);
    }

    // 有効のまま SetEnabled(true) を呼んでも（設定を ON のまま保存し直す）、持っている測定は捨てない
    [Fact]
    public void SetEnabledTrue_WhileEnabled_KeepsMeasurement()
    {
        var (controller, _, surface, _) = Create();
        controller.SetLevels(Connected);
        controller.Measure();

        controller.SetEnabled(true);
        controller.Tick();

        Assert.Equal("ShowAt", surface.Calls[^1].Kind);
    }

    [Fact]
    public void Constructor_Disabled_DoesNotShow()
    {
        var (controller, _, surface, _) = Create(enabled: false);
        controller.SetLevels(Connected);
        controller.Measure();

        controller.Tick();

        Assert.DoesNotContain(surface.Calls, c => c.Kind == "ShowAt");
    }

    private static bool SamePixels(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size)
        {
            return false;
        }

        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private sealed class FakeProbe : ITaskbarProbe
    {
        public TaskbarProbeResult Result { get; set; } = new(null, "not set");
        public Exception? Throw { get; set; }
        public bool LightTheme { get; set; }
        public int LightThemeReads { get; private set; }

        /// <summary>測っている途中に割り込ませる処理（並行の順序を作るため）。</summary>
        public Action? DuringMeasure { get; set; }

        public TaskbarProbeResult Measure()
        {
            DuringMeasure?.Invoke();
            return Throw is null ? Result : throw Throw;
        }

        public bool IsFullscreenForeground() => false;

        public bool IsLightTheme()
        {
            LightThemeReads++;
            return LightTheme;
        }
    }

    private sealed class FakeSurface : IOverlaySurface
    {
        public int Dpi { get; set; } = 96;
        public Exception? ThrowOnShow { get; set; }
        public List<(string Kind, Bitmap? Image, int ImageHeight)> Calls { get; } = new();

        public void ShowAt(Rectangle bounds, Bitmap image)
        {
            if (ThrowOnShow is not null)
            {
                throw ThrowOnShow;
            }

            Calls.Add(("ShowAt", image, image.Height));
        }

        public void Hide() => Calls.Add(("Hide", null, 0));
    }
}
