using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// 3状態のトレイアイコン。起動時に GDI+ で描き、終了時にまとめて解放する。
/// spec §2 のとおり独自のアイコン生成ツールは作らず、テーマ・DPI の変更には追従しない。
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Icon _connected;
    private readonly Icon _disconnected;
    private readonly Icon _unknown;

    public TrayIcons()
    {
        // 2つ目・3つ目の生成が失敗したとき、先に作ったアイコンを取り残さない
        var created = new List<Icon>(3);
        try
        {
            _connected = Keep(created, Create(Color.FromArgb(255, 60, 160, 90), filled: true));
            _disconnected = Keep(created, Create(Color.FromArgb(255, 130, 130, 130), filled: true));
            _unknown = Keep(created, Create(Color.Transparent, filled: false));
        }
        catch
        {
            foreach (var icon in created)
            {
                icon.Dispose();
            }

            throw;
        }
    }

    private static Icon Keep(List<Icon> created, Icon icon)
    {
        created.Add(icon);
        return icon;
    }

    public Icon For(HeadsetState state) => state switch
    {
        HeadsetState.Connected => _connected,
        HeadsetState.Disconnected => _disconnected,
        _ => _unknown,
    };

    public void Dispose()
    {
        _connected.Dispose();
        _disconnected.Dispose();
        _unknown.Dispose();
    }

    private static Icon Create(Color fill, bool filled)
    {
        var small = SystemInformation.SmallIconSize;
        var side = Math.Max(16, Math.Min(small.Width, small.Height));

        using var bitmap = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var pad = Math.Max(1, side / 8);
            var circle = new Rectangle(pad, pad, side - (pad * 2) - 1, side - (pad * 2) - 1);

            if (filled)
            {
                using var brush = new SolidBrush(fill);
                graphics.FillEllipse(brush, circle);
            }

            // 輪郭は明暗どちらのタスクバーでも見える中間の明るさにする。「不明」は塗りが無く
            // 輪郭だけなので、ここが背景に溶けると状態が読めない（Task 19 の実機確認で判明）
            using var pen = new Pen(Color.FromArgb(255, 150, 150, 150), Math.Max(2f, side / 8f));
            graphics.DrawEllipse(pen, circle);
        }

        // GetHicon のハンドルは Icon が所有しないため、複製してから元のハンドルを解放する
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
