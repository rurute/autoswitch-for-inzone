using System.Text;

namespace AutoSwitch.Core.Tests;

// 実際のファイルで検証する。各テストは一時ディレクトリを作り、終了時に削除する
public sealed class ActionLogReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ActionLogReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "alr-" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "ActionLog.log");
    }

    public void Dispose()
    {
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

    // 既存ファイルは削除せずに中身を置き換える（旧版テストの Set-LogContent と同じ）
    private void SetContent(string text, bool bom = false) =>
        File.WriteAllText(_path, text, new UTF8Encoding(bom));

    private void AppendContent(string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void NewReader_OffsetIsZero()
    {
        Assert.Equal(0, new ActionLogReader(_path).Offset);
    }

    [Fact]
    public void Read_FileMissing_ReturnsFileMissing()
    {
        var reader = new ActionLogReader(_path);

        var result = reader.Read();

        Assert.Equal(ReadStatus.FileMissing, result.Status);
        Assert.Empty(result.Lines);
        Assert.Equal(0, reader.Offset);
    }

    [Fact]
    public void Read_FileDeletedAfterRead_ReturnsFileMissingAndResetsOffset()
    {
        SetContent("line1\nline2\n");
        var reader = new ActionLogReader(_path);
        reader.Read();
        Assert.True(reader.Offset > 0);

        File.Delete(_path);
        var result = reader.Read();

        Assert.Equal(ReadStatus.FileMissing, result.Status);
        Assert.Empty(result.Lines);
        Assert.Equal(0, reader.Offset);
    }

    [Fact]
    public void Read_FirstTime_ReturnsAllLines()
    {
        SetContent("line1\nline2\nline3\n");
        var reader = new ActionLogReader(_path);

        var result = reader.Read();

        Assert.Equal(ReadStatus.Ok, result.Status);
        Assert.Equal(["line1", "line2", "line3"], result.Lines);
    }

    [Fact]
    public void Read_SecondTime_ReturnsAppendedLinesOnly()
    {
        SetContent("line1\nline2\n");
        var reader = new ActionLogReader(_path);
        reader.Read();

        AppendContent("line3\n");
        var result = reader.Read();

        Assert.Equal(ReadStatus.Ok, result.Status);
        Assert.Equal(["line3"], result.Lines);
    }

    [Fact]
    public void Read_NothingAppended_ReturnsOkWithNoLines()
    {
        SetContent("line1\n");
        var reader = new ActionLogReader(_path);
        reader.Read();

        var result = reader.Read();

        Assert.Equal(ReadStatus.Ok, result.Status);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Read_AppendOneLineFiveTimes_IsNotMistakenForRecreated()
    {
        SetContent("aaa\n");
        var reader = new ActionLogReader(_path);
        reader.Read();

        for (var i = 1; i <= 5; i++)
        {
            AppendContent($"line{i}\n");
            var result = reader.Read();

            Assert.Equal(ReadStatus.Ok, result.Status);
            Assert.Equal([$"line{i}"], result.Lines);
        }
    }

    [Fact]
    public void Read_OffsetBeyondFingerprintLength_KeepsReadingAppendedLines()
    {
        var longLine = new string('x', 400);
        SetContent(longLine + "\n");
        var reader = new ActionLogReader(_path);
        var first = reader.Read();
        Assert.Equal([longLine], first.Lines);
        Assert.True(reader.Offset > 256);

        AppendContent("after\n");
        var result = reader.Read();

        Assert.Equal(ReadStatus.Ok, result.Status);
        Assert.Equal(["after"], result.Lines);
    }

    [Fact]
    public void Read_LastLineWithoutNewline_IsCarriedOverUntilCompleted()
    {
        SetContent("line1\nincomplete");
        var reader = new ActionLogReader(_path);

        var first = reader.Read();
        Assert.Equal(["line1"], first.Lines);
        Assert.Equal(6, reader.Offset);

        // 続きが来ない間は、何度読んでも途中行を返さずオフセットも進めない
        var again = reader.Read();
        Assert.Equal(ReadStatus.Ok, again.Status);
        Assert.Empty(again.Lines);
        Assert.Equal(6, reader.Offset);

        AppendContent("-now-complete\n");
        var result = reader.Read();

        Assert.Equal(ReadStatus.Ok, result.Status);
        Assert.Equal(["incomplete-now-complete"], result.Lines);
        Assert.Equal(new FileInfo(_path).Length, reader.Offset);
    }

    [Fact]
    public void Read_ShrunkBelowOffset_ReturnsTruncatedAndNewLines()
    {
        SetContent("aaaa\nbbbb\ncccc\n");
        var reader = new ActionLogReader(_path);
        reader.Read();

        SetContent("zzzz\n");
        var result = reader.Read();

        Assert.Equal(ReadStatus.Truncated, result.Status);
        Assert.Equal(["zzzz"], result.Lines);
    }

    [Fact]
    public void Read_ShrunkBelowLastLengthButNotBelowOffset_ReturnsTruncatedAndRereadsFromStart()
    {
        // 改行なしの末尾を残して Offset(5) < LastLength(100) にする
        SetContent("aaaa\n" + new string('x', 95));
        var reader = new ActionLogReader(_path);
        reader.Read();
        Assert.Equal(5, reader.Offset);

        // 長さ 50 は LastLength 未満・Offset 以上
        var replacement = new string('y', 49);
        SetContent(replacement + "\n");
        var result = reader.Read();

        Assert.Equal(ReadStatus.Truncated, result.Status);
        Assert.Equal([replacement], result.Lines);
    }

    [Fact]
    public void Read_DeletedAndRecreatedAfter50ms_ReturnsRecreatedAndAllNewLines()
    {
        // NTFS の file system tunneling により作成日時が元の値に戻る窓。作成日時では検知できない
        SetContent("old1\nold2\n");
        var reader = new ActionLogReader(_path);
        reader.Read();

        File.Delete(_path);
        Thread.Sleep(50);
        SetContent("new1\nnew2\nnew3\n");
        var result = reader.Read();

        Assert.Equal(ReadStatus.Recreated, result.Status);
        Assert.Equal(["new1", "new2", "new3"], result.Lines);
    }

    [Fact]
    public void Read_ReplacedWithLongerDifferentContent_ReturnsRecreatedAndAllNewLines()
    {
        SetContent("old1\nold2\n");
        var reader = new ActionLogReader(_path);
        reader.Read();

        SetContent("new1\nnew2\nnew3\nnew4\n");
        var result = reader.Read();

        Assert.Equal(ReadStatus.Recreated, result.Status);
        Assert.Equal(["new1", "new2", "new3", "new4"], result.Lines);
    }

    [Fact]
    public void Read_ReplacedWithSameLengthDifferentContent_ReturnsRecreatedAndAllNewLines()
    {
        // 長さが変わらないので「変化が無ければ開かない」最適化があるとすり抜ける
        SetContent("old1\nold2\n");
        var reader = new ActionLogReader(_path);
        reader.Read();

        SetContent("new1\nnew2\n");
        var result = reader.Read();

        Assert.Equal(ReadStatus.Recreated, result.Status);
        Assert.Equal(["new1", "new2"], result.Lines);
    }

    [Fact]
    public void Read_Utf8Bom_IsRemovedFromFirstLine()
    {
        SetContent("first\nsecond\n", bom: true);
        var reader = new ActionLogReader(_path);

        var result = reader.Read();

        Assert.Equal(["first", "second"], result.Lines);
        Assert.NotEqual('﻿', result.Lines[0][0]);
    }

    [Fact]
    public void Read_Crlf_RemovesCarriageReturn()
    {
        SetContent("aaa\r\nbbb\r\n");
        var reader = new ActionLogReader(_path);

        var result = reader.Read();

        Assert.Equal(["aaa", "bbb"], result.Lines);
    }

    [Fact]
    public void Read_EmptyLines_AreSkipped()
    {
        SetContent("aaa\n\n\nbbb\n");
        var reader = new ActionLogReader(_path);

        var result = reader.Read();

        Assert.Equal(["aaa", "bbb"], result.Lines);
    }

    [Fact]
    public void Read_MultibyteText_KeepsOffsetAligned()
    {
        SetContent("スピーカー (INZONE Buds - Game)\n");
        var reader = new ActionLogReader(_path);
        var first = reader.Read();
        Assert.Equal(["スピーカー (INZONE Buds - Game)"], first.Lines);

        AppendContent("スピーカー (Realtek(R) Audio)\n");
        var result = reader.Read();

        Assert.Equal(ReadStatus.Ok, result.Status);
        Assert.Equal(["スピーカー (Realtek(R) Audio)"], result.Lines);
    }

    [Fact]
    public void Read_LockedByAnotherHandle_ReturnsErrorWithoutThrowing()
    {
        // 実際の排他ロックで失敗させる。FileShare.None で開かれていると ReadWrite|Delete の共有要求は失敗する
        SetContent("aaa\n");
        var reader = new ActionLogReader(_path);

        ReadResult? result = null;
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = Record.Exception(() => result = reader.Read());

            Assert.Null(exception);
            Assert.NotNull(result);
            Assert.Equal(ReadStatus.Error, result.Status);
            Assert.Empty(result.Lines);
            Assert.False(string.IsNullOrEmpty(result.Message));
        }

        // ロックが外れたら次の読み取りで復帰する
        var recovered = reader.Read();
        Assert.Equal(ReadStatus.Ok, recovered.Status);
        Assert.Equal(["aaa"], recovered.Lines);
    }

    [Fact]
    public void Read_MoreThan4MB_ReadsUpTo4MBAndCarriesOverTheRest()
    {
        // 1行 1001 バイト × 5000 行。4MB (4194304) に収まる完結行は 4190 行
        const int lineBytes = 1001;
        const int lineCount = 5000;
        var line = new string('x', lineBytes - 1);
        SetContent(string.Concat(Enumerable.Repeat(line + "\n", lineCount)));
        var reader = new ActionLogReader(_path);

        var first = reader.Read();
        Assert.Equal(ReadStatus.Ok, first.Status);
        Assert.Equal(4190, first.Lines.Count);
        Assert.Equal(4190L * lineBytes, reader.Offset);

        var second = reader.Read();
        Assert.Equal(ReadStatus.Ok, second.Status);
        Assert.Equal(lineCount - 4190, second.Lines.Count);
        Assert.Equal((long)lineCount * lineBytes, reader.Offset);
    }
}
