using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Issue rurute/inzone-auto-switch#21 の前提を実機で測る使い捨てのコンソール（spec 2026-10-18-read-on-file-change-design.md §7「測り方」）。製品には含めない。
// 通知・1 秒ごと・10 ms ごとの 3 つを ms 付きで CSV に記録し、Enter（または標準入力の終わり・Ctrl+C）で止めて要約を出す。
// 終了コード: 0 = 計測して要約を出した、1 = 計測を始められなかった、2 = 引数不正

Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 2 || args is ["-h"] or ["--help"])
{
    Console.Error.WriteLine(
        """
        Usage:
          LogTimingProbe [ActionLog.log のパス] [CSV の出力先]
            既定: %APPDATA%\Sony\INZONE Hub\ActionLog.log と、カレントの probe-<yyyyMMdd-HHmmss>.csv
            Enter・標準入力の終わり・Ctrl+C のどれかで止まり、要約を標準出力に出す
        """);
    return 2;
}

var logPath = Path.GetFullPath(args.Length >= 1
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sony", "INZONE Hub", "ActionLog.log"));
var csvPath = Path.GetFullPath(args.Length >= 2
    ? args[1]
    : Path.Combine(Environment.CurrentDirectory, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.csv"));

var directory = Path.GetDirectoryName(logPath);
if (directory is null || !Directory.Exists(directory))
{
    Console.Error.WriteLine($"ログのフォルダがありません: {directory}");
    return 1;
}

Probe probe;
try
{
    probe = new Probe(logPath, csvPath);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"CSV を作れません: {ex.Message}");
    return 1;
}

using (probe)
{
    try
    {
        probe.Start();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"計測を始められません: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"ログ: {logPath}");
    Console.WriteLine($"記録先: {csvPath}");
    Console.WriteLine("準備ができました。Buds を操作してください。終わったら Enter で止めます。");

    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Set();
    };
    var stdinThread = new Thread(() =>
    {
        try
        {
            // Enter でも、標準入力が閉じたとき（null）でも止める
            Console.ReadLine();
        }
        catch (IOException)
        {
        }

        stop.Set();
    })
    { IsBackground = true };
    stdinThread.Start();
    stop.Wait();

    probe.Stop();
    probe.PrintSummary(Console.Out);
}

return 0;

internal sealed record LengthObservation(long TimeMs, string Source, long Length);

// ReadIndex は、その行を読んだ読み取りの長さの観測が _observations の何番目か（ms の時刻が同じでも前後を区別するため）
internal sealed record FirstRead(string LogId, long ActionTypeId, long TimeStamp, string Source, long ReadMs, int ReadIndex);

internal sealed record Truncation(long AMs, long BMs, int AIndex, int BIndex, long LengthBefore, long LengthAfter, string SourceA, string SourceB);

internal sealed class Probe : IDisposable
{
    private const long ConnectDevice = 23011;
    private const long DisconnectDevice = 23012;
    private const int SampleIntervalMs = 10;
    private const int PollIntervalMs = 1000;

    private readonly string _logPath;
    private readonly string _csvPath;
    private readonly StreamWriter _csv;

    // 読み取り・長さの記録・CSV への書き出しは、すべてこの 1 つの lock の中で行う。
    // 記録の順序が実際に測った順序と一致するので、縮む前と縮んだあとの観測を取り違えない
    private readonly Lock _gate = new();

    private readonly HashSet<string> _baselineLogIds = new(StringComparer.Ordinal);
    private readonly List<LengthObservation> _observations = [];
    private readonly Dictionary<string, FirstRead> _firstReads = new(StringComparer.Ordinal);
    private readonly List<FirstRead> _firstReadOrder = [];
    private readonly Dictionary<WatcherChangeTypes, int> _notifyCounts = [];
    private readonly List<string> _errors = [];
    private readonly List<long> _sampleTimes = [];

    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private Thread? _sampler;
    private volatile bool _running;

    // lock の中で見る停止の印。lock の外で _running を見たあとに止まっても、CSV を閉じたあとに書かない
    private bool _stopped;
    private bool _timerPeriodRaised;

    private long _startMs;
    private long _offset;
    private long _lastLength;
    private int _errorCount;

    public Probe(string logPath, string csvPath)
    {
        _logPath = logPath;
        _csvPath = csvPath;
        _csv = new StreamWriter(csvPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _csv.WriteLine("kind,time_ms,source,length,log_id,action_type_id,time_stamp");
    }

    private static long NowMs() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public void Start()
    {
        lock (_gate)
        {
            // start_ms は基準線を読む前に取る。基準線を読む間に書かれた行は、基準線の logId として除く
            _startMs = NowMs();
            var baselineLength = 0L;
            if (File.Exists(_logPath))
            {
                using var stream = OpenLog();
                baselineLength = stream.Length;
                var (lines, consumed) = ReadCompleteLines(stream, 0, baselineLength);
                foreach (var line in lines)
                {
                    var (logId, _, _) = ParseLine(line);
                    if (logId is not null)
                    {
                        _baselineLogIds.Add(logId);
                    }
                }

                _offset = consumed;
            }

            _lastLength = baselineLength;
            _observations.Add(new LengthObservation(_startMs, "baseline", baselineLength));
            WriteRow("start", _startMs, "baseline", baselineLength, null, null, null);
        }

        _running = true;

        try
        {
            _timerPeriodRaised = NativeMethods.timeBeginPeriod(1) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _timerPeriodRaised = false;
        }

        _sampler = new Thread(SampleLoop) { IsBackground = true, Name = "len10", Priority = ThreadPriority.AboveNormal };
        _sampler.Start();

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_logPath)!, Path.GetFileName(_logPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnNotify;
        _watcher.Created += OnNotify;
        _watcher.Deleted += OnNotify;
        _watcher.Renamed += OnNotify;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;

        _pollTimer = new Timer(_ => OnPoll(), null, PollIntervalMs, PollIntervalMs);
    }

    public void Stop()
    {
        _running = false;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
        }

        if (_pollTimer is not null)
        {
            using var done = new ManualResetEvent(false);
            if (_pollTimer.Dispose(done))
            {
                done.WaitOne();
            }

            _pollTimer = null;
        }

        _sampler?.Join();
        if (_timerPeriodRaised)
        {
            NativeMethods.timeEndPeriod(1);
        }

        lock (_gate)
        {
            _stopped = true;
            _csv.Flush();
        }
    }

    public void Dispose()
    {
        _running = false;
        _watcher?.Dispose();
        _pollTimer?.Dispose();
        lock (_gate)
        {
            _stopped = true;
            _csv.Dispose();
        }
    }

    private void OnNotify(object sender, FileSystemEventArgs e)
    {
        if (!_running)
        {
            return;
        }

        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _notifyCounts[e.ChangeType] = _notifyCounts.GetValueOrDefault(e.ChangeType) + 1;
            ReadLocked("notify");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        lock (_gate)
        {
            RecordErrorLocked("watcher", e.GetException().Message);
        }

        // あふれなどで止まった見張りを動かし直す。計測ツールなので作り直しまではしない
        try
        {
            if (_running && _watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or ObjectDisposedException or FileNotFoundException)
        {
            lock (_gate)
            {
                RecordErrorLocked("watcher", $"見張りを動かし直せません: {ex.Message}");
            }
        }
    }

    private void OnPoll()
    {
        if (!_running)
        {
            return;
        }

        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            ReadLocked("poll");
            _csv.Flush();
        }
    }

    private void SampleLoop()
    {
        var clock = Stopwatch.StartNew();
        var next = clock.ElapsedMilliseconds;
        while (_running)
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                SampleLocked();
            }

            next += SampleIntervalMs;
            var now = clock.ElapsedMilliseconds;
            if (next < now)
            {
                // 遅れた分を詰めて取り返さない。間隔の実測は要約に出す
                next = now;
            }

            while (_running && (now = clock.ElapsedMilliseconds) < next)
            {
                if (next - now >= 2)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.Yield();
                }
            }
        }
    }

    private void SampleLocked()
    {
        try
        {
            long length;
            if (!File.Exists(_logPath))
            {
                length = 0;
            }
            else
            {
                // FileInfo.Length は使わない。開いたまま書かれているファイルでは、ディレクトリの情報が遅れるため
                using var stream = OpenLog();
                length = stream.Length;
            }

            var time = NowMs();
            _sampleTimes.Add(time);
            _observations.Add(new LengthObservation(time, "len10", length));
            WriteRow("length", time, "len10", length, null, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordErrorLocked("len10", ex.Message);
        }
    }

    private void ReadLocked(string source)
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                var missingTime = NowMs();
                RecordLengthLocked(missingTime, source, 0);
                if (_lastLength > 0 || _offset > 0)
                {
                    WriteRow("truncate", missingTime, source, 0, null, null, null);
                }

                _offset = 0;
                _lastLength = 0;
                return;
            }

            using var stream = OpenLog();
            var length = stream.Length;

            // 読んだ時刻は長さを測った直後に取る。これから読む行は、この時点でファイルにあった
            var time = NowMs();
            var readIndex = RecordLengthLocked(time, source, length);

            if (length < _lastLength || length < _offset)
            {
                WriteRow("truncate", time, source, length, null, null, null);
                _offset = 0;
            }

            _lastLength = length;
            if (length <= _offset)
            {
                return;
            }

            var (lines, consumed) = ReadCompleteLines(stream, _offset, length);
            _offset += consumed;
            foreach (var line in lines)
            {
                var (logId, actionTypeId, timeStamp) = ParseLine(line);
                WriteRow("line", time, source, length, logId, actionTypeId, timeStamp);
                if (logId is null || _baselineLogIds.Contains(logId) || _firstReads.ContainsKey(logId))
                {
                    continue;
                }

                // 計測前からある行は数えない（truncate のあと先頭から読み直したときに残っていた行を含む）
                if (timeStamp is null || timeStamp.Value < _startMs)
                {
                    continue;
                }

                var first = new FirstRead(logId, actionTypeId ?? 0, timeStamp.Value, source, time, readIndex);
                _firstReads.Add(logId, first);
                _firstReadOrder.Add(first);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordErrorLocked(source, ex.Message);
        }
    }

    private int RecordLengthLocked(long time, string source, long length)
    {
        _observations.Add(new LengthObservation(time, source, length));
        WriteRow("length", time, source, length, null, null, null);
        return _observations.Count - 1;
    }

    private void RecordErrorLocked(string source, string message)
    {
        _errorCount++;
        if (_errors.Count < 10)
        {
            _errors.Add($"{FormatTime(NowMs())} [{source}] {message}");
        }

        Console.Error.WriteLine($"エラー [{source}]: {message}");
    }

    private FileStream OpenLog() =>
        new(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>offset から length までを読み、改行で終わる行だけを返す。consumed は最後の改行までのバイト数。</summary>
    private static (List<string> Lines, long Consumed) ReadCompleteLines(FileStream stream, long offset, long length)
    {
        var lines = new List<string>();
        if (length <= offset)
        {
            return (lines, 0);
        }

        var buffer = new byte[length - offset];
        stream.Seek(offset, SeekOrigin.Begin);
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var lastLf = read > 0 ? Array.LastIndexOf(buffer, (byte)'\n', read - 1) : -1;
        if (lastLf < 0)
        {
            return (lines, 0);
        }

        foreach (var raw in Encoding.UTF8.GetString(buffer, 0, lastLf + 1).Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart('﻿');
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return (lines, lastLf + 1);
    }

    private static (string? LogId, long? ActionTypeId, long? TimeStamp) ParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            string? logId = null;
            if (root.TryGetProperty("logId", out var id))
            {
                logId = id.ValueKind switch
                {
                    JsonValueKind.String => id.GetString(),
                    JsonValueKind.Number => id.GetRawText(),
                    _ => null,
                };
            }

            long? actionTypeId = root.TryGetProperty("actionTypeId", out var type) && type.ValueKind == JsonValueKind.Number && type.TryGetInt64(out var t)
                ? t
                : null;

            long? timeStamp = null;
            if (root.TryGetProperty("timeStamp", out var ts))
            {
                if (ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var n))
                {
                    timeStamp = n;
                }
                else if (ts.ValueKind == JsonValueKind.String && long.TryParse(ts.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                {
                    timeStamp = s;
                }
            }

            return (string.IsNullOrWhiteSpace(logId) ? null : logId, actionTypeId, timeStamp);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private void WriteRow(string kind, long time, string source, long length, string? logId, long? actionTypeId, long? timeStamp)
    {
        _csv.Write(kind);
        _csv.Write(',');
        _csv.Write(time.ToString(CultureInfo.InvariantCulture));
        _csv.Write(',');
        _csv.Write(source);
        _csv.Write(',');
        _csv.Write(length.ToString(CultureInfo.InvariantCulture));
        _csv.Write(',');
        _csv.Write(CsvField(logId));
        _csv.Write(',');
        _csv.Write(actionTypeId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        _csv.Write(',');
        _csv.WriteLine(timeStamp?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string FormatTime(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Kind(long actionTypeId) => actionTypeId switch
    {
        ConnectDevice => "接続",
        DisconnectDevice => "切断",
        _ => actionTypeId.ToString(CultureInfo.InvariantCulture),
    };

    public void PrintSummary(TextWriter output)
    {
        lock (_gate)
        {
            var stopMs = NowMs();
            output.WriteLine();
            output.WriteLine("===== 要約 =====");
            output.WriteLine($"start_ms: {_startMs}（{FormatTime(_startMs)}）  終了: {FormatTime(stopMs)}  計測時間: {(stopMs - _startMs) / 1000.0:F1} 秒");
            output.WriteLine($"記録先: {_csvPath}");
            output.WriteLine($"基準線より前の行（数えない）: {_baselineLogIds.Count} 行");

            var events = _firstReadOrder.Where(r => r.ActionTypeId is ConnectDevice or DisconnectDevice).ToList();

            output.WriteLine();
            output.WriteLine("--- 接続・切断の行（最初に読んだ出所と時刻、遅れの上限 = 最初に読んだ時刻 − timeStamp）---");
            if (events.Count == 0)
            {
                output.WriteLine("  なし");
            }

            foreach (var e in events)
            {
                output.WriteLine($"  [{Kind(e.ActionTypeId)}] logId={e.LogId} timeStamp={e.TimeStamp}（{FormatTime(e.TimeStamp)}） 出所={e.Source} 読んだ時刻={e.ReadMs}（{FormatTime(e.ReadMs)}） 遅れの上限={e.ReadMs - e.TimeStamp} ms");
            }

            // 縮んだ観測を truncate とみなす。A = 縮む前の長さを最後に見た時刻、B = 縮んだ長さを最初に見た時刻
            var truncations = new List<Truncation>();
            for (var i = 1; i < _observations.Count; i++)
            {
                var before = _observations[i - 1];
                var after = _observations[i];
                if (after.Length < before.Length)
                {
                    truncations.Add(new Truncation(before.TimeMs, after.TimeMs, i - 1, i, before.Length, after.Length, before.Source, after.Source));
                }
            }

            output.WriteLine();
            output.WriteLine("--- truncate（下限 = A − 行を最初に読んだ時刻、上限 = B − 行の timeStamp）---");
            if (truncations.Count == 0)
            {
                output.WriteLine("  なし");
            }

            long? minLower = null;
            long? minUpper = null;
            var previousBIndex = 0;
            foreach (var t in truncations)
            {
                output.WriteLine($"  A={t.AMs}（{FormatTime(t.AMs)}, {t.SourceA}, {t.LengthBefore} バイト） B={t.BMs}（{FormatTime(t.BMs)}, {t.SourceB}, {t.LengthAfter} バイト） B−A={t.BMs - t.AMs} ms");

                // 前の truncate の B の読み取りから、この truncate の A までに読んだ接続・切断の行。
                // ms の時刻は同じになりうるので、観測の順番で区切る。B はその直前の観測より短いので、
                // B の読み取りで読んだ行は縮んだあとの内容、つまり前の truncate のあとに書かれた行として、こちらに入れる
                var paired = events.Where(e => e.ReadIndex >= previousBIndex && e.ReadIndex <= t.AIndex).ToList();
                if (paired.Count == 0)
                {
                    output.WriteLine("    この truncate の前に読んだ接続・切断の行なし（書かれなかったか、間に合わなかった。Buds の操作の記録と照らす）");
                }

                foreach (var e in paired)
                {
                    var lower = t.AMs - e.ReadMs;
                    var upper = t.BMs - e.TimeStamp;
                    minLower = minLower is null ? lower : Math.Min(minLower.Value, lower);
                    minUpper = minUpper is null ? upper : Math.Min(minUpper.Value, upper);
                    output.WriteLine($"    [{Kind(e.ActionTypeId)}] logId={e.LogId} 下限={lower} ms 上限={upper} ms");
                }

                previousBIndex = t.BIndex;
            }

            var connects = events.Count(e => e.ActionTypeId == ConnectDevice);
            var disconnects = events.Count(e => e.ActionTypeId == DisconnectDevice);
            var eventMisses = events.Count(e => e.Source != "notify");
            var allMisses = _firstReadOrder.Count(e => e.Source != "notify");

            output.WriteLine();
            output.WriteLine("--- 全体 ---");
            output.WriteLine($"  接続: {connects} 回  切断: {disconnects} 回");
            output.WriteLine($"  通知が届かなかった接続・切断の行（最初に読んだ出所が notify でない）: {eventMisses} 行");
            output.WriteLine($"  通知の遅れの上限の最大（接続・切断の行）: {(events.Count == 0 ? "なし" : $"{events.Max(e => e.ReadMs - e.TimeStamp)} ms")}");
            output.WriteLine($"  参考・すべての新しい行: {_firstReadOrder.Count} 行、通知が届かなかった行 {allMisses} 行、遅れの上限の最大 {(_firstReadOrder.Count == 0 ? "なし" : $"{_firstReadOrder.Max(e => e.ReadMs - e.TimeStamp)} ms")}");
            output.WriteLine($"  truncate: {truncations.Count} 回");
            output.WriteLine($"  下限の最短: {(minLower is null ? "なし" : $"{minLower} ms")}  上限の最短: {(minUpper is null ? "なし" : $"{minUpper} ms")}");

            var notifyText = _notifyCounts.Count == 0
                ? "0 回"
                : string.Join("、", _notifyCounts.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value} 回"));
            output.WriteLine($"  受けた通知: {notifyText}");

            if (_sampleTimes.Count >= 2)
            {
                var gaps = new List<long>(_sampleTimes.Count - 1);
                for (var i = 1; i < _sampleTimes.Count; i++)
                {
                    gaps.Add(_sampleTimes[i] - _sampleTimes[i - 1]);
                }

                gaps.Sort();
                var p99 = gaps[(int)Math.Min(gaps.Count - 1, Math.Ceiling(gaps.Count * 0.99) - 1)];
                output.WriteLine($"  10 ms ごとの長さの記録: {_sampleTimes.Count} 回、間隔の中央値 {gaps[gaps.Count / 2]} ms・99% 点 {p99} ms・最大 {gaps[^1]} ms（timeBeginPeriod(1): {(_timerPeriodRaised ? "有効" : "無効")}）");
            }
            else
            {
                output.WriteLine($"  10 ms ごとの長さの記録: {_sampleTimes.Count} 回");
            }

            output.WriteLine($"  エラー: {_errorCount} 回");
            foreach (var error in _errors)
            {
                output.WriteLine($"    {error}");
            }
        }
    }
}

internal static class NativeMethods
{
    // Windows の既定のタイマー分解能（約 15.6 ms）では 10 ms ごとの記録が粗くなるので、計測中だけ 1 ms に上げる
    [DllImport("winmm.dll")]
    internal static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    internal static extern uint timeEndPeriod(uint period);
}
