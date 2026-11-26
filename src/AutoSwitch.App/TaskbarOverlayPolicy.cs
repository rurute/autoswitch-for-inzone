using System.Drawing;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// タスクバーを測った結果。座標はすべて物理ピクセルの画面座標。
/// TrayLeft はトレイ（TrayNotifyWnd）の左端、AppsRight はアプリのボタンの右端のうち最も右のもの。
/// </summary>
internal sealed record TaskbarSnapshot(Rectangle Taskbar, Rectangle Monitor, int TrayLeft, int AppsRight);

/// <summary>測定の結果。Snapshot と Error のどちらか一方が入る。</summary>
internal sealed record TaskbarProbeResult(TaskbarSnapshot? Snapshot, string? Error);

internal enum OverlayHideReason { None, Disabled, NoLevel, TaskbarUnavailable, TaskbarHidden, Fullscreen, NoSpace }

/// <summary>出すなら Bounds に位置と大きさ、隠すなら Bounds は null で Reason に理由。</summary>
internal sealed record OverlayDecision(Rectangle? Bounds, OverlayHideReason Reason);

/// <summary>
/// タスクバーの残量表示の置き場所と、出すか隠すかを決める（Issue rurute/inzone-auto-switch#27）。
/// 「位置が分からない」「空きがない」で出せないときは Warn を 1 回だけ、出せるようになったら Info を 1 回書く。
/// 自動非表示・全画面などほかの理由ではログを書かず、警告済みかどうかも変えない。
/// UI スレッドからだけ呼ぶ前提で、排他はしない。
/// </summary>
internal sealed class TaskbarOverlayPolicy(IAppLogger logger)
{
    private const int MarginAt96 = 4;
    private const int GapAt96 = 8;
    private const string NoSpaceMessage = "アプリのアイコンと重なるため";
    private const string UnknownErrorMessage = "タスクバーの位置が分かりません";

    private bool _warned;

    public OverlayDecision Evaluate(bool enabled, int? level, TaskbarProbeResult? probe, bool fullscreen, Size overlaySize, int dpi)
    {
        if (!enabled)
        {
            return Hide(OverlayHideReason.Disabled);
        }

        if (level is null)
        {
            return Hide(OverlayHideReason.NoLevel);
        }

        var snapshot = probe?.Snapshot;
        if (snapshot is null)
        {
            var error = string.IsNullOrEmpty(probe?.Error) ? UnknownErrorMessage : probe.Error;
            WarnOnce(error);
            return Hide(OverlayHideReason.TaskbarUnavailable);
        }

        var taskbar = snapshot.Taskbar;
        if (Rectangle.Intersect(taskbar, snapshot.Monitor).Height < taskbar.Height - 2)
        {
            return Hide(OverlayHideReason.TaskbarHidden);
        }

        if (fullscreen)
        {
            return Hide(OverlayHideReason.Fullscreen);
        }

        var margin = MarginAt96 * dpi / 96;
        var gap = GapAt96 * dpi / 96;
        var x = snapshot.TrayLeft - margin - overlaySize.Width;
        var y = taskbar.Top + (taskbar.Height - overlaySize.Height) / 2;
        if (x < snapshot.AppsRight + gap)
        {
            WarnOnce(NoSpaceMessage);
            return Hide(OverlayHideReason.NoSpace);
        }

        if (_warned)
        {
            _warned = false;
            logger.Write(LogLevel.Info, "タスクバーに残量を出せるようになりました。");
        }

        return new OverlayDecision(new Rectangle(x, y, overlaySize.Width, overlaySize.Height), OverlayHideReason.None);
    }

    private void WarnOnce(string reason)
    {
        if (_warned)
        {
            return;
        }

        _warned = true;
        logger.Write(LogLevel.Warn, $"タスクバーに残量を出せません: {reason}");
    }

    private static OverlayDecision Hide(OverlayHideReason reason) => new(null, reason);
}
