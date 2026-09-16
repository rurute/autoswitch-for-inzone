namespace AutoSwitch.Core.Tests;

// 音声セッションの代わり。一覧・既定・各メソッドの例外を設定でき、切り替えの呼び出しと Dispose の回数を記録する
public sealed class FakePlaybackSession : IPlaybackSession
{
    public List<PlaybackDevice> Devices { get; } = new();

    public PlaybackDevice? Default { get; set; }

    public Exception? GetPlaybackDevicesException { get; set; }

    public Exception? GetDefaultPlaybackException { get; set; }

    public Exception? SetDefaultPlaybackException { get; set; }

    public Exception? DisposeException { get; set; }

    /// <summary>SetDefaultPlayback に渡された ID。例外を投げる設定でも記録する</summary>
    public List<string> SetDefaultPlaybackCalls { get; } = new();

    public int DisposeCount { get; private set; }

    public IReadOnlyList<PlaybackDevice> GetPlaybackDevices()
    {
        if (GetPlaybackDevicesException is not null)
        {
            throw GetPlaybackDevicesException;
        }
        return Devices.ToList();
    }

    public PlaybackDevice? GetDefaultPlayback()
    {
        if (GetDefaultPlaybackException is not null)
        {
            throw GetDefaultPlaybackException;
        }
        return Default;
    }

    public void SetDefaultPlayback(string id)
    {
        SetDefaultPlaybackCalls.Add(id);
        if (SetDefaultPlaybackException is not null)
        {
            throw SetDefaultPlaybackException;
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

// 切り替えの代わり。呼ばれた名前を順に記録し、設定した結果を返すか例外を投げる
public sealed class RecordingSwitcher : IPlaybackSwitcher
{
    public SwitchResult Result { get; set; } = SwitchResult.Switched;

    public Exception? Exception { get; set; }

    /// <summary>SwitchTo に渡された名前。例外を投げる設定でも記録する</summary>
    public List<string> Calls { get; } = new();

    public SwitchOutcome SwitchTo(string deviceName)
    {
        Calls.Add(deviceName);
        if (Exception is not null)
        {
            throw Exception;
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
// 設定を使い切った後の呼び出しは false を返す（テストのループを必ず終わらせるため）
public sealed class ScriptedWaiter : IWaiter
{
    private readonly Queue<Func<bool>> _steps = new();

    /// <summary>Wait に渡された待機時間。例外を投げる設定でも記録する</summary>
    public List<TimeSpan> Durations { get; } = new();

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
        return _steps.Count > 0 && _steps.Dequeue()();
    }
}
