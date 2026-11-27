namespace AutoSwitch.App.Tests;

/// <summary>
/// Issue #2。SDK は informational version の「+」の後ろに 40 文字のコミット SHA を付ける。
/// ログには先頭 7 文字だけを残し、メニューには「+」より前だけを出す。
/// </summary>
public sealed class AppVersionTests
{
    private const string FullSha = "adba3fe0123456789012345678901234567890ab";

    [Theory]
    [InlineData("0.1.0+" + FullSha, "0.1.0+adba3fe")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+abc", "0.1.0+abc")]
    [InlineData("0.1.0+", "0.1.0+")]
    [InlineData("0.1.0-beta.1+" + FullSha, "0.1.0-beta.1+adba3fe")]
    public void ログ用_SHA_を_7_文字に切る(string informational, string expected)
    {
        Assert.Equal(expected, AppVersion.ForLog(informational));
    }

    [Theory]
    [InlineData("0.1.0+" + FullSha, "0.1.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+", "0.1.0")]
    [InlineData("0.1.0-beta.1+" + FullSha, "0.1.0-beta.1")]
    public void メニュー用_プラスより前だけ(string informational, string expected)
    {
        Assert.Equal(expected, AppVersion.ForDisplay(informational));
    }

    [Fact]
    public void 実行中のアセンブリから読める()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Current));
    }
}
