using System.Globalization;
using System.Text;

namespace AutoSwitch.Core;

public interface IAppLogger
{
    void Write(LogLevel level, string message);
}

/// <summary>
/// 診断ログ。行形式・レベル・同一メッセージの抑制・ローテーション・共有モードは旧版 Logging.psm1 に合わせる。
/// 例外を外へ出さない。
/// </summary>
public sealed class FileLogger : IAppLogger
{
    private const int MaxWriteAttempts = 3;
    private const string NewLine = "\r\n";
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    // ローテーションと書き込みを保持したまま止まったスレッドがあっても、
    // 他のスレッドを無制限に待たせない。段階3では監視スレッドと UI スレッドが共有する
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(2);

    private readonly string _path;
    private readonly LogLevel _minimumLevel;
    private readonly long _maxSizeBytes;
    private readonly object _gate = new();
    private string? _lastMessage;
    private int _suppressedCount;

    public FileLogger(string path, LogLevel minimumLevel, int maxSizeKB)
    {
        _path = path;
        _minimumLevel = minimumLevel;
        _maxSizeBytes = (long)maxSizeKB * 1024L;

        // 旧版と同じく、フォルダは初期化時に1回だけ作る
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch (Exception)
        {
        }
    }

    public void Write(LogLevel level, string message)
    {
        try
        {
            WriteCore(level, message ?? string.Empty);
        }
        catch (Exception)
        {
        }
    }

    private void WriteCore(LogLevel level, string message)
    {
        if (string.IsNullOrEmpty(_path) || level < _minimumLevel)
        {
            return;
        }

        if (!Monitor.TryEnter(_gate, GateTimeout))
        {
            // 先客が止まっている。この行は捨てる。捨てたことを同じロガーへ書くことはできない
            return;
        }

        try
        {
            // 旧版は -eq で比べるため、大文字小文字を区別しない
            if (_lastMessage is not null && string.Equals(message, _lastMessage, StringComparison.OrdinalIgnoreCase))
            {
                _suppressedCount++;
                return;
            }

            var text = new StringBuilder();
            if (_suppressedCount > 0)
            {
                // 最小レベルに関係なく Info で書く（旧版と同じ）
                text.Append(FormatLine(LogLevel.Info, $"(同一メッセージを {_suppressedCount} 回抑制)")).Append(NewLine);
                _suppressedCount = 0;
            }
            text.Append(FormatLine(level, message)).Append(NewLine);
            _lastMessage = message;

            RotateIfNeeded();
            Append(Utf8WithoutBom.GetBytes(text.ToString()));
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private static string FormatLine(LogLevel level, string message) =>
        $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level.ToString().ToUpperInvariant()}] {message}";

    // 書く前に本体が上限以上なら .1 へ移す。世代は1つ
    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < _maxSizeBytes)
            {
                return;
            }

            var backup = _path + ".1";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
            File.Move(_path, backup);
        }
        catch (Exception)
        {
        }
    }

    // FileShare.ReadWrite で開く。別プロセスが書き込み用に掴んでいても共有違反にしない
    private void Append(byte[] bytes)
    {
        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                return;
            }
            catch (Exception)
            {
                if (attempt >= MaxWriteAttempts)
                {
                    return;
                }
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
