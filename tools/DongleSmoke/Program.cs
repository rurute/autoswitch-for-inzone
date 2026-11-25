using System.Diagnostics;
using System.Globalization;
using System.Text;
using AutoSwitch.Core;
using AutoSwitch.Hid;

// 実機で HidDongleTransport と DongleProbe を試す開発用コンソール。製品には含めない。
// ドングルに問い合わせるだけで、音声の切り替えは行わない。
// 2 秒おき（知らせが来たら、すぐ）に問い合わせ、結果を 1 行ずつ表示する。知らせはヘッダーだけを表示し、payload は伏せる。終了時に Dispose にかかった時間と、読んだフレームの総数（返答・知らせ・他のソフトへの返答を含む）を表示する。
// 使い方: DongleSmoke [秒数]（既定 120）。Ctrl+C で早く終われる
// 終了コード: 0 = 最後まで動いた、2 = 引数不正

Console.OutputEncoding = Encoding.UTF8;

var seconds = 120;
if (args.Length > 1 || (args.Length == 1 && (!int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds <= 0)))
{
    Console.Error.WriteLine("Usage: DongleSmoke [seconds]   （既定 120 秒。ドングルに問い合わせて結果を表示する）");
    return 2;
}

var pollInterval = TimeSpan.FromSeconds(2);

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

using var waiter = new WakeableWaiter();
var transport = new HidDongleTransport();
var framesReceived = 0;
transport.FrameReceived += frame =>
{
    Interlocked.Increment(ref framesReceived);
    if (BudsProtocol.IsNotification(frame))
    {
        Print($"notification {BudsProtocol.DescribeNotification(frame)}");
        waiter.Wake();
    }
};
var probe = new DongleProbe(transport);

Print($"start seconds={seconds} poll={pollInterval.TotalSeconds}s");
ProbeStatus? last = null;
try
{
    while (!cancellation.IsCancellationRequested)
    {
        var started = Stopwatch.GetTimestamp();
        var result = probe.Query(cancellation.Token);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var mark = result.Status != last ? $"  <== {result.Status}" : "";
        last = result.Status;
        Print($"query status={result.Status} {FormatLevels(result.Levels)} took={elapsed.TotalMilliseconds:F0}ms"
            + (result.Error is null ? "" : $" error=\"{result.Error}\"") + mark);

        if (!waiter.Wait(pollInterval, cancellation.Token))
        {
            break;
        }
    }
}
catch (OperationCanceledException)
{
    // 時間切れか Ctrl+C で問い合わせの途中に取り消された
}

var disposeStarted = Stopwatch.GetTimestamp();
transport.Dispose();
Print($"dispose took={Stopwatch.GetElapsedTime(disposeStarted).TotalMilliseconds:F0}ms");
Print($"end frames={Volatile.Read(ref framesReceived)}");
return 0;

static void Print(string message) =>
    Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message}");

static string FormatLevels(BudsLevels? levels) =>
    levels is null ? "L=? R=? C=?" : $"L={Level(levels.Left)} R={Level(levels.Right)} C={Level(levels.Case)}";

static string Level(int? value) => value is null ? "--" : value.Value.ToString(CultureInfo.InvariantCulture);
