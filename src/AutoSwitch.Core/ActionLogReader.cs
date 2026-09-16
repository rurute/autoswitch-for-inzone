using System.Text;

namespace AutoSwitch.Core;

public enum ReadStatus
{
    Ok,
    FileMissing,
    Truncated,
    Recreated,
    Error,
}

public sealed record ReadResult(ReadStatus Status, IReadOnlyList<string> Lines, string Message);

/// <summary>
/// ActionLog.log を前回の続きから読む。規則は旧版 ActionLogReader.psm1 に合わせる。
/// </summary>
public sealed class ActionLogReader
{
    private const int MaxReadBytes = 4194304;   // 1 回で読む上限 4MB。超過分は次回へ持ち越す
    private const int FingerprintBytes = 256;   // ファイル同一性の指紋に使う先頭バイト数

    private readonly string _path;
    private long _lastLength;
    private byte[] _headFingerprint = [];

    public ActionLogReader(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    public long Offset { get; private set; }

    /// <summary>例外を外へ出さない。失敗は <see cref="ReadStatus.Error"/> として返す。</summary>
    public ReadResult Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Offset = 0;
                _lastLength = 0;
                _headFingerprint = [];
                return Result(ReadStatus.FileMissing);
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;

            // 長さが同じでも必ず指紋を照合する。「変化が無ければ開かない」最適化は、
            // 同じ長さの別内容への差し替えを永久に見逃すので入れてはいけない
            var status = ReadStatus.Ok;
            if (length < Offset || length < _lastLength)
            {
                Offset = 0;
                _headFingerprint = [];
                status = ReadStatus.Truncated;
            }
            else if (Offset > 0 && !ReadHeadFingerprint(stream, Offset).AsSpan().SequenceEqual(_headFingerprint))
            {
                Offset = 0;
                _headFingerprint = [];
                status = ReadStatus.Recreated;
            }

            _lastLength = length;

            if (length <= Offset)
            {
                return Result(status);
            }

            var buffer = new byte[(int)Math.Min(MaxReadBytes, length - Offset)];
            stream.Seek(Offset, SeekOrigin.Begin);
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            var lastLf = read > 0 ? Array.LastIndexOf(buffer, (byte)'\n', read - 1) : -1;
            if (lastLf < 0)
            {
                // 完結した行がまだ無い。オフセットは進めない
                _headFingerprint = ReadHeadFingerprint(stream, Offset);
                return Result(status);
            }

            var text = Encoding.UTF8.GetString(buffer, 0, lastLf + 1);
            Offset += lastLf + 1;
            _headFingerprint = ReadHeadFingerprint(stream, Offset);

            var lines = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r').TrimStart('﻿');
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            return new ReadResult(status, lines, string.Empty);
        }
        catch (Exception ex)
        {
            return new ReadResult(ReadStatus.Error, [], ex.Message);
        }
    }

    private static ReadResult Result(ReadStatus status) => new(status, [], string.Empty);

    /// <summary>
    /// 既読部分（先頭 min(FingerprintBytes, offset) バイト）。既読範囲だけを見るので、通常の追記では変化せず、
    /// ファイルの差し替えだけが検知できる。
    /// CreationTimeUtc は使わない。NTFS の file system tunneling により、削除後 15 秒以内に
    /// 同名ファイルを作り直すと作成日時が元の値のまま復元される（旧版で実機計測済み）。
    /// </summary>
    private static byte[] ReadHeadFingerprint(FileStream stream, long offset)
    {
        var count = (int)Math.Min(Math.Min(FingerprintBytes, offset), stream.Length);
        if (count <= 0)
        {
            return [];
        }

        var buffer = new byte[count];
        stream.Seek(0, SeekOrigin.Begin);
        var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return read == count ? buffer : buffer[..read];
    }
}
