using System.Drawing;
using AutoSwitch.Core;

namespace AutoSwitch.App.Tests;

/// <summary>
/// タスクバーの残量表示の置き場所と、出す／隠すの判定、Warn／Info を 1 回ずつ書くことを確かめる（Issue rurute/inzone-auto-switch#27）。
/// 共通の値は作者の環境（3440×1440、表示倍率 100%、タスクバーは下）に近い数字にする。
/// </summary>
public sealed class TaskbarOverlayPolicyTests
{
    private static readonly Rectangle Monitor = new(0, 0, 3440, 1440);
    private static readonly Rectangle Taskbar = new(0, 1392, 3440, 48);
    private const int TrayLeft = 3183;
    private const int AppsRight = 2029;
    private static readonly Size Overlay = new(72, 32);
    private const int Dpi = 96;

    private static TaskbarProbeResult Probe(int appsRight = AppsRight, Rectangle? taskbar = null) =>
        new(new TaskbarSnapshot(taskbar ?? Taskbar, Monitor, TrayLeft, appsRight), null);

    private static TaskbarProbeResult ProbeError(string error) => new(null, error);

    [Fact]
    public void Evaluate_Normal_PlacesLeftOfTrayCentered()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        var decision = policy.Evaluate(true, 80, Probe(), false, Overlay, Dpi);

        Assert.Equal(OverlayHideReason.None, decision.Reason);
        Assert.Equal(new Rectangle(3107, 1400, 72, 32), decision.Bounds);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Evaluate_Disabled_Hides()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        var decision = policy.Evaluate(false, 80, Probe(), false, Overlay, Dpi);

        Assert.Equal(OverlayHideReason.Disabled, decision.Reason);
        Assert.Null(decision.Bounds);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Evaluate_NoLevel_Hides()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        var decision = policy.Evaluate(true, null, Probe(), false, Overlay, Dpi);

        Assert.Equal(OverlayHideReason.NoLevel, decision.Reason);
        Assert.Null(decision.Bounds);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Evaluate_NullProbe_HidesAsUnavailable()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        var decision = policy.Evaluate(true, 80, null, false, Overlay, Dpi);

        Assert.Equal(OverlayHideReason.TaskbarUnavailable, decision.Reason);
        Assert.Null(decision.Bounds);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warn);
    }

    [Fact]
    public void Evaluate_ProbeError_HidesAndWarnsOnce()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        OverlayDecision? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = policy.Evaluate(true, 80, ProbeError("UIA failed"), false, Overlay, Dpi);
        }

        Assert.Equal(OverlayHideReason.TaskbarUnavailable, last!.Reason);
        Assert.Null(last.Bounds);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Contains("UIA failed", entry.Message);
    }

    [Fact]
    public void Evaluate_RecoversAfterError_ShowsAndInfoOnce()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        policy.Evaluate(true, 80, ProbeError("UIA failed"), false, Overlay, Dpi);
        var first = policy.Evaluate(true, 80, Probe(), false, Overlay, Dpi);
        var second = policy.Evaluate(true, 80, Probe(), false, Overlay, Dpi);

        Assert.Equal(OverlayHideReason.None, first.Reason);
        Assert.Equal(OverlayHideReason.None, second.Reason);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warn);
        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Info);
        Assert.Equal("タスクバーに残量を出せるようになりました。", info.Message);
        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public void Evaluate_NoSpace_HidesAndWarnsOnce()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        // x = 3183 - 4 - 72 = 3107、AppsRight + gap = 3100 + 8 = 3108 → 足りない
        policy.Evaluate(true, 80, Probe(appsRight: 3100), false, Overlay, Dpi);
        var decision = policy.Evaluate(true, 80, Probe(appsRight: 3100), false, Overlay, Dpi);

        Assert.Equal(OverlayHideReason.NoSpace, decision.Reason);
        Assert.Null(decision.Bounds);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Contains("アプリのアイコンと重なるため", entry.Message);
    }

    [Fact]
    public void Evaluate_ExactlyEnoughSpace_Shows()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        // x = 3107 == 3099 + 8 → ちょうど足りる
        var decision = policy.Evaluate(true, 80, Probe(appsRight: 3099), false, Overlay, Dpi);

        Assert.Equal(OverlayHideReason.None, decision.Reason);
        Assert.Equal(new Rectangle(3107, 1400, 72, 32), decision.Bounds);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Evaluate_TaskbarHidden_HidesWithoutLog()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        // 自動非表示で画面の下へ引っ込んだタスクバー（画面に残るのは 2px）
        var decision = policy.Evaluate(true, 80, Probe(taskbar: new Rectangle(0, 1438, 3440, 48)), false, Overlay, Dpi);

        Assert.Equal(OverlayHideReason.TaskbarHidden, decision.Reason);
        Assert.Null(decision.Bounds);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Evaluate_Fullscreen_HidesWithoutLog()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        var decision = policy.Evaluate(true, 80, Probe(), true, Overlay, Dpi);

        Assert.Equal(OverlayHideReason.Fullscreen, decision.Reason);
        Assert.Null(decision.Bounds);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Evaluate_FullscreenWhileWarned_DoesNotResetWarning()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        policy.Evaluate(true, 80, Probe(appsRight: 3100), false, Overlay, Dpi);
        var fullscreen = policy.Evaluate(true, 80, Probe(appsRight: 3100), true, Overlay, Dpi);
        var again = policy.Evaluate(true, 80, Probe(appsRight: 3100), false, Overlay, Dpi);

        Assert.Equal(OverlayHideReason.Fullscreen, fullscreen.Reason);
        Assert.Equal(OverlayHideReason.NoSpace, again.Reason);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
    }

    [Fact]
    public void Evaluate_Dpi144_ScalesMarginAndGap()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);

        var decision = policy.Evaluate(
            true, 80, Probe(taskbar: new Rectangle(0, 1368, 3440, 72)), false, new Size(108, 48), 144);

        Assert.Equal(OverlayHideReason.None, decision.Reason);
        Assert.Equal(new Rectangle(3183 - 6 - 108, 1380, 108, 48), decision.Bounds);
    }

    [Fact]
    public void Evaluate_Dpi144_GapIsScaled()
    {
        var logger = new RecordingLogger();
        var policy = new TaskbarOverlayPolicy(logger);
        var taskbar = new Rectangle(0, 1368, 3440, 72);

        // x = 3183 - 6 - 108 = 3069。gap は 12 なので AppsRight 3058 で足りず、3057 で足りる
        var tooClose = policy.Evaluate(true, 80, Probe(appsRight: 3058, taskbar: taskbar), false, new Size(108, 48), 144);
        var enough = policy.Evaluate(true, 80, Probe(appsRight: 3057, taskbar: taskbar), false, new Size(108, 48), 144);

        Assert.Equal(OverlayHideReason.NoSpace, tooClose.Reason);
        Assert.Equal(OverlayHideReason.None, enough.Reason);
    }
}

/// <summary>書かれたログを記録するだけの偽物。</summary>
internal sealed class RecordingLogger : IAppLogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public void Write(LogLevel level, string message) => Entries.Add((level, message));
}
