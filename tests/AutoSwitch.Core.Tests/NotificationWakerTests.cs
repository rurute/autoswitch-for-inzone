namespace AutoSwitch.Core.Tests;

public class NotificationWakerTests
{
    // 2026-11-25 の記録（知らせ、event 0x01・0x41）
    private static readonly byte[] Notification = Convert.FromHexString("020e04ff0b0096c31201a0000000010d");
    private static readonly byte[] NotificationEvent41 = Convert.FromHexString("021004ff0d0096c31441a001000014ff0062");

    // battery-hub の spec の実測値（TID=1 の返答、type 0x10）
    private static readonly byte[] Reply = Convert.FromHexString("021204ff0f0096c3140410010000630063ff60a7");

    // 予約を記録し、テストが手で実行する
    private sealed class Reservation(TimeSpan delay, Action action) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;
        public Action Action { get; } = action;
        public bool Cancelled { get; private set; }

        public void Dispose() => Cancelled = true;
    }

    private sealed class Harness
    {
        public int WakeCount;
        public readonly List<Reservation> Reservations = [];
        public readonly NotificationWaker Waker;

        public Harness(Action? wake = null)
        {
            Waker = new NotificationWaker(
                wake ?? (() => WakeCount++),
                (delay, action) =>
                {
                    var r = new Reservation(delay, action);
                    Reservations.Add(r);
                    return r;
                });
        }

        public IEnumerable<Reservation> Active => Reservations.Where(r => !r.Cancelled);

        // 「問い合わせ」は OnQueryStarting() → OnQueryCompleted(status) の組で表す
        public void Query(ProbeStatus status)
        {
            Waker.OnQueryStarting();
            Waker.OnQueryCompleted(status);
        }
    }

    [Fact]
    public void FollowUpDelay_IsOneSecond()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), NotificationWaker.FollowUpDelay);
    }

    [Fact]
    public void OnFrame_Notification_WakesWithoutScheduling()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);

        Assert.Equal(1, h.WakeCount);
        Assert.Empty(h.Reservations);
    }

    [Fact]
    public void OnFrame_NotificationEvent41_Wakes()
    {
        var h = new Harness();

        h.Waker.OnFrame(NotificationEvent41);

        Assert.Equal(1, h.WakeCount);
    }

    [Fact]
    public void OnFrame_Reply_DoesNothing()
    {
        var h = new Harness();

        h.Waker.OnFrame(Reply);

        Assert.Equal(0, h.WakeCount);
        Assert.Empty(h.Reservations);
    }

    [Fact]
    public void OnFrame_ReplyBeforeQuery_DoesNotArm()
    {
        var h = new Harness();

        h.Waker.OnFrame(Reply);
        h.Query(ProbeStatus.Connected);

        Assert.Empty(h.Reservations);
    }

    [Fact]
    public void Query_ConnectedAfterNotification_SchedulesOneFollowUp()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Connected);

        var r = Assert.Single(h.Reservations);
        Assert.Equal(TimeSpan.FromSeconds(1), r.Delay);
        Assert.False(r.Cancelled);

        r.Action();

        Assert.Equal(2, h.WakeCount);
    }

    [Fact]
    public void Query_DisconnectedAfterNotification_DoesNotSchedule()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Disconnected);

        Assert.Empty(h.Reservations);
    }

    [Fact]
    public void Query_UnavailableAfterNotification_DoesNotSchedule()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Unavailable);

        Assert.Empty(h.Reservations);
    }

    [Fact]
    public void Query_WithoutNotification_DoesNotSchedule()
    {
        var h = new Harness();

        h.Query(ProbeStatus.Connected);
        h.Query(ProbeStatus.Connected);

        Assert.Empty(h.Reservations);
    }

    [Fact]
    public void Query_TwiceAfterOneNotification_SchedulesOnlyOnce()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Connected);
        h.Query(ProbeStatus.Connected);

        var r = Assert.Single(h.Reservations);
        Assert.False(r.Cancelled);
    }

    [Fact]
    public void FollowUpQuery_Connected_DoesNotScheduleAgain()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Connected);
        h.Reservations[0].Action();
        // 追いかけで起こされた問い合わせ
        h.Query(ProbeStatus.Connected);

        Assert.Single(h.Reservations);
    }

    [Fact]
    public void Notification_DuringQuery_ArmsTheNextQueryOnly()
    {
        var h = new Harness();

        h.Waker.OnQueryStarting();                       // A
        h.Waker.OnFrame(Notification);
        h.Waker.OnQueryCompleted(ProbeStatus.Connected); // A

        Assert.Empty(h.Reservations);

        h.Query(ProbeStatus.Connected);                  // B

        var r = Assert.Single(h.Reservations);
        Assert.False(r.Cancelled);
    }

    [Fact]
    public void Notification_DuringQuery_NextUnavailable_LeavesNoFollowUp()
    {
        var h = new Harness();

        h.Waker.OnQueryStarting();                       // A
        h.Waker.OnFrame(Notification);
        h.Waker.OnQueryCompleted(ProbeStatus.Connected); // A
        h.Query(ProbeStatus.Unavailable);                // B

        Assert.Empty(h.Active);
    }

    [Fact]
    public void Notification_DuringArmedQuery_KeepsOneActiveFollowUp()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);                   // N1
        h.Waker.OnQueryStarting();                       // A
        h.Waker.OnFrame(Notification);                   // N2
        h.Waker.OnQueryCompleted(ProbeStatus.Connected); // A → T1

        var t1 = Assert.Single(h.Reservations);

        h.Query(ProbeStatus.Connected);                  // B → T2

        Assert.Equal(2, h.Reservations.Count);
        var t2 = h.Reservations[1];
        Assert.True(t1.Cancelled);
        Assert.Same(t2, Assert.Single(h.Active));
    }

    [Fact]
    public void Query_NotConnected_CancelsPendingFollowUp()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Connected);
        var r = Assert.Single(h.Reservations);

        h.Query(ProbeStatus.Unavailable);

        Assert.True(r.Cancelled);
        Assert.Empty(h.Active);
    }

    [Fact]
    public void Query_Disconnected_CancelsPendingFollowUp()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Connected);

        h.Query(ProbeStatus.Disconnected);

        Assert.Empty(h.Active);
    }

    [Fact]
    public void OnFrame_Burst_SchedulesOneFollowUpAfterLastQuery()
    {
        var h = new Harness();

        for (var i = 0; i < 20; i++)
        {
            h.Waker.OnFrame(Notification);
        }

        Assert.Equal(20, h.WakeCount);
        Assert.Empty(h.Reservations);

        h.Query(ProbeStatus.Connected);

        var first = Assert.Single(h.Reservations);
        Assert.False(first.Cancelled);

        h.Waker.OnFrame(Notification);

        Assert.True(first.Cancelled);

        h.Query(ProbeStatus.Connected);

        Assert.Equal(2, h.Reservations.Count);
        Assert.Same(h.Reservations[1], Assert.Single(h.Active));
    }

    [Fact]
    public void CancelledFollowUp_RunLate_DoesNotWake()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Connected);
        var r = h.Reservations[0];
        h.Query(ProbeStatus.Unavailable);
        var before = h.WakeCount;

        // 取り消しと同時に、すでに走り始めていたタイマー
        r.Action();

        Assert.Equal(before, h.WakeCount);
    }

    [Fact]
    public void Dispose_CancelsPendingAndIgnoresLaterCalls()
    {
        var h = new Harness();

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Connected);
        var r = Assert.Single(h.Reservations);
        var before = h.WakeCount;

        h.Waker.Dispose();

        Assert.True(r.Cancelled);

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Connected);
        r.Action();

        Assert.Equal(before, h.WakeCount);
        Assert.Single(h.Reservations);
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        var h = new Harness();

        h.Waker.Dispose();
        h.Waker.Dispose();
    }

    [Fact]
    public void OnFrame_WakeThrows_DoesNotThrow()
    {
        var h = new Harness(() => throw new InvalidOperationException("wake failed"));

        h.Waker.OnFrame(Notification);
    }

    [Fact]
    public void FollowUp_WakeThrows_DoesNotThrow()
    {
        var calls = 0;
        var h = new Harness(() =>
        {
            calls++;
            throw new InvalidOperationException("wake failed");
        });

        h.Waker.OnFrame(Notification);
        h.Query(ProbeStatus.Connected);
        h.Reservations[0].Action();

        Assert.Equal(2, calls);
    }
}
