using System.Text;

namespace AutoSwitch.Core.Tests;

// 実際のファイルで検証する。各テストは一時ディレクトリを使い、終了時に削除する
public sealed class FileLoggerTests : IDisposable
{
    private const string TimeStampPattern = @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}";

    private readonly string _dir;
    private readonly string _path;

    public FileLoggerTests()
    {
        // フォルダは作らない（ロガーが作ることを確認するケースがある）
        _dir = Path.Combine(Path.GetTempPath(), "flog-" + Guid.NewGuid().ToString());
        _path = Path.Combine(_dir, "logs", "test.log");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string[] ReadLines() => File.ReadAllLines(_path, Encoding.UTF8);

    // 旧版テストと同じ 1 行あたり約 80 バイトの文言
    private static string RotationMessage(int i) => $"msg {i} {new string('x', 40)}";

    [Fact]
    public void Write_DirectoryMissing_CreatesDirectoryAndWrites()
    {
        Assert.False(Directory.Exists(_dir));
        var logger = new FileLogger(_path, LogLevel.Info, 1024);

        logger.Write(LogLevel.Info, "hello");

        Assert.True(File.Exists(_path));
        Assert.Single(ReadLines());
    }

    [Fact]
    public void Write_WarnJapanese_MatchesLineFormat()
    {
        var logger = new FileLogger(_path, LogLevel.Info, 1024);

        logger.Write(LogLevel.Warn, "デバイスが見つかりません");

        var lines = ReadLines();
        Assert.Single(lines);
        Assert.Matches($@"^{TimeStampPattern} \[WARN\] デバイスが見つかりません$", lines[0]);
    }

    [Fact]
    public void Write_LevelNames_AreUpperCase()
    {
        var logger = new FileLogger(_path, LogLevel.Debug, 1024);

        logger.Write(LogLevel.Debug, "d");
        logger.Write(LogLevel.Info, "i");
        logger.Write(LogLevel.Warn, "w");
        logger.Write(LogLevel.Error, "e");

        var lines = ReadLines();
        Assert.Equal(4, lines.Length);
        Assert.Matches($@"^{TimeStampPattern} \[DEBUG\] d$", lines[0]);
        Assert.Matches($@"^{TimeStampPattern} \[INFO\] i$", lines[1]);
        Assert.Matches($@"^{TimeStampPattern} \[WARN\] w$", lines[2]);
        Assert.Matches($@"^{TimeStampPattern} \[ERROR\] e$", lines[3]);
    }

    [Fact]
    public void Write_UsesCrlfAndUtf8WithoutBom()
    {
        var logger = new FileLogger(_path, LogLevel.Info, 1024);

        logger.Write(LogLevel.Info, "日本語の文言");
        logger.Write(LogLevel.Info, "second");

        var bytes = File.ReadAllBytes(_path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        var lines = text.Split("\r\n");
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(" [INFO] 日本語の文言", lines[0]);
        Assert.EndsWith(" [INFO] second", lines[1]);
        Assert.Equal("", lines[2]);
        Assert.DoesNotContain('\n', string.Concat(lines));
        Assert.DoesNotContain('\r', string.Concat(lines));
    }

    [Fact]
    public void Write_MinimumWarn_WritesOnlyError()
    {
        var logger = new FileLogger(_path, LogLevel.Warn, 1024);

        logger.Write(LogLevel.Debug, "debug msg");
        logger.Write(LogLevel.Info, "info msg");
        logger.Write(LogLevel.Error, "error msg");

        var lines = ReadLines();
        Assert.Single(lines);
        Assert.Contains("error msg", lines[0]);
    }

    [Fact]
    public void Write_SameMessageFiveTimes_WritesOneLine()
    {
        var logger = new FileLogger(_path, LogLevel.Info, 1024);

        for (var i = 0; i < 5; i++)
        {
            logger.Write(LogLevel.Warn, "same message");
        }

        Assert.Single(ReadLines());
    }

    [Fact]
    public void Write_SameMessageDifferentCase_WritesOneLine()
    {
        var logger = new FileLogger(_path, LogLevel.Info, 1024);

        logger.Write(LogLevel.Warn, "same message");
        logger.Write(LogLevel.Warn, "SAME MESSAGE");

        var lines = ReadLines();
        Assert.Single(lines);
        Assert.Contains("same message", lines[0]);
    }

    [Fact]
    public void Write_SameMessageFourTimesThenDifferent_WritesSuppressedCountFirst()
    {
        var logger = new FileLogger(_path, LogLevel.Info, 1024);

        for (var i = 0; i < 4; i++)
        {
            logger.Write(LogLevel.Warn, "same message");
        }
        logger.Write(LogLevel.Info, "different message");

        var lines = ReadLines();
        Assert.Equal(3, lines.Length);
        Assert.Matches($@"^{TimeStampPattern} \[WARN\] same message$", lines[0]);
        Assert.Contains("同一メッセージを 3 回抑制", lines[1]);
        Assert.Matches($@"^{TimeStampPattern} \[INFO\] \(同一メッセージを 3 回抑制\)$", lines[1]);
        Assert.Matches($@"^{TimeStampPattern} \[INFO\] different message$", lines[2]);
    }

    [Fact]
    public void Write_SuppressedCount_IsWrittenAtInfoEvenBelowMinimumLevel()
    {
        // 旧版は抑制件数の行を最小レベルに関係なく Info で書く
        var logger = new FileLogger(_path, LogLevel.Warn, 1024);

        for (var i = 0; i < 3; i++)
        {
            logger.Write(LogLevel.Warn, "same message");
        }
        logger.Write(LogLevel.Error, "different message");

        var lines = ReadLines();
        Assert.Equal(3, lines.Length);
        Assert.Matches($@"^{TimeStampPattern} \[INFO\] \(同一メッセージを 2 回抑制\)$", lines[1]);
        Assert.Matches($@"^{TimeStampPattern} \[ERROR\] different message$", lines[2]);
    }

    [Fact]
    public void Write_BelowMinimumLevel_IsNotUsedForSuppression()
    {
        var logger = new FileLogger(_path, LogLevel.Warn, 1024);

        logger.Write(LogLevel.Warn, "same message");
        logger.Write(LogLevel.Debug, "ignored message");   // 書かれず、直前の文言も変えない
        logger.Write(LogLevel.Warn, "same message");
        logger.Write(LogLevel.Warn, "next message");

        var lines = ReadLines();
        Assert.Equal(3, lines.Length);
        Assert.Contains("same message", lines[0]);
        Assert.Matches($@"^{TimeStampPattern} \[INFO\] \(同一メッセージを 1 回抑制\)$", lines[1]);
        Assert.Contains("next message", lines[2]);
        Assert.DoesNotContain(lines, line => line.Contains("ignored message"));
    }

    [Fact]
    public void Write_MaxSize1KB_80Lines_RotatesToBackup()
    {
        var logger = new FileLogger(_path, LogLevel.Info, 1);

        for (var i = 1; i <= 80; i++)
        {
            logger.Write(LogLevel.Info, RotationMessage(i));
        }

        Assert.True(File.Exists(_path + ".1"));
        // ローテーションは追記の「前」に判定するので、直後は上限を 1 回分だけ超えうる
        Assert.True(new FileInfo(_path).Length < 2048);
    }

    [Fact]
    public void Write_MaxSize1KB_400Lines_KeepsOnlyOneBackup()
    {
        var logger = new FileLogger(_path, LogLevel.Info, 1);

        for (var i = 1; i <= 400; i++)
        {
            logger.Write(LogLevel.Info, RotationMessage(i));
        }

        Assert.True(File.Exists(_path + ".1"));
        Assert.False(File.Exists(_path + ".2"));
    }

    [Fact]
    public void Write_BodyAtLimit_RotatesBeforeWriting()
    {
        // 上限ちょうど（1024 バイト）は「上限以上」なので、書く前に .1 へ移す
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, Encoding.ASCII.GetBytes(new string('x', 1024)));
        File.WriteAllText(_path + ".1", "old backup");
        var logger = new FileLogger(_path, LogLevel.Info, 1);

        logger.Write(LogLevel.Info, "after rotation");

        Assert.Equal(1024, new FileInfo(_path + ".1").Length);
        var lines = ReadLines();
        Assert.Single(lines);
        Assert.EndsWith(" [INFO] after rotation", lines[0]);
    }

    [Fact]
    public void Write_BodyBelowLimit_DoesNotRotate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, Encoding.ASCII.GetBytes(new string('x', 1023)));
        var logger = new FileLogger(_path, LogLevel.Info, 1);

        logger.Write(LogLevel.Info, "appended");

        Assert.False(File.Exists(_path + ".1"));
        Assert.True(new FileInfo(_path).Length > 1023);
    }

    [Fact]
    public void Write_DirectoryCannotBeCreated_DoesNotThrow()
    {
        // 既存ファイルの配下はフォルダを作れない
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker.txt");
        File.WriteAllText(blocker, "not a directory");
        var path = Path.Combine(blocker, "logs", "test.log");

        var exception = Record.Exception(() =>
        {
            var logger = new FileLogger(path, LogLevel.Info, 1024);
            logger.Write(LogLevel.Error, "boom");
            logger.Write(LogLevel.Error, "boom again");
        });

        Assert.Null(exception);
        Assert.Equal("not a directory", File.ReadAllText(blocker));
    }

    [Fact]
    public void Write_InvalidArguments_DoNotThrow()
    {
        var exception = Record.Exception(() =>
        {
            new FileLogger(null!, LogLevel.Info, 1024).Write(LogLevel.Info, "no path");
            new FileLogger("", LogLevel.Info, 1024).Write(LogLevel.Info, "empty path");
            new FileLogger(_path, LogLevel.Info, 1024).Write(LogLevel.Info, null!);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Write_WhileAnotherHandleHoldsAppendReadWrite_KeepsBothLines()
    {
        var logger = new FileLogger(_path, LogLevel.Info, 1024);
        logger.Write(LogLevel.Info, "first line");

        // 他プロセスが追記用に開いている状況を模す
        using (new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            logger.Write(LogLevel.Info, "second line");
        }

        var lines = ReadLines();
        Assert.Equal(2, lines.Length);
        Assert.Contains("first line", lines[0]);
        Assert.Contains("second line", lines[1]);
    }

    // F5: ロックを Monitor.TryEnter へ変えても、並行して書いた行が壊れたり落ちたりしない
    [Fact]
    public void Write_FromManyThreads_KeepsEveryLine()
    {
        const int threads = 16;
        const int linesPerThread = 50;
        var logger = new FileLogger(_path, LogLevel.Info, maxSizeKB: 1024);

        Parallel.For(0, threads, thread =>
        {
            for (var i = 0; i < linesPerThread; i++)
            {
                logger.Write(LogLevel.Info, $"thread {thread} line {i}");
            }
        });

        var lines = File.ReadAllLines(_path);
        Assert.Equal(threads * linesPerThread, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[INFO\] thread \d+ line \d+$", line));

        // 行数だけでなく、どの行がどれかまで固定する（取り違えや重複を見逃さない）
        var expected = from thread in Enumerable.Range(0, threads)
                       from i in Enumerable.Range(0, linesPerThread)
                       select $"thread {thread} line {i}";
        var actual = lines.Select(line => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..]);
        Assert.Equal(expected.Order(), actual.Order());
    }
}
