namespace AutoSwitch.Core;

/// <summary>
/// ActionLog.log の変更を見張り、変更のたびに onChange を呼ぶ（監視ループの待機を起こすために使う）。
/// 見張りを始められない間とエラーのあとは、retryInterval ごとに作り直す。その間も監視ループは時間だけで読み続ける。
/// どの経路からも例外を外へ出さない。通知 1 回ごとのログは出さない（短い間に何回も来るため）
/// </summary>
public sealed class LogChangeWatcher : IDisposable
{
    /// <summary>見張りを始められないときに、やり直すまでの間隔</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(5);

    // Task 1 の計測で、Hub の書き込みのたびに通知が届いた組み合わせ
    private const NotifyFilters Filters = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime;

    private readonly string _path;
    private readonly Action _onChange;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _retryInterval;

    // 開始・作り直し・破棄はこの lock の中で行う
    private readonly object _gate = new();

    // onChange の呼び出しと破棄の印を付けるのはこの lock の中で行う。
    // Dispose が戻ったあとに onChange が呼ばれないようにするため。_gate とは入れ子にしない
    private readonly object _callbackGate = new();

    private volatile bool _disposed;
    private FileSystemWatcher? _watcher;
    private Timer? _retryTimer;

    // 失敗が続く間で、始められない旨の Warn を出したか
    private bool _startFailureLogged;

    public LogChangeWatcher(string path, Action onChange, IAppLogger logger)
        : this(path, onChange, logger, DefaultRetryInterval)
    {
    }

    public LogChangeWatcher(string path, Action onChange, IAppLogger logger, TimeSpan retryInterval)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(onChange);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryInterval, TimeSpan.Zero);
        _path = path;
        _onChange = onChange;
        _logger = logger;
        _retryInterval = retryInterval;
    }

    /// <summary>見張っているか（始められず、やり直しを待っている間は false）</summary>
    public bool IsWatching
    {
        get
        {
            lock (_gate)
            {
                return _watcher is not null;
            }
        }
    }

    /// <summary>見張りを始める。始められなければ Warn を 1 回出し、retryInterval ごとにやり直す</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is not null || _retryTimer is not null)
            {
                return;
            }

            TryStartLocked();
        }
    }

    public void Dispose()
    {
        lock (_callbackGate)
        {
            _disposed = true;
        }

        lock (_gate)
        {
            DisposeWatcherLocked();
            StopRetryLocked();
        }
    }

    // FileSystemWatcher の Error の処理本体。テストから呼べるように切り出してある
    internal void OnError(Exception error)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                Log(LogLevel.Warn, $"ActionLog.log の変更通知でエラーが発生しました。見張りを作り直します: {error.Message}");
                DisposeWatcherLocked();
                TryStartLocked();
            }
        }
        catch (Exception)
        {
            // 通知のスレッドへ例外を出さない（未処理の例外はプロセスを落とす）
        }

        // 通知があふれるなどして取りこぼしたかもしれないので、一度読ませる
        NotifyChange();
    }

    private void TryStartLocked()
    {
        FileSystemWatcher? watcher = null;
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException($"フォルダを決められないパスです: {_path}");
            }

            watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
            {
                IncludeSubdirectories = false,
                NotifyFilter = Filters,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            DisposeQuietly(watcher);
            if (!_startFailureLogged)
            {
                _startFailureLogged = true;
                Log(LogLevel.Warn, $"ActionLog.log の変更通知を受け取れません。1 秒ごとの読み取りだけで監視します: {ex.Message}");
            }
            StartRetryLocked();
            return;
        }

        _watcher = watcher;
        _startFailureLogged = false;
        StopRetryLocked();
        Log(LogLevel.Info, "ActionLog.log の変更通知を受け取ります。");
    }

    private void StartRetryLocked()
    {
        if (_retryTimer is not null)
        {
            return;
        }

        try
        {
            _retryTimer = new Timer(_ => OnRetry(), null, _retryInterval, _retryInterval);
        }
        catch (Exception)
        {
            // タイマーを作れなければやり直さない。監視ループは時間だけで読み続ける
        }
    }

    private void StopRetryLocked()
    {
        DisposeQuietly(_retryTimer);
        _retryTimer = null;
    }

    private void OnRetry()
    {
        try
        {
            lock (_gate)
            {
                // 破棄のあとや、ほかの経路で始められたあとに遅れて届いたものは何もしない
                if (_disposed || _watcher is not null)
                {
                    return;
                }

                TryStartLocked();
            }
        }
        catch (Exception)
        {
            // タイマーのスレッドへ例外を出さない
        }
    }

    private void DisposeWatcherLocked()
    {
        if (_watcher is null)
        {
            return;
        }

        // 破棄の前に外し、破棄済みの見張りから遅れて届く通知・エラーを受けないようにする
        _watcher.Changed -= OnFileEvent;
        _watcher.Created -= OnFileEvent;
        _watcher.Deleted -= OnFileEvent;
        _watcher.Renamed -= OnFileEvent;
        _watcher.Error -= OnWatcherError;
        DisposeQuietly(_watcher);
        _watcher = null;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => NotifyChange();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Exception error;
        try
        {
            error = e.GetException();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        OnError(error);
    }

    private void NotifyChange()
    {
        lock (_callbackGate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _onChange();
            }
            catch (Exception)
            {
                // 起こす側の失敗で見張りを止めない
            }
        }
    }

    private void Log(LogLevel level, string message)
    {
        try
        {
            _logger.Write(level, message);
        }
        catch (Exception)
        {
            // ログの失敗で見張りを止めない
        }
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
