using System.Diagnostics;

namespace AutoSwitch.Core.Tests;

// 流れ（再生・録音）ひとつ分のフェイクの状態。一覧・既定・各操作の例外を設定でき、SetDefault に渡された ID を記録する
public sealed class FakeFlowState
{
    public List<AudioDevice> Devices { get; } = new();

    /// <summary>Console・Multimedia・Communications の順の既定。テストから個別のロールを書き換えられる</summary>
    public AudioDevice?[] Defaults { get; set; } = [null, null, null];

    /// <summary>3ロールとも同じ既定にする。読むと Multimedia の既定を返す</summary>
    public AudioDevice? Default
    {
        get => Defaults[1];
        set => Defaults = [value, value, value];
    }

    public Exception? GetDevicesException { get; set; }

    public Exception? GetDefaultException { get; set; }

    public Exception? SetDefaultException { get; set; }

    /// <summary>この流れの SetDefault に渡された ID。例外を投げる設定でも記録する</summary>
    public List<string> SetDefaultCalls { get; } = new();
}

// 音声セッションの代わり。流れごとに別の状態を持ち、Dispose の回数を記録する
public sealed class FakeAudioSession : IAudioSession
{
    public FakeFlowState Playback { get; } = new();

    public FakeFlowState Capture { get; } = new();

    public Exception? DisposeException { get; set; }

    public int DisposeCount { get; private set; }

    public IReadOnlyList<AudioDevice> GetDevices(DeviceFlow flow)
    {
        var state = For(flow);
        if (state.GetDevicesException is not null)
        {
            throw state.GetDevicesException;
        }
        return state.Devices.ToList();
    }

    public IReadOnlyList<AudioDevice?> GetDefaults(DeviceFlow flow)
    {
        var state = For(flow);
        if (state.GetDefaultException is not null)
        {
            throw state.GetDefaultException;
        }
        return state.Defaults.ToList();
    }

    public void SetDefault(DeviceFlow flow, string id)
    {
        var state = For(flow);
        state.SetDefaultCalls.Add(id);
        if (state.SetDefaultException is not null)
        {
            throw state.SetDefaultException;
        }
    }

    public void Dispose()
    {
        DisposeCount++;
        if (DisposeException is not null)
        {
            throw DisposeException;
        }
    }

    private FakeFlowState For(DeviceFlow flow) => flow switch
    {
        DeviceFlow.Playback => Playback,
        DeviceFlow.Capture => Capture,
        _ => throw new ArgumentOutOfRangeException(nameof(flow)),
    };
}

// 時計の代わり。テストから現在時刻を進められる
public sealed class FakeClock : IClock
{
    public FakeClock(DateTime utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTime UtcNow { get; set; }
}

// INZONE Hub のプロセス検出の代わり。null は Hub が起動していないことを表す
public sealed class FakeHubProcessProbe : IHubProcessProbe
{
    public DateTime? StartTimeUtc { get; set; }

    public int CallCount { get; private set; }

    public DateTime? GetStartTimeUtc()
    {
        CallCount++;
        return StartTimeUtc;
    }
}

// 切り替えの代わり。呼ばれた名前と流れを順に記録し、設定した結果を返すか例外を投げる
public sealed class RecordingSwitcher : IAudioSwitcher
{
    public SwitchResult Result { get; set; } = SwitchResult.Switched;

    /// <summary>録音の結果。null なら Result を使う</summary>
    public SwitchResult? CaptureResult { get; set; }

    public Exception? Exception { get; set; }

    /// <summary>記録の直後、結果を返す前に呼ばれる。テストが切り替えの途中で取り消しを要求するのに使う</summary>
    public Action<DeviceFlow, string>? OnSwitch { get; set; }

    /// <summary>SwitchTo に渡された名前（流れを問わず呼ばれた順）。例外を投げる設定でも記録する</summary>
    public List<string> Calls { get; } = new();

    /// <summary>SwitchTo に渡された流れと名前（呼ばれた順）。例外を投げる設定でも記録する</summary>
    public List<(DeviceFlow Flow, string Name)> FlowCalls { get; } = new();

