using System.Windows.Forms;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// 段階3のトレイ常駐。コンソールは出さない。単一起動を先に判定してから設定とログを初期化する。
/// 終了コード: 0 = メニューからの終了 / 二重起動による自己終了、1 = 致命的なエラー、または監視の停止を確認できなかった終了
/// </summary>
internal static class Program
{
    // 新版専用。旧版 PowerShell 版との同時起動は防げない（spec §6 のとおり手順で担保する）
    private const string SingleInstanceMutexName = @"Local\AutoSwitchForInzone.SingleInstance";

    // 致命的エラーのハンドラから使う。ロガーができる前は null
    private static IAppLogger? _logger;
    private static int _exitCode;

    [STAThread]
    private static int Main()
    {
        Mutex? singleInstance = null;
        try
        {
            // ロガーより前に判定する。2つ目のプロセスが同じログファイルへ追記しないようにするため
            singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                return 0;
            }

            ApplicationConfiguration.Initialize();

            // UI スレッドの例外を既定の未処理例外ダイアログへ流さず、自分の経路で扱う。
            // ウィンドウを作る前に呼ぶ必要がある
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            var created = false;
            Exception? createError = null;
            try
            {
                created = SettingsLoader.CreateDefaultFileIfMissing(AppPaths.ConfigPath);
            }
            catch (Exception ex)
            {
                createError = ex;
            }

            var loadResult = SettingsLoader.Load(AppPaths.ConfigPath);
            var settings = loadResult.Settings;
            var logger = new FileLogger(AppPaths.LogPath, settings.LogLevel, settings.MaxLogSizeKB);
            _logger = logger;

            if (createError is not null)
            {
                logger.Write(LogLevel.Warn, $"既定値の設定ファイルを作成できません: {AppPaths.ConfigPath}: {createError.Message}");
            }
            if (created)
            {
                logger.Write(LogLevel.Info, $"既定値の設定ファイルを作成しました: {AppPaths.ConfigPath}");
            }
            foreach (var warning in loadResult.Warnings)
            {
                logger.Write(LogLevel.Warn, warning);
            }

            logger.Write(LogLevel.Info, $"INZONE Auto Switch を開始します。appRoot={AppPaths.AppRoot}");
            logger.Write(LogLevel.Info, $"監視対象: {settings.ActionLogPath}");

            var engine = new AutoSwitchEngine(
                settings,
                logger,
                new PlaybackSwitcher(() => new CoreAudioPlaybackSession()),
                new SystemClock(),
                new SystemHubProcessProbe());

            // exe のフルパス。apphost 経由の起動では ProcessPath がその exe を指す
            var autoStart = new AutoStartRegistration(Environment.ProcessPath ?? Application.ExecutablePath);
            LogAutoStartState(autoStart, logger);

            // 起動時同期は MonitorRunner のスレッドで行う（常駐の可否をログ1行の内容に委ねない）
            // 終了の行は Dispose のあとに書く。監視スレッドの停止より先に書くと、ログの最終行が前後する
            // アイコンの所有権が移るのは構築が成功したときだけ。失敗したらここで捨てる
            var icons = new TrayIcons();
            TrayApplicationContext context;
            try
            {
                context = new TrayApplicationContext(engine, settings, logger, icons, autoStart);
            }
            catch
            {
                icons.Dispose();
                throw;
            }

            try
            {
                Application.Run(context);
            }
            finally
            {
                context.Dispose();
            }

            // 監視の停止を確認できなかった終了を、成功として報告しない
            if (!context.MonitorStoppedCleanly)
            {
                BoundedLog.Write(logger, LogLevel.Error, "終了処理で監視の停止を確認できませんでした。");
                _exitCode = 1;
            }

            BoundedLog.Write(logger, LogLevel.Info, "INZONE Auto Switch を終了します。");
            return _exitCode;
        }
        catch (Exception ex)
        {
            Fatal("起動処理で予期しない例外が発生しました", ex);
            return 1;
        }
        finally
        {
            Application.ThreadException -= OnThreadException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            singleInstance?.Dispose();
        }
    }

    // ログオン起動の確認で、そのとき Run キーがどうなっていたかを後から読み取れるようにする
    private static void LogAutoStartState(AutoStartRegistration autoStart, IAppLogger logger)
    {
        try
        {
            var state = autoStart.IsEnabled() ? "有効" : "無効";
            logger.Write(LogLevel.Info, $@"自動起動: {state}（HKCU\{autoStart.KeyPath}）");
        }
        catch (Exception ex)
        {
            logger.Write(LogLevel.Warn, $"自動起動の登録状態を読み取れません: {ex.Message}");
        }
    }

    // UI スレッドで捕まえ損ねた例外。後片付けを通すため Environment.Exit は使わない
    private static void OnThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        Fatal("UI スレッドで予期しない例外が発生しました", e.Exception);
        _exitCode = 1;
        Application.Exit();
    }

    // ここへ来た時点でランタイムはプロセスを終わらせる。残せる範囲で残すだけ
    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Fatal("予期しない例外が発生しました", e.ExceptionObject as Exception);
    }

    private static void Fatal(string headline, Exception? ex)
    {
        var detail = ex?.Message ?? "詳細不明";
        if (_logger is not null)
        {
            BoundedLog.Write(_logger, LogLevel.Error, $"{headline}: {detail}");
        }
        MessageBox.Show(
            $"{headline}。{Environment.NewLine}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}ログ: {AppPaths.LogPath}",
            "INZONE Auto Switch",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
