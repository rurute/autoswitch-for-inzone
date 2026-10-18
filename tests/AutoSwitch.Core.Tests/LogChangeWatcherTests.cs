using System.Diagnostics;
using System.Text;

namespace AutoSwitch.Core.Tests;

// ActionLog.log の見張り。一時フォルダの実ファイルで、変更通知で起こされることを確かめる。
// 起こされたことは onChange で Set し、最大 5 秒待つ。起こされないことは 500 ms 待って見る
public sealed class LogChangeWatcherTests : IDisposable
{
    private const string StartedMessage = "ActionLog.log の変更通知を受け取ります。";
    private const string StartFailedPrefix = "ActionLog.log の変更通知を受け取れません。1 秒ごとの読み取りだけで監視します: ";
    private const string ErrorPrefix = "ActionLog.log の変更通知でエラーが発生しました。見張りを作り直します: ";

    private static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShortRetry = TimeSpan.FromMilliseconds(100);

    private readonly string _dir;
    private readonly string _path;
    private readonly LockedLogger _logger = new();
    private readonly ManualResetEventSlim _changed = new(false);
    private int _changeCount;

    public LogChangeWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lcw-" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "ActionLog.log");
    }

    public void Dispose()
    {
        _changed.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnChange()
    {
        Interlocked.Increment(ref _changeCount);
        _changed.Set();
    }

    private LogChangeWatcher CreateWatcher(string? path = null, TimeSpan? retryInterval = null) =>
        retryInterval is TimeSpan retry
            ? new LogChangeWatcher(path ?? _path, OnChange, _logger, retry)
            : new LogChangeWatcher(path ?? _path, OnChange, _logger);

    private static void Append(string path, string text) => File.AppendAllText(path, text, Encoding.UTF8);

    // 条件が満たされるまで最大 timeout 待つ
    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start) > timeout)
            {
                return false;
            }
            Thread.Sleep(10);
        }
        return true;
    }

    [Fact]
    public void Start_ExistingDirectory_LogsInfoAndIsWatching()
    {
        using var watcher = CreateWatcher();

        watcher.Start();

        Assert.True(watcher.IsWatching);
        Assert.Equal([(LogLevel.Info, StartedMessage)], _logger.Snapshot());
    }

    [Fact]
    public void Append_AfterStart_CallsOnChange()
    {
        File.WriteAllText(_path, "");
        using var watcher = CreateWatcher();
        watcher.Start();

        Append(_path, "line\r\n");

        Assert.True(_changed.Wait(NotifyTimeout));
    }

    [Fact]
    public void Truncate_AfterStart_CallsOnChange()
    {
        File.WriteAllText(_path, "line1\r\nline2\r\n");
        using var watcher = CreateWatcher();
        watcher.Start();

        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(0);
        }

        Assert.True(_changed.Wait(NotifyTimeout));
    }

    [Fact]
    public void CreateFile_AfterStart_CallsOnChange()
    {
        using var watcher = CreateWatcher();
        watcher.Start();

        File.WriteAllText(_path, "line\r\n");

        Assert.True(_changed.Wait(NotifyTimeout));
    }

    [Fact]
    public void OtherFileInSameDirectory_DoesNotCallOnChange()
    {
        var other = Path.Combine(_dir, "Other.log");
        File.WriteAllText(other, "");
        using var watcher = CreateWatcher();
        watcher.Start();

        Append(other, "line\r\n");

        Assert.False(_changed.Wait(QuietPeriod));
    }

    [Fact]
    public void Start_DirectoryMissing_LogsWarnOnceAndRetries()
    {
        var missing = Path.Combine(_dir, "missing", "ActionLog.log");
        using var watcher = CreateWatcher(missing, ShortRetry);

        watcher.Start();
        Thread.Sleep(QuietPeriod);

        Assert.False(watcher.IsWatching);
        var entry = Assert.Single(_logger.Snapshot());
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.StartsWith(StartFailedPrefix, entry.Message);
    }

    [Fact]
    public void Retry_DirectoryCreatedLater_StartsAndLogsInfo()
    {
        var laterDir = Path.Combine(_dir, "later");
        var laterPath = Path.Combine(laterDir, "ActionLog.log");
        using var watcher = CreateWatcher(laterPath, ShortRetry);
        watcher.Start();
        Assert.False(watcher.IsWatching);

        Directory.CreateDirectory(laterDir);

        Assert.True(WaitUntil(() => watcher.IsWatching, NotifyTimeout));
        var entries = _logger.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal(LogLevel.Warn, entries[0].Level);
        Assert.StartsWith(StartFailedPrefix, entries[0].Message);
        Assert.Equal((LogLevel.Info, StartedMessage), entries[1]);

        File.WriteAllText(laterPath, "line\r\n");

        Assert.True(_changed.Wait(NotifyTimeout));
    }

    [Fact]
    public void OnChangeThrows_DoesNotPropagate()
    {
        File.WriteAllText(_path, "");
        var calls = 0;
        using var called = new ManualResetEventSlim(false);
        using var watcher = new LogChangeWatcher(_path, () =>
        {
            Interlocked.Increment(ref calls);
            called.Set();
            throw new InvalidOperationException("onChange の失敗");
        }, _logger);
        watcher.Start();

        Append(_path, "line1\r\n");
        Assert.True(called.Wait(NotifyTimeout));

        // 1 回目の追記の通知が遅れて重なって届き終わるのを待ってから、2 回目を見る
        Thread.Sleep(QuietPeriod);
        called.Reset();
        Append(_path, "line2\r\n");

        Assert.True(called.Wait(NotifyTimeout));
        Assert.True(watcher.IsWatching);
    }

    [Fact]
    public void Dispose_StopsCallbacks()
    {
        File.WriteAllText(_path, "");
        var watcher = CreateWatcher();
        watcher.Start();

        watcher.Dispose();
        Append(_path, "line\r\n");

        Assert.False(_changed.Wait(QuietPeriod));
        Assert.False(watcher.IsWatching);
        watcher.Dispose();
    }

    [Fact]
    public void Dispose_WhileRetrying_StopsRetries()
    {
        var laterDir = Path.Combine(_dir, "later");
        using var watcher = CreateWatcher(Path.Combine(laterDir, "ActionLog.log"), ShortRetry);
        watcher.Start();

        watcher.Dispose();
        Directory.CreateDirectory(laterDir);
        Thread.Sleep(QuietPeriod);

        Assert.False(watcher.IsWatching);
        var entry = Assert.Single(_logger.Snapshot());
        Assert.Equal(LogLevel.Warn, entry.Level);
    }

    [Fact]
    public void OnError_LogsWarnRecreatesAndWakes()
    {
        File.WriteAllText(_path, "");
        using var watcher = CreateWatcher();
        watcher.Start();

        watcher.OnError(new InternalBufferOverflowException("あふれ"));

        Assert.Equal(
            [(LogLevel.Info, StartedMessage), (LogLevel.Warn, ErrorPrefix + "あふれ"), (LogLevel.Info, StartedMessage)],
            _logger.Snapshot());
        Assert.Equal(1, Volatile.Read(ref _changeCount));
        Assert.True(watcher.IsWatching);

        // 作り直した見張りでも起こされる
        _changed.Reset();
        Append(_path, "line\r\n");

        Assert.True(_changed.Wait(NotifyTimeout));
    }

    // 見張りはタイマーや通知のスレッドからも書くので、ロックして記録する
    private sealed class LockedLogger : IAppLogger
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public void Write(LogLevel level, string message)
        {
            lock (_gate)
            {
                _entries.Add((level, message));
            }
        }

        public List<(LogLevel Level, string Message)> Snapshot()
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }
}