    public SwitchOutcome SwitchTo(DeviceFlow flow, string deviceName)
    {
        Calls.Add(deviceName);
        FlowCalls.Add((flow, deviceName));
        OnSwitch?.Invoke(flow, deviceName);
        if (Exception is not null)
        {
            throw Exception;
        }

        if (flow == DeviceFlow.Capture)
        {
            var captureResult = CaptureResult ?? Result;
            return new SwitchOutcome(captureResult, $"Capture {captureResult}: {deviceName}");
        }
        return new SwitchOutcome(Result, $"{Result}: {deviceName}");
    }
}

// ロガーの代わり。レベルと文言を順に記録する
public sealed class ListLogger : IAppLogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public void Write(LogLevel level, string message) => Entries.Add((level, message));
}

// 待機の代わり。要求された待機時間を順に記録し、呼び出しごとに設定した動作（true / false / 例外）を行う。
// Wait と WaitIgnoringWake は同じ手順を順に使う。
// 設定を使い切った後の呼び出しは false を返す（テストのループを必ず終わらせるため）
public sealed class ScriptedWaiter : IWaiter
{
    private readonly Queue<Func<bool>> _steps = new();

    /// <summary>Wait に渡された待機時間。例外を投げる設定でも記録する</summary>
    public List<TimeSpan> Durations { get; } = new();

    /// <summary>WaitIgnoringWake に渡された待機時間。例外を投げる設定でも記録する</summary>
    public List<TimeSpan> IgnoringWakeDurations { get; } = new();

    /// <summary>Wait と WaitIgnoringWake の呼び出しを呼ばれた順に記録する。IgnoringWake は WaitIgnoringWake のとき true</summary>
    public List<(TimeSpan Duration, bool IgnoringWake)> AllWaits { get; } = new();

    public ScriptedWaiter Returns(params bool[] results)
    {
        foreach (var result in results)
        {
            _steps.Enqueue(() => result);
        }
        return this;
    }

    public ScriptedWaiter Throws(Exception exception)
    {
        _steps.Enqueue(() => throw exception);
        return this;
    }

    public ScriptedWaiter Does(Func<bool> step)
    {
        _steps.Enqueue(step);
        return this;
    }

    public bool Wait(TimeSpan duration, CancellationToken token)
    {
        Durations.Add(duration);
        AllWaits.Add((duration, false));
        return NextStep();
    }

    bool IWaiter.WaitIgnoringWake(TimeSpan duration, CancellationToken token)
    {
        IgnoringWakeDurations.Add(duration);
        AllWaits.Add((duration, true));
        return NextStep();
    }

    private bool NextStep() => _steps.Count > 0 && _steps.Dequeue()();
}

// 時計の代わり。UtcNow を読むたびに回数と経過時間（Stopwatch）を記録し、OnRead に読んだ回数を渡して呼ぶ。
// MonitorLoop.Run は StartupSync を呼ばず、Tick は UtcNow を 1 回だけ読むので、読んだ回数が Tick の回数になる。
// 監視ループのスレッドから読まれ、テストのスレッドから数えるのでロックする
public sealed class CountingClock : IClock
{
    private readonly object _lock = new();
    private readonly List<TimeSpan> _reads = new();
    private readonly long _start = Stopwatch.GetTimestamp();

    /// <summary>UtcNow を読んだ直後に、読んだ回数（1 始まり）を渡して呼ばれる</summary>
    public Action<int>? OnRead { get; set; }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _reads.Count;
            }
        }
    }

    /// <summary>UtcNow を読んだ時刻（作成からの経過時間）を読んだ順に返す</summary>
    public TimeSpan[] ReadTimes()
    {
        lock (_lock)
        {
            return _reads.ToArray();
        }
    }

    public DateTime UtcNow
    {
        get
        {
            int count;
            lock (_lock)
            {
                _reads.Add(Stopwatch.GetElapsedTime(_start));
                count = _reads.Count;
            }
            OnRead?.Invoke(count);
            return DateTime.UtcNow;
        }
    }
}
