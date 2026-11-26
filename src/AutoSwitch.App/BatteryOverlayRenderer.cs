using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// タスクバーの残量表示（左にヘッドホンの絵、右に「78%」）を、背景が透明なビットマップに描く。
/// 絵と文字はダークのタスクバーで白、ライトで黒。残量が少ないときは数字と % だけオレンジにする。
/// 大きさは 96 dpi を基準に dpi に合わせて拡大する。
/// </summary>
internal static class BatteryOverlayRenderer
{
    private static readonly Color DarkLow = Color.FromArgb(255, 0xFF, 0x9F, 0x43);
    private static readonly Color LightLow = Color.FromArgb(255, 0xC2, 0x41, 0x0C);

    // 96 dpi のときの寸法（ピクセル）
    private const int BaseHeight = 32;
    private const float BaseFontSize = 13f;
    private const float BaseGlyphSide = 16f;
    private const float BaseGap = 4f;
    private const float BasePadding = 1f;

    /// <summary>
    /// 呼び出し側が Dispose する。PixelFormat は UpdateLayeredWindow に渡せる Format32bppPArgb。
    /// </summary>
    public static Bitmap Render(int level, bool lightTheme, int dpi)
    {
        var scale = dpi / 96f;
        var height = BaseHeight * dpi / 96;
        var glyphSide = BaseGlyphSide * scale;
        var gap = BaseGap * scale;
        var padding = BasePadding * scale;

        var foreground = lightTheme ? Color.Black : Color.White;
        var textColor = BatteryDisplay.ToneFor(level) == BatteryTone.Low
            ? (lightTheme ? LightLow : DarkLow)
            : foreground;
        var text = $"{level}%";

        using var font = new Font("Segoe UI", BaseFontSize * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        format.LineAlignment = StringAlignment.Center;

        var textWidth = MeasureTextWidth(text, font, format);
        var width = (int)Math.Ceiling(padding + glyphSide + gap + textWidth + padding);

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // 透過の上に描くので ClearType は使わない（色のにじみが縁に残る）
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var glyphTop = (height - glyphSide) / 2f;
            var state = graphics.Save();
            graphics.TranslateTransform(padding, glyphTop);
            graphics.ScaleTransform(glyphSide / 16f, glyphSide / 16f);
            DrawHeadphones(graphics, foreground);
            graphics.Restore(state);

            using var brush = new SolidBrush(textColor);
            var textLeft = padding + glyphSide + gap;
            graphics.DrawString(text, font, brush, new RectangleF(textLeft, 0f, textWidth + padding, height), format);

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static float MeasureTextWidth(string text, Font font, StringFormat format)
    {
        using var probe = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(probe);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return graphics.MeasureString(text, font, PointF.Empty, format).Width;
    }

    // TrayIcons と同じヘッドホンの形（16 単位のグリッド）。タイルは描かない
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
