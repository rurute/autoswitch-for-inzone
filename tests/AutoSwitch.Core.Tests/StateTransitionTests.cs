namespace AutoSwitch.Core.Tests;

public class StateTransitionTests
{
    [Theory]
    [InlineData(HeadsetState.Unknown, HeadsetState.Connected)]
    [InlineData(HeadsetState.Unknown, HeadsetState.Disconnected)]
    [InlineData(HeadsetState.Connected, HeadsetState.Disconnected)]
    [InlineData(HeadsetState.Disconnected, HeadsetState.Connected)]
    public void Resolve_DifferentState_IsChangedToIncoming(HeadsetState current, HeadsetState incoming)
    {
        var (changed, newState) = StateTransition.Resolve(current, incoming);

        Assert.True(changed);
        Assert.Equal(incoming, newState);
    }

    // 問い合わせのたびに同じ状態が返っても、手動変更後に切り替え直さないための根拠
    [Theory]
    [InlineData(HeadsetState.Connected)]
    [InlineData(HeadsetState.Disconnected)]
    public void Resolve_SameState_IsNotChanged(HeadsetState current)
    {
        var (changed, newState) = StateTransition.Resolve(current, current);

        Assert.False(changed);
        Assert.Equal(current, newState);
    }

    [Fact]
    public void Resolve_UndefinedCurrent_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => StateTransition.Resolve((HeadsetState)99, HeadsetState.Connected));
    }

    // Unknown はイベントにならない。未定義値はシリアル化を避けるため int で渡す
    [Theory]
    [InlineData((int)HeadsetState.Unknown)]
    [InlineData(99)]
    public void Resolve_InvalidIncoming_Throws(int incoming)
    {
        Assert.ThrowsAny<ArgumentException>(() => StateTransition.Resolve(HeadsetState.Connected, (HeadsetState)incoming));
    }
}
