using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// 監視を専用スレッドで回す。アパートメントは既定（MTA）のままにして、
/// 段階2のコンソール版と同じ条件で Core Audio の COM を呼ぶ。
/// </summary>
internal sealed class MonitorRunner : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExtendedStopTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan WatcherStopTimeout = TimeSpan.FromSeconds(2);

    private readonly AutoSwitchEngine _engine;
    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly WakeableWaiter _waiter = new();
    private readonly LogChangeWatcher _watcher;
    private readonly Thread _thread;

    public MonitorRunner(AutoSwitchEngine engine, AppSettings settings, IAppLogger logger)
    {
        _engine = engine;
        _settings = settings;
        _logger = logger;
        // ActionLog.log が変わったら監視ループの待機を起こし、1 秒待たずに読ませる
        _watcher = new LogChangeWatcher(settings.ActionLogPath, _waiter.Wake, logger);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "AutoSwitchMonitor",
        };
    }

    /// <summary>監視スレッドの終了と、変更通知の見張りの破棄を確認できたか。Dispose のあとに読む</summary>
    public bool StoppedCleanly { get; private set; }

    public void Start() => _thread.Start();

    private void Run()
    {
        // 起動時同期より前に始め、その間の変更も起こした印として残す（次の待機がすぐ終わる）。
        // 始められなくても例外は出ず、時間だけで読み続ける
        _watcher.Start();

        // 起動時同期の失敗で常駐そのものを止めない（トレイ版では「トレイが出ない」形でしか表に出ないため）
        // 取り消し済みなら切り替えは行われない（StartupSync が自分で判定する）
        try
        {
            _engine.StartupSync(_cancellation.Token);
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Error, $"起動時同期で予期しない例外が発生しました: {ex.Message}");
        }

        try
        {
            MonitorLoop.Run(_engine, _settings, _logger, _waiter, _cancellation.Token);
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Error, $"監視ループが異常終了しました: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // これ以後は待機を起こさない。破棄済みの待機を起こしても例外は出ないが、先に止めておく
        var watcherStopped = StopWatcher();

        if (!_thread.IsAlive || _thread.Join(StopTimeout))
        {
            StoppedCleanly = watcherStopped;
            _cancellation.Dispose();
            _waiter.Dispose();
            return;
        }

        // 取り消しが効くのは待機だけで、Tick や音声操作の実行中は止められない。
        // ここに来るのは実行中の呼び出しが返っていない場合だけなので、もう一度だけ猶予を取る。
        // 音声操作は COM の呼び出しが返れば AudioSwitcher の using が働く
        BoundedLog.Write(_logger, LogLevel.Warn, "監視スレッドが5秒で終了しませんでした。実行中の処理が返るのを待ちます。");

        if (_thread.Join(ExtendedStopTimeout))
        {
            StoppedCleanly = watcherStopped;
            _cancellation.Dispose();
            _waiter.Dispose();
            return;
        }

        // spec §4 の「監視を停止してリソースを解放する」をここでは確認できない。
        // 終了操作を無制限にハングさせないため待たずに進むが、成功としては報告しない
        // （StoppedCleanly を false のままにし、Program が終了コード1で終わる）。
        // まだ token と待機を触っている可能性があるので CancellationTokenSource と待機は破棄しない
        BoundedLog.Write(_logger, LogLevel.Error, "監視スレッドが30秒以内に終了しませんでした。監視の停止と音声操作の解放を確認できません。");
    }

    // 見張りの破棄は、見張りの開始やエラー処理がログの書き込みで止まっていると、その間ずっと待つ。
    // BoundedLog と同じく別スレッドへ出して待つ時間だけを区切り、上の 5 秒・25 秒の経路を止めない。
    // 待ち切れなければ破棄を確認できなかったとして、成功としては報告しない（StoppedCleanly を false にする）。
    // 破棄が遅れて通知が届いても、破棄済みの待機を起こして例外は出ない
    private bool StopWatcher()
    {
        bool stopped;
        try
        {
            stopped = Task.Run(_watcher.Dispose).Wait(WatcherStopTimeout);
        }
        catch (Exception)
        {
            // LogChangeWatcher は例外を外へ出さないのでここへは来ない想定。来たとしても、終了を止めない
            stopped = false;
        }

        if (!stopped)
        {
            BoundedLog.Write(_logger, LogLevel.Warn, "ActionLog.log の変更通知の見張りを2秒で止められませんでした。待たずに終了処理を進めます。");
        }

        return stopped;
    }
}
