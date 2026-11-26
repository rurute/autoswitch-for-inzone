using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoSwitch.App;

/// <summary>
/// タスクバーの残量表示を出す、最前面のレイヤード窓（Issue rurute/inzone-auto-switch#27）。UI スレッドで作り、UI スレッドから使う。
/// 枠なし・タスクバーと Alt+Tab に出ない・アクティブにならない・クリックは素通し（WS_EX_TRANSPARENT）。
/// 座標は物理ピクセルで扱うため、窓を作るときと動かすときは、呼んだスレッドの DPI の扱いをその間だけ Per-Monitor V2 に切り替える。
/// 失敗は例外で知らせる（受け取る TaskbarOverlayController が記録して隠す）。
/// </summary>
internal sealed class TaskbarOverlayWindow : NativeWindow, IOverlaySurface, IDisposable
{
    private const int DefaultDpi = 96;

    private bool _disposed;

    /// <summary>WM_SETTINGCHANGE（テーマの切り替えなど）を受けたとき、UI スレッドで発火する。</summary>
    public event Action? SettingsChanged;

    public TaskbarOverlayWindow()
    {
        var parameters = new CreateParams
        {
            Caption = "AutoSwitch for INZONE taskbar battery",
            Style = NativeMethods.WS_POPUP,
            ExStyle = NativeMethods.WS_EX_LAYERED
                | NativeMethods.WS_EX_TRANSPARENT
                | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TOPMOST,
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
        };

        // 窓の DPI の扱いは、作ったときのスレッドの扱いで決まる
        WithPerMonitorDpi(() => CreateHandle(parameters));
    }

    public int Dpi
    {
        get
        {
            ThrowIfDisposed();
            var dpi = (int)NativeMethods.GetDpiForWindow(Handle);
            return dpi > 0 ? dpi : DefaultDpi;
        }
    }

    public void ShowAt(Rectangle bounds, Bitmap image)
    {
        ThrowIfDisposed();
        WithPerMonitorDpi(() =>
        {
            Draw(bounds.Location, image);
            if (!NativeMethods.SetWindowPos(
                    Handle,
                    NativeMethods.HWND_TOPMOST,
                    bounds.X,
                    bounds.Y,
                    image.Width,
                    image.Height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos に失敗しました");
            }
        });
    }

    public void Hide()
    {
        ThrowIfDisposed();
        // 戻り値は直前に見えていたかどうかで、失敗を表さない
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SettingsChanged = null;
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_SETTINGCHANGE)
        {
            try
            {
                SettingsChanged?.Invoke();
            }
            catch (Exception)
            {
                // 窓のメッセージ処理から例外を出さない（UI スレッドの未処理例外の経路へ流さない）
            }
        }

        base.WndProc(ref m);
    }

    /// <summary>image（Format32bppPArgb）を、位置 location に、透過を保ったまま窓の中身として描く。</summary>
    private void Draw(Point location, Bitmap image)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception("画面の DC を取れません");
        }

        try
        {
            var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "描画用の DC を作れません");
            }

            try
            {
                var bitmap = image.GetHbitmap(Color.FromArgb(0));
                try
                {
                    var previous = NativeMethods.SelectObject(memoryDc, bitmap);
                    try
                    {
                        var destination = new NativeMethods.POINT { X = location.X, Y = location.Y };
                        var size = new NativeMethods.SIZE { cx = image.Width, cy = image.Height };
                        var source = new NativeMethods.POINT();
                        var blend = new NativeMethods.BLENDFUNCTION
                        {
                            BlendOp = NativeMethods.AC_SRC_OVER,
                            BlendFlags = 0,
                            SourceConstantAlpha = 255,
                            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
                        };

                        if (!NativeMethods.UpdateLayeredWindow(
                                Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, NativeMethods.ULW_ALPHA))
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateLayeredWindow に失敗しました");
                        }
                    }
                    finally
                    {
                        NativeMethods.SelectObject(memoryDc, previous);
                    }
                }
                finally
                {
                    NativeMethods.DeleteObject(bitmap);
                }
            }
            finally
            {
                NativeMethods.DeleteDC(memoryDc);
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>呼んだスレッドの DPI の扱いを、action の間だけ Per-Monitor V2 にする。切り替えも戻しも、失敗したら例外にする。</summary>
    private static void WithPerMonitorDpi(Action action)
    {
        var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (previous == IntPtr.Zero)
        {
            throw new InvalidOperationException("DPI の扱いを切り替えられません");
        }

        bool restored;
        try
        {
            action();
        }
        finally
        {
            restored = NativeMethods.SetThreadDpiAwarenessContext(previous) != IntPtr.Zero;
        }

        if (!restored)
        {
            throw new InvalidOperationException("DPI の扱いを元に戻せません");
        }
    }
}
