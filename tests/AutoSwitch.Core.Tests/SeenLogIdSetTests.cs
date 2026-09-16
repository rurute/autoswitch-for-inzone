namespace AutoSwitch.Core.Tests;

public class SeenLogIdSetTests
{
    [Fact]
    public void Add_SameLogIdTwice_ReturnsTrueThenFalse()
    {
        var set = new SeenLogIdSet(10);

        Assert.True(set.Add("a"));
        Assert.False(set.Add("a"));
    }

    // 旧版は PowerShell の hashtable（キーの大文字小文字を区別しない）で既読を判定している
    [Fact]
    public void Add_DifferentCaseOnly_IsTreatedAsSeen()
    {
        var set = new SeenLogIdSet(10);

        Assert.True(set.Add("abc"));
        Assert.False(set.Add("ABC"));
    }

    [Fact]
    public void Add_OverCapacity_EvictsOldest()
    {
        var set = new SeenLogIdSet(3);
        set.Add("a");
        set.Add("b");
        set.Add("c");
        set.Add("d");   // a が押し出される

        Assert.Equal(3, set.Count);
        Assert.True(set.Add("a"));
        Assert.False(set.Add("c"));
    }

    [Fact]
    public void DefaultCapacity_Is200()
    {
        Assert.Equal(200, new SeenLogIdSet().Capacity);
    }
}
