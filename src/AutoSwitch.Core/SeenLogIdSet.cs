namespace AutoSwitch.Core;

/// <summary>
/// 既読の logId を容量付きで保持する。超過時は最古から破棄する。
/// 旧版は PowerShell の hashtable で判定しているため、大文字小文字を区別しない。
/// </summary>
public sealed class SeenLogIdSet
{
    private readonly HashSet<string> _lookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();

    public SeenLogIdSet(int capacity = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _lookup.Count;

    /// <returns>新規なら true</returns>
    public bool Add(string logId)
    {
        ArgumentException.ThrowIfNullOrEmpty(logId);

        if (!_lookup.Add(logId))
        {
            return false;
        }

        _order.Enqueue(logId);
        while (_order.Count > Capacity)
        {
            _lookup.Remove(_order.Dequeue());
        }

        return true;
    }
}
