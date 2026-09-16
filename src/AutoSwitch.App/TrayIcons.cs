using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// 3状態のトレイアイコン。起動時に GDI+ で描き、終了時にまとめて解放する。
/// spec §2 のとおり独自のアイコン生成ツールは作らず、テーマ・DPI の変更には追従しない。
/// 見た目は Issue rurute/inzone-auto-switch#10（角丸のタイルに白いヘッドホン。接続は紫、切断は灰色、不明は塗らずに枠だけ）。
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    // 接続は INZONE Hub の紫系に寄せる
    private static readonly Color ConnectedFill = Color.FromArgb(255, 0x9d, 0x7b, 0xff);
    private static readonly Color DisconnectedFill = Color.FromArgb(255, 0x8b, 0x91, 0x99);
    private static readonly Color UnknownLine = Color.FromArgb(255, 0x9a, 0xa0, 0xa8);

    private readonly Icon _connected;
    private readonly Icon _disconnected;
    private readonly Icon _unknown;

    public TrayIcons()
    {
        // 2つ目・3つ目の生成が失敗したとき、先に作ったアイコンを取り残さない
        var created = new List<Icon>(3);
        try
        {
            _connected = Keep(created, Create(HeadsetState.Connected));
            _disconnected = Keep(created, Create(HeadsetState.Disconnected));
            _unknown = Keep(created, Create(HeadsetState.Unknown));
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

    private static Icon Create(HeadsetState state)
    {
        var small = SystemInformation.SmallIconSize;
        var side = Math.Max(16, Math.Min(small.Width, small.Height));

        using var bitmap = Render(state, side);

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

    /// <summary>
    /// 16 単位のグリッドで描いた形を side ピクセルへ拡大して描く。テストから実ピクセルを確かめるために internal にしている。
    /// </summary>
    internal static Bitmap Render(HeadsetState state, int side)
    {
        var bitmap = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // 座標をピクセルの角に合わせ、グリッドの 0.5 がピクセルの中央に来るようにする
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform(side / 16f, side / 16f);

            Color glyph;
            if (state == HeadsetState.Unknown)
            {
                using var tile = RoundedRectangle(1f, 1f, 14f, 14f, 3.2f);
                using var pen = new Pen(UnknownLine, 1.3f);
                graphics.DrawPath(pen, tile);
                glyph = UnknownLine;
            }
            else
            {
                using var tile = RoundedRectangle(0.5f, 0.5f, 15f, 15f, 3.4f);
                using var brush = new SolidBrush(state == HeadsetState.Connected ? ConnectedFill : DisconnectedFill);
                graphics.FillPath(brush, tile);
                glyph = Color.White;
            }

            // ヘッドホンはタイルの中で少し下寄りに、0.72 倍で置く
            graphics.TranslateTransform(8f, 8.3f);
            graphics.ScaleTransform(0.72f, 0.72f);
            graphics.TranslateTransform(-8f, -8f);
            DrawHeadphones(graphics, glyph);

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void DrawHeadphones(Graphics graphics, Color color)
    {
        using (var band = new GraphicsPath())
        using (var pen = new Pen(color, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            band.AddLine(3.2f, 9.6f, 3.2f, 8.2f);
            band.AddArc(3.2f, 3.4f, 9.6f, 9.6f, 180f, 180f);
            band.AddLine(12.8f, 8.2f, 12.8f, 9.6f);
            graphics.DrawPath(pen, band);
        }

        using var brush = new SolidBrush(color);
        using var left = RoundedRectangle(1.6f, 8.6f, 3.8f, 5.8f, 1.3f);
        using var right = RoundedRectangle(10.6f, 8.6f, 3.8f, 5.8f, 1.3f);
        graphics.FillPath(brush, left);
        graphics.FillPath(brush, right);
    }

    private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(x, y, diameter, diameter, 180f, 90f);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270f, 90f);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }
}
