using System.Diagnostics;

namespace AutoSwitch.Core.Tests;

// 起こせる待機。実時間を使うので、時間の下限は 10 ms の余裕を見て判定し、
// 上限は待機時間（10 秒・5 秒）より十分短いことだけを見る
public sealed class WakeableWaiterTests : IDisposable
{
    private static readonly TimeSpan LongWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _cts = new();

    public void Dispose() => _cts.Dispose();

    private static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);

    // 待機を呼んで、結果と経過時間を返す
    private static (bool Result, TimeSpan Elapsed) Measure(Func<bool> wait)
    {
        var start = Stopwatch.GetTimestamp();
        var result = wait();
        return (result, Stopwatch.GetElapsedTime(start));
    }

    [Fact]
    public void Wait_NotWoken_ReturnsTrueAfterDuration()
    {
        using var waiter = new WakeableWaiter(TimeSpan.Zero);

        var (result, elapsed) = Measure(() => waiter.Wait(Ms(200), _cts.Token));

        Assert.True(result);
        Assert.True(elapsed >= Ms(190), $"elapsed={elapsed}");
    }

    [Fact]
    public void Wait_WokenDuringWait_ReturnsTrueBeforeDuration()
    {
        using var waiter = new WakeableWaiter(TimeSpan.Zero);
        var waker = new Thread(() =>
        {
            Thread.Sleep(50);
            waiter.Wake();
        });
        waker.Start();

        var (result, elapsed) = Measure(() => waiter.Wait(LongWait, _cts.Token));
        waker.Join();

        Assert.True(result);
        Assert.True(elapsed < Quick, $"elapsed={elapsed}");
    }

    [Fact]
    public void Wait_WokenBeforeWait_ReturnsImmediately()
    {
        // Tick の最中に来た通知の代わり。待機の外で起こされた印は、次の Wait まで残る
        using var waiter = new WakeableWaiter(TimeSpan.Zero);
        waiter.Wake();

        var (result, elapsed) = Measure(() => waiter.Wait(LongWait, _cts.Token));

        Assert.True(result);
        Assert.True(elapsed < Ms(200), $"elapsed={elapsed}");
    }

    [Fact]
    public void Wait_WokenManyTimes_CoalescesIntoOneReturn()
    {
        using var waiter = new WakeableWaiter(TimeSpan.Zero);
        for (var i = 0; i < 50; i++)
        {
            waiter.Wake();
        }

        var (first, firstElapsed) = Measure(() => waiter.Wait(LongWait, _cts.Token));
        var (second, secondElapsed) = Measure(() => waiter.Wait(Ms(300), _cts.Token));

        Assert.True(first);
        Assert.True(firstElapsed < Quick, $"firstElapsed={firstElapsed}");
        Assert.True(second);
        Assert.True(secondElapsed >= Ms(290), $"secondElapsed={secondElapsed}");
    }

    [Fact]
    public void Wait_WokenRepeatedly_ReturnsNoSoonerThanMinInterval()
    {
        using var waiter = new WakeableWaiter(Ms(200));
        waiter.Wake();
        Assert.True(waiter.Wait(LongWait, _cts.Token));

        // 前回の戻りの直後に起こしても、最小間隔が過ぎるまでは戻らない
        var (result, elapsed) = Measure(() =>
        {
            waiter.Wake();
            return waiter.Wait(LongWait, _cts.Token);
        });

        Assert.True(result);
        Assert.True(elapsed >= Ms(190), $"elapsed={elapsed}");
        Assert.True(elapsed < Quick, $"elapsed={elapsed}");
    }

    [Fact]
    public void Wait_WokenWithSubMillisecondRemaining_ReturnsNoSoonerThanMinIntervalOnItsClock()
    {
        // 読むたびに 0.4 ms 進む時計に差し替える。最小間隔 1 ms の残りに 1 ms 未満の端数が出る。
        // 待機が戻った時刻は、待機がその戻りで最後に読んだ時刻として、前回の戻りからの間隔を見る
        var step = Stopwatch.Frequency * 4 / 10_000;
        long now = 0;
        var reads = new List<long>();
        long Read()
        {
            now += step;
            reads.Add(now);
            return now;
        }
        var minInterval = Ms(1);
        using var waiter = new WakeableWaiter(minInterval, Read);
        waiter.Wake();
        Assert.True(waiter.Wait(LongWait, _cts.Token));
        var previousReturn = reads[^1];

        waiter.Wake();
        Assert.True(waiter.Wait(LongWait, _cts.Token));
        var interval = Stopwatch.GetElapsedTime(previousReturn, reads[^1]);

        Assert.True(interval >= minInterval, $"interval={interval.TotalMilliseconds} ms");
    }

    [Fact]
    public void Wait_FirstCallWoken_DoesNotWaitMinInterval()
    {
        using var waiter = new WakeableWaiter(TimeSpan.FromSeconds(5));
        waiter.Wake();

        var (result, elapsed) = Measure(() => waiter.Wait(LongWait, _cts.Token));

        Assert.True(result);
        Assert.True(elapsed < Quick, $"elapsed={elapsed}");
    }

    [Fact]
    public void Wait_CanceledToken_ReturnsFalse()
    {
        using var waiter = new WakeableWaiter(TimeSpan.Zero);
        _cts.Cancel();

        var (result, elapsed) = Measure(() => waiter.Wait(LongWait, _cts.Token));

        Assert.False(result);
        Assert.True(elapsed < Quick, $"elapsed={elapsed}");
    }

    [Fact]
    public void Wait_CanceledAndWoken_ReturnsFalse()
    {
        // 取り消しと起こすが重なったら、取り消しを優先する
        using var waiter = new WakeableWaiter(TimeSpan.Zero);
        waiter.Wake();
        _cts.Cancel();

        Assert.False(waiter.Wait(LongWait, _cts.Token));
    }

    [Fact]
    public void Wait_CanceledDuringMinInterval_ReturnsFalse()
    {
        using var waiter = new WakeableWaiter(TimeSpan.FromSeconds(5));
        waiter.Wake();
        Assert.True(waiter.Wait(LongWait, _cts.Token));
        waiter.Wake();
        _cts.CancelAfter(Ms(100));

        var (result, elapsed) = Measure(() => waiter.Wait(LongWait, _cts.Token));

        Assert.False(result);
        Assert.True(elapsed < Quick, $"elapsed={elapsed}");
    }

    [Fact]
    public void Wait_NegativeDuration_ReturnsTrueWithoutException()
    {
        using var waiter = new WakeableWaiter(TimeSpan.Zero);

        Assert.True(waiter.Wait(TimeSpan.FromSeconds(-1), _cts.Token));
    }

    [Fact]
    public void Wait_DurationBeyondWaitHandleLimit_WithCanceledToken_ReturnsFalseWithoutException()
    {
        using var waiter = new WakeableWaiter(TimeSpan.Zero);
        _cts.Cancel();

        Assert.False(waiter.Wait(TimeSpan.MaxValue, _cts.Token));
    }

    [Fact]
    public void WaitIgnoringWake_Woken_WaitsFullDurationAndKeepsWake()
    {
        using var waiter = new WakeableWaiter(TimeSpan.Zero);
        waiter.Wake();

        var (ignoring, ignoringElapsed) = Measure(() => waiter.WaitIgnoringWake(Ms(300), _cts.Token));
        var (next, nextElapsed) = Measure(() => waiter.Wait(LongWait, _cts.Token));

        Assert.True(ignoring);
        Assert.True(ignoringElapsed >= Ms(290), $"ignoringElapsed={ignoringElapsed}");
        Assert.True(next);
        Assert.True(nextElapsed < Quick, $"nextElapsed={nextElapsed}");
    }

    [Fact]
    public void WaitIgnoringWake_CanceledToken_ReturnsFalse()
    {
        using var waiter = new WakeableWaiter(TimeSpan.Zero);
        _cts.Cancel();

        Assert.False(waiter.WaitIgnoringWake(LongWait, _cts.Token));
    }

    [Fact]
    public void Wake_AfterDispose_DoesNotThrow()
    {
        var waiter = new WakeableWaiter();
        waiter.Dispose();

        waiter.Wake();
    }

    [Fact]
    public void Wait_AfterDispose_ReturnsFalse()
    {
        var waiter = new WakeableWaiter();
        waiter.Dispose();

        Assert.False(waiter.Wait(LongWait, _cts.Token));
        Assert.False(waiter.WaitIgnoringWake(LongWait, _cts.Token));
    }

    [Fact]
    public void Wait_DisposedSource_ReturnsFalseWithoutException()
    {
        using var waiter = new WakeableWaiter(TimeSpan.Zero);
        var source = new CancellationTokenSource();
        var token = source.Token;
        source.Dispose();

        Assert.False(waiter.Wait(LongWait, token));
        Assert.False(waiter.WaitIgnoringWake(LongWait, token));
    }

    [Fact]
    public void DefaultMinInterval_IsTask1Value()
    {
        // 計画の Task 1 の計測で決めた値（100 ms のまま）
        Assert.Equal(TimeSpan.FromMilliseconds(100), WakeableWaiter.DefaultMinInterval);
    }
}
