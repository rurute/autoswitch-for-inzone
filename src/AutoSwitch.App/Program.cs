using System.Text;
using AutoSwitch.App;
using AutoSwitch.Core;

// 段階2のコンソール常駐。設定を読み込み、起動時同期のあと監視を続ける。Ctrl+C で終了する。
// 起動の順序とログの文言は旧版 INZONE-AutoSwitch.ps1 に合わせる。単一起動は段階3で扱う。
// 終了コード: 0 = Ctrl+C による正常終了、1 = 起動処理（ループ開始まで）の想定外の例外

IAppLogger? logger = null;
CancellationTokenSource? cancellation = null;
ConsoleCancelEventHandler? cancelHandler = null;

try
{
    Console.OutputEncoding = Encoding.UTF8;

    // 1. 設定とログのパスを表示する
    Console.WriteLine($"設定ファイル: {AppPaths.ConfigPath}");
    Console.WriteLine($"ログファイル: {AppPaths.LogPath}");
    Console.WriteLine("Ctrl+C で終了します。");

    // 2. 既定値の設定ファイルを作る。失敗はログを初期化してから残す
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

    // 3. 設定を読み込み、ログを初期化する
    var loadResult = SettingsLoader.Load(AppPaths.ConfigPath);
    var settings = loadResult.Settings;
    logger = new FileLogger(AppPaths.LogPath, settings.LogLevel, settings.MaxLogSizeKB);

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

    // 4. 開始と監視対象を残す
    logger.Write(LogLevel.Info, $"INZONE Auto Switch を開始します。appRoot={AppPaths.AppRoot}");
    logger.Write(LogLevel.Info, $"監視対象: {settings.ActionLogPath}");

    // 5. エンジンを組み立てて起動時同期を行う
    var engine = new AutoSwitchEngine(
        settings,
        logger,
        new PlaybackSwitcher(() => new CoreAudioPlaybackSession()),
        new SystemClock(),
        new SystemHubProcessProbe());
    engine.StartupSync();

    // 6. Ctrl+C では既定の強制終了を止め、監視ループを抜けさせる
    cancellation = new CancellationTokenSource();
    var token = cancellation.Token;
    var source = cancellation;
    cancelHandler = (_, e) =>
    {
        e.Cancel = true;
        source.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;

    MonitorLoop.Run(engine, settings, logger, new SystemWaiter(), token);

    // 7. ループを抜けたら終了を残す
    logger.Write(LogLevel.Info, "INZONE Auto Switch を終了します。");
    return 0;
}
catch (Exception ex)
{
    logger?.Write(LogLevel.Error, $"起動処理で予期しない例外が発生しました: {ex.Message}");
    Console.Error.WriteLine($"起動処理で予期しない例外が発生しました: {ex}");
    return 1;
}
finally
{
    if (cancelHandler is not null)
    {
        Console.CancelKeyPress -= cancelHandler;
    }
    cancellation?.Dispose();
}
