using System.Drawing;

namespace AutoSwitch.App.Tests;

/// <summary>
/// タスクバーの残量表示の描画を、ビットマップの実ピクセルで確かめる。
/// 色は縁のぼかしを避けるため、不透明（A == 255）のピクセルだけを見る。
/// </summary>
public sealed class BatteryOverlayRendererTests
{
    private static readonly Color DarkOrange = Color.FromArgb(0xFF, 0x9F, 0x43);
    private static readonly Color LightOrange = Color.FromArgb(0xC2, 0x41, 0x0C);

    [Fact]
    public void Render_DarkNormal_HasWhiteAndNoOrange()
    {
        using var bitmap = BatteryOverlayRenderer.Render(78, lightTheme: false, dpi: 96);

        Assert.True(HasNear(bitmap, Color.White), "白に近いピクセルが無い");
        Assert.False(HasNear(bitmap, DarkOrange), "ふだんの残量なのにオレンジのピクセルがある");
    }

    [Fact]
    public void Render_DarkLow_HasOrangeAndWhiteGlyph()
    {
        using var bitmap = BatteryOverlayRenderer.Render(18, lightTheme: false, dpi: 96);

        Assert.True(HasNear(bitmap, DarkOrange), "#FF9F43 に近いピクセルが無い");
        Assert.True(HasNear(bitmap, Color.White), "絵の白に近いピクセルが無い");
    }

    [Fact]
    public void Render_LightLow_HasDarkOrangeAndBlack()
    {
        using var bitmap = BatteryOverlayRenderer.Render(18, lightTheme: true, dpi: 96);

        Assert.True(HasNear(bitmap, LightOrange), "#C2410C に近いピクセルが無い");
        Assert.True(HasNear(bitmap, Color.Black), "絵の黒に近いピクセルが無い");
    }

    [Fact]
    public void Render_Background_IsTransparent()
    {
        using var bitmap = BatteryOverlayRenderer.Render(78, lightTheme: false, dpi: 96);

        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
    }

    [Fact]
    public void Render_Dpi144_Height48()
    {
        using var bitmap = BatteryOverlayRenderer.Render(78, lightTheme: false, dpi: 144);

        Assert.Equal(48, bitmap.Height);
    }

    [Fact]
    public void Render_WiderForThreeDigits()
    {
        using var hundred = BatteryOverlayRenderer.Render(100, lightTheme: false, dpi: 96);
        using var five = BatteryOverlayRenderer.Render(5, lightTheme: false, dpi: 96);

        Assert.True(hundred.Width > five.Width, $"100% の幅 {hundred.Width} が 5% の幅 {five.Width} より広くない");
    }

    // TrayIconsTests.AssertNear と同じ考え方。不透明なピクセルのうち、各成分の差が許容差以内のものがあるか
    private static bool HasNear(Bitmap bitmap, Color expected)
    {
        const int tolerance = 40;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var actual = bitmap.GetPixel(x, y);
                if (actual.A == 255
                    && Math.Abs(expected.R - actual.R) <= tolerance
                    && Math.Abs(expected.G - actual.G) <= tolerance
                    && Math.Abs(expected.B - actual.B) <= tolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
