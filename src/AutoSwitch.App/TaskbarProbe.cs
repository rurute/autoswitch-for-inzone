using System.Drawing;
using Microsoft.Win32;
using Automation = System.Windows.Automation;

namespace AutoSwitch.App;

/// <summary>タスクバーの位置・前面の窓・テーマを調べる（Issue rurute/inzone-auto-switch#27）。偽物に差し替えて判定をテストするための入口。</summary>
internal interface ITaskbarProbe
{
    /// <summary>メインのモニターのタスクバーを測る。例外は外へ出さず、失敗は Error に入れる。</summary>
    TaskbarProbeResult Measure();

    /// <summary>前面の窓がメインのモニターを覆っているとき true。</summary>
    bool IsFullscreenForeground();

    /// <summary>タスクバーがライトのテーマのとき true。読めなければ false。</summary>
    bool IsLightTheme();
}

/// <summary>
/// Win32 と UI Automation でタスクバーを測る。UI Automation は時間がかかりうるので、UI スレッドの外から呼ぶ。
/// Explorer の再起動で窓が作り直されるため、窓ハンドルは覚えておかず、測るたびに FindWindow し直す。
/// 座標はすべて物理ピクセル（呼んだスレッドの DPI の扱いを、測る間だけ Per-Monitor V2 に切り替える）。
/// </summary>
internal sealed class Win32TaskbarProbe : ITaskbarProbe
{
    private const string TaskbarClass = "Shell_TrayWnd";
    private const string TrayClass = "TrayNotifyWnd";
    private const string AppButtonClass = "Taskbar.TaskListButtonAutomationPeer";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly HashSet<string> ShellClasses =
        new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    public TaskbarProbeResult Measure()
    {
        try
        {
            return WithPerMonitorDpi(MeasureCore);
        }
        catch (Exception ex)
        {
            return Fail($"タスクバーを測れませんでした（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    public bool IsFullscreenForeground()
    {
        try
        {
            return WithPerMonitorDpi(IsFullscreenForegroundCore);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static TaskbarProbeResult MeasureCore()
    {
        var taskbar = NativeMethods.FindWindow(TaskbarClass, null);
        if (taskbar == IntPtr.Zero)
        {
            return Fail($"{TaskbarClass} が見つかりません");
        }

        if (!NativeMethods.GetWindowRect(taskbar, out var taskbarRect))
        {
            return Fail($"{TaskbarClass} の位置を取れません");
        }

        var monitor = NativeMethods.MonitorFromWindow(taskbar, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return Fail("タスクバーのモニターの位置を取れません");
        }

        var tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, TrayClass, null);
        if (tray == IntPtr.Zero)
        {
            return Fail($"{TrayClass} が見つかりません");
        }

        if (!NativeMethods.GetWindowRect(tray, out var trayRect))
        {
            return Fail($"{TrayClass} の位置を取れません");
        }

        var appsRight = FindAppsRight(taskbar);
        if (appsRight is null)
        {
            return Fail("タスクバーのボタンが見つかりません");
        }

        var snapshot = new TaskbarSnapshot(ToRectangle(taskbarRect), ToRectangle(monitorInfo.rcMonitor), trayRect.Left, appsRight.Value);
        return new TaskbarProbeResult(snapshot, null);
    }

    /// <summary>アプリのボタンの右端のうち最も右のもの（切り上げ）。1 つも無ければ null。</summary>
    private static int? FindAppsRight(IntPtr taskbar)
    {
        // 位置はまとめて取り寄せる。1 つずつ問い合わせると、数え終わる前に消えたボタンで例外になる
        var cache = new Automation.CacheRequest { AutomationElementMode = Automation.AutomationElementMode.None };
        cache.Add(Automation.AutomationElement.BoundingRectangleProperty);
        var condition = new Automation.PropertyCondition(Automation.AutomationElement.ClassNameProperty, AppButtonClass);

        // root は取り寄せの外で取る。中で取ると root 自身も参照を持たない要素になり、FindAll が失敗する
        var root = Automation.AutomationElement.FromHandle(taskbar);
        Automation.AutomationElementCollection buttons;
        using (cache.Activate())
        {
            buttons = root.FindAll(Automation.TreeScope.Descendants, condition);
        }

        int? right = null;
        foreach (Automation.AutomationElement button in buttons)
        {
            var bounds = button.Cached.BoundingRectangle;
            // 画面に出ていないボタンは空の矩形（Right が -∞）になる
            if (bounds.IsEmpty || bounds.Width <= 0 || !double.IsFinite(bounds.Right))
            {
                continue;
            }

            var value = (int)Math.Ceiling(bounds.Right);
            right = right is null ? value : Math.Max(right.Value, value);
        }

        return right;
    }

    private static bool IsFullscreenForegroundCore()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var buffer = new char[256];
        var length = NativeMethods.GetClassName(foreground, buffer, buffer.Length);
        if (length > 0 && ShellClasses.Contains(new string(buffer, 0, length)))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(foreground, out var windowRect))
        {
            return false;
        }

        // メインのモニターは画面座標の原点を含む
        var primary = NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (primary == IntPtr.Zero || !NativeMethods.GetMonitorInfo(primary, ref monitorInfo))
        {
            return false;
        }

        return ToRectangle(windowRect).Contains(ToRectangle(monitorInfo.rcMonitor));
    }

    /// <summary>
    /// 呼んだスレッドの DPI の扱いを測る間だけ Per-Monitor V2 にし、最後に元へ戻す。アプリ全体の設定は変えない。
    /// 切り替えか戻しに失敗したら例外にする（座標が物理ピクセルだと言えないため。呼び出し側が Error または false にする）。
    /// </summary>
    private static T WithPerMonitorDpi<T>(Func<T> action)
    {
        var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (previous == IntPtr.Zero)
        {
            throw new InvalidOperationException("DPI の扱いを切り替えられません");
        }

        T result;
        bool restored;
        try
        {
            result = action();
        }
        finally
        {
            restored = NativeMethods.SetThreadDpiAwarenessContext(previous) != IntPtr.Zero;
        }

        if (!restored)
        {
            throw new InvalidOperationException("DPI の扱いを元へ戻せません");
        }

        return result;
    }

    private static TaskbarProbeResult Fail(string error) => new(null, error);

    private static Rectangle ToRectangle(NativeMethods.RECT rect) => Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
