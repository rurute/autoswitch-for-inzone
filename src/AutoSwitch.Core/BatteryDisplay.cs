namespace AutoSwitch.Core;

// タスクバーに出す残量の色の段階。Low は数字と % をオレンジにする
public enum BatteryTone { Normal, Low }

// タスクバーに出す残量の値と、その色の段階を決める
public static class BatteryDisplay
{
    // この値以下で Low（固定。設定項目にしない）
    public const int LowThreshold = 20;

    // 左右のうち低いほう。片耳だけ報告があればその値。ケースの値は使わない。
    // 左右とも報告なし（null）なら null（出さない）。null を 0 とみなさない
    public static int? LevelToShow(BudsLevels? levels)
    {
        if (levels is null) return null;
        return (levels.Left, levels.Right) switch
        {
            (int left, int right) => Math.Min(left, right),
            (int left, null) => left,
            (null, int right) => right,
            _ => null,
        };
    }

    public static BatteryTone ToneFor(int level) =>
        level <= LowThreshold ? BatteryTone.Low : BatteryTone.Normal;
}
