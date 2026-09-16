using System.Drawing;
using AutoSwitch.Core;

namespace AutoSwitch.App.Tests;

/// <summary>
/// Issue rurute/inzone-auto-switch#10 の見た目のうち、状態を見分けるのに効く箇所だけを 16px の実ピクセルで確かめる。ヘッドバンドは 16px では半端なピクセルに掛かるため見ない。
/// 座標は 16 単位のグリッドで、(1, 8) はタイルの左端寄り、(4, 10) は左の耳当ての中、(8, 8) はタイルの中央。
/// </summary>
public sealed class TrayIconsTests
{
    [Fact]
    public void 接続_紫のタイルに白いヘッドホン()
    {
        using var bitmap = TrayIcons.Render(HeadsetState.Connected, 16);

        AssertNear(Color.FromArgb(0x9d, 0x7b, 0xff), bitmap.GetPixel(1, 8));
        AssertNear(Color.White, bitmap.GetPixel(4, 10));
    }

    [Fact]
    public void 切断_灰色のタイルに白いヘッドホン()
    {
        using var bitmap = TrayIcons.Render(HeadsetState.Disconnected, 16);

        AssertNear(Color.FromArgb(0x8b, 0x91, 0x99), bitmap.GetPixel(1, 8));
        AssertNear(Color.White, bitmap.GetPixel(4, 10));
    }

    [Fact]
    public void 不明_タイルを塗らず灰色で描く()
    {
        using var bitmap = TrayIcons.Render(HeadsetState.Unknown, 16);

        Assert.Equal(0, bitmap.GetPixel(8, 8).A);
        AssertNear(Color.FromArgb(0x9a, 0xa0, 0xa8), bitmap.GetPixel(4, 10));
    }

    [Fact]
    public void 状態ごとに別のアイコンを返す()
    {
        using var icons = new TrayIcons();

        Assert.NotSame(icons.For(HeadsetState.Connected), icons.For(HeadsetState.Disconnected));
        Assert.NotSame(icons.For(HeadsetState.Connected), icons.For(HeadsetState.Unknown));
        Assert.NotSame(icons.For(HeadsetState.Disconnected), icons.For(HeadsetState.Unknown));
    }

    private static void AssertNear(Color expected, Color actual)
    {
        const int tolerance = 24;
        var near = actual.A > 230
            && Math.Abs(expected.R - actual.R) <= tolerance
            && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance;
        Assert.True(near, $"期待 {expected} に近い色ではない: {actual}");
    }
}
