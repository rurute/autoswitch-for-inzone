namespace AutoSwitch.Core;

public static class StateTransition
{
    /// <summary>
    /// 同じ状態は遷移とみなさない。ドングルへの問い合わせは、状態が変わっていなくても毎回同じ状態を返すので、
    /// そのたびに切り替え直さないため。ユーザーが手動で出力を変えた後に、同じ状態が返っても上書きしないための根拠でもある。
    /// </summary>
    public static (bool Changed, HeadsetState NewState) Resolve(HeadsetState current, HeadsetState incoming)
    {
        if (!Enum.IsDefined(current))
        {
            throw new ArgumentException($"current が不正です: '{current}'", nameof(current));
        }
        if (incoming is not (HeadsetState.Connected or HeadsetState.Disconnected))
        {
            throw new ArgumentException($"incoming が不正です: '{incoming}'", nameof(incoming));
        }

        return current == incoming ? (false, current) : (true, incoming);
    }
}
