namespace AutoSwitch.Core.Tests;

public class BatteryDisplayTests
{
    [Fact] public void LevelToShow_BothEars_ReturnsLower() => Assert.Equal(60, BatteryDisplay.LevelToShow(new BudsLevels(80, 60, 100)));
    [Fact] public void LevelToShow_OnlyRight_ReturnsRight() => Assert.Equal(70, BatteryDisplay.LevelToShow(new BudsLevels(null, 70, 10)));
    [Fact] public void LevelToShow_LeftNull_RightValue_IgnoresNull() => Assert.Equal(5, BatteryDisplay.LevelToShow(new BudsLevels(null, 5, null)));
    [Fact] public void LevelToShow_NoEars_ReturnsNull() => Assert.Null(BatteryDisplay.LevelToShow(new BudsLevels(null, null, 90)));
    [Fact] public void LevelToShow_NullLevels_ReturnsNull() => Assert.Null(BatteryDisplay.LevelToShow(null));
    [Fact] public void LevelToShow_CaseLowerThanEars_IgnoresCase() => Assert.Equal(90, BatteryDisplay.LevelToShow(new BudsLevels(90, 95, 3)));

    [Theory]
    [InlineData(21, BatteryTone.Normal)]
    [InlineData(100, BatteryTone.Normal)]
    [InlineData(20, BatteryTone.Low)]
    [InlineData(0, BatteryTone.Low)]
    public void ToneFor_Threshold20(int level, BatteryTone expected) => Assert.Equal(expected, BatteryDisplay.ToneFor(level));
}
