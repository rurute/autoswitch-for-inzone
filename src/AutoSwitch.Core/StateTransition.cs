namespace AutoSwitch.Core;

public static class StateTransition
{
    /// <summary>
    /// 同じ状態へのイベントは遷移とみなさない。INZONE Hub は ConnectDevice を重複送出するうえ、
    /// ユーザーが手動で出力を変えた後に再送されても上書きしないための根拠でもある。
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
