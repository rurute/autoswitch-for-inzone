using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AutoSwitch.Core;
using Microsoft.Win32.SafeHandles;

namespace AutoSwitch.Hid;

/// <summary>
/// INZONE Buds の USB ドングル（054c:0ec2）の COL03（Usage Page 0xFF04 / Usage 0x0001）を Win32 の HID API で開く。
/// 同じ collection を読み取り用（非同期）と書き込み用（同期）の 2 つのハンドルで開き、読み取りは別のタスクで
/// 待ち続ける。読んだフレームは FrameReceived に渡し、WaitForFrame のためにキューへ積む。
/// TryOpen・Write・WaitForFrame は監視ループの 1 本のスレッドから呼ぶ前提。Close・Dispose はどのスレッドからでもよい
/// </summary>
public sealed class HidDongleTransport : IBudsTransport
{
    public const ushort VendorId = 0x054C;
    public const ushort ProductId = 0x0EC2;
    public const ushort UsagePage = 0xFF04;
    public const ushort Usage = 0x0001;

    // 読み取りタスクの終わりを待つ上限（終了操作が止まらないように区切る）
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    // WaitForFrame に渡すために積んでおくフレームの上限。あふれたら古いものから捨てる
    private const int QueueCapacity = 256;

    public const string NotFoundMessage = "INZONE Buds のドングルが見つかりません";

    private readonly object _gate = new();
    private Session? _session;
    private bool _disposed;

    public event Action<byte[]>? FrameReceived;

    public bool TryOpen(out string error)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                error = "ドングルの接続は破棄済みです";
                return false;
            }

            if (_session is not null)
            {
                error = "";
                return true;
            }

            if (!TryFindDevice(out var found, out error))
            {
                return false;
            }

            if (!TryOpenSession(found, out var session, out error))
            {
                return false;
            }

            _session = session;
            session.ReadTask = Task.Run(() => ReadLoopAsync(session));
            error = "";
            return true;
        }
    }

    public void Write(byte[] report)
    {
        var session = Volatile.Read(ref _session) ?? throw new IOException("ドングルを開いていません");
        var buffer = new byte[session.OutputLength];
        Array.Copy(report, buffer, Math.Min(report.Length, buffer.Length));
        try
        {
            session.Writer.Write(buffer, 0, buffer.Length);
        }
        catch (ObjectDisposedException ex)
        {
            // 別のスレッドから閉じられた
            throw new IOException("ドングルへの書き込みの途中で閉じられました", ex);
        }
    }

    public byte[]? WaitForFrame(Func<byte[], bool> match, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = Volatile.Read(ref _session) ?? throw new IOException("ドングルを開いていません");
        var started = Stopwatch.GetTimestamp();

        while (true)
        {
            // 読み取りの失敗が記録されていれば、キューに残ったフレームや時間切れより先に失敗として伝える
            // （失敗したセッションを DongleProbe に閉じさせ、Unavailable にするため）
            ThrowIfReadFailed(session);

            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            // TryTake はミリ秒未満を受け付けないので切り上げる
            var milliseconds = (int)Math.Min(int.MaxValue, Math.Ceiling(remaining.TotalMilliseconds));
            if (session.Frames.TryTake(out var frame, milliseconds, token))
            {
                if (match(frame))
                {
                    ThrowIfReadFailed(session);
                    return frame;
                }

                continue;
            }

            // 積み終わりにされていて空なら、読み取りが止まった（閉じられた）
            if (session.Frames.IsCompleted)
            {
                ThrowIfReadFailed(session);
                throw new IOException("ドングルからの読み取りが止まりました");
            }
        }
    }

    private static void ThrowIfReadFailed(Session session)
    {
        if (session.ReadError is { } readError)
        {
            throw new IOException($"ドングルからの読み取りに失敗しました: {readError.Message}", readError);
        }
    }

    public void Close()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        session?.Shutdown();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Close();
    }

    private async Task ReadLoopAsync(Session session)
    {
        var buffer = new byte[session.InputLength];
        var token = session.Cancellation.Token;
        try
        {
            while (true)
            {
                var read = await session.Reader.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0)
                {
                    // HID の読み取りは 1 レポートずつ返る。0 バイトは異常なので、空回りせず止める
                    throw new IOException("ドングルからの読み取りが 0 バイトで終わりました");
                }

                var frame = buffer.AsSpan(0, read).ToArray();
                try
                {
                    FrameReceived?.Invoke(frame);
                }
                catch
                {
                    // 受け手の失敗で読み取りを止めない
                }

                if (!session.Frames.TryAdd(frame))
                {
                    session.Frames.TryTake(out _);
                    session.Frames.TryAdd(frame);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Close による取り消し
        }
        catch (Exception ex)
        {
            // 抜けた・スリープ復帰でハンドルが無効になった・閉じられた、など。WaitForFrame が IOException にして伝える
            session.ReadError = ex;
        }
        finally
        {
            session.Frames.CompleteAdding();
        }
    }

    private readonly record struct FoundDevice(string Path, int InputLength, int OutputLength);

    private static bool TryFindDevice(out FoundDevice found, out string error)
    {
        found = default;
        HidNative.HidD_GetHidGuid(out var hidGuid);
        var deviceInfoSet = HidNative.SetupDiGetClassDevsW(
            ref hidGuid, null, IntPtr.Zero, HidNative.DIGCF_PRESENT | HidNative.DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == HidNative.InvalidHandleValue)
        {
            error = $"HID 機器を列挙できません（Win32 エラー {Marshal.GetLastWin32Error()}）";
            return false;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new HidNative.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<HidNative.SP_DEVICE_INTERFACE_DATA>(),
                };
                if (!HidNative.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    var code = Marshal.GetLastWin32Error();
                    if (code == HidNative.ERROR_NO_MORE_ITEMS)
                    {
                        break;
                    }

                    error = $"HID 機器を列挙できません（Win32 エラー {code}）";
                    return false;
                }

                var path = GetDevicePath(deviceInfoSet, ref interfaceData);
                if (path is not null && TryMatch(path, out found))
                {
                    error = "";
                    return true;
                }
            }
        }
        finally
        {
            HidNative.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        error = NotFoundMessage;
        return false;
    }

    private static string? GetDevicePath(IntPtr deviceInfoSet, ref HidNative.SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        // 1 回目は必要な大きさを聞くだけ（ERROR_INSUFFICIENT_BUFFER で失敗するのが正常）
        HidNative.SetupDiGetDeviceInterfaceDetailW(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required <= HidNative.DeviceInterfaceDetailPathOffset)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W は可変長。cbSize には固定部分の大きさを書く
            Marshal.WriteInt32(buffer, HidNative.DeviceInterfaceDetailHeaderSize);
            if (!HidNative.SetupDiGetDeviceInterfaceDetailW(deviceInfoSet, ref interfaceData, buffer, required, out _, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer + HidNative.DeviceInterfaceDetailPathOffset);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // アクセス 0 で開いて（読み書きの権限なしでも属性と能力は読める）VID/PID と Usage を照合する
    private static bool TryMatch(string path, out FoundDevice found)
    {
        found = default;
        using var handle = HidNative.CreateFileW(
            path, 0, HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE, IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        var attributes = new HidNative.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HidNative.HIDD_ATTRIBUTES>() };
        if (!HidNative.HidD_GetAttributes(handle, ref attributes)
            || attributes.VendorID != VendorId
            || attributes.ProductID != ProductId)
        {
            return false;
        }

        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return false;
        }

        try
        {
            if (HidNative.HidP_GetCaps(preparsed, out var caps) != HidNative.HIDP_STATUS_SUCCESS
                || caps.UsagePage != UsagePage
                || caps.Usage != Usage
                || caps.InputReportByteLength == 0
                || caps.OutputReportByteLength == 0)
            {
                return false;
            }

            found = new FoundDevice(path, caps.InputReportByteLength, caps.OutputReportByteLength);
            return true;
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }
    }

    private static bool TryOpenSession(FoundDevice found, out Session session, out string error)
    {
        session = null!;
        const uint share = HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE;

        var readHandle = HidNative.CreateFileW(
            found.Path, HidNative.GENERIC_READ, share, IntPtr.Zero, HidNative.OPEN_EXISTING, HidNative.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (readHandle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            readHandle.Dispose();
            error = OpenFailedMessage("読み取り", code);
            return false;
        }

        var writeHandle = HidNative.CreateFileW(
            found.Path, HidNative.GENERIC_WRITE, share, IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
        if (writeHandle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            writeHandle.Dispose();
            readHandle.Dispose();
            error = OpenFailedMessage("書き込み", code);
            return false;
        }

        FileStream? reader = null;
        try
        {
            // バッファを持たない（1 レポートずつ読み書きする）
            reader = new FileStream(readHandle, FileAccess.Read, 0, isAsync: true);
            var writer = new FileStream(writeHandle, FileAccess.Write, 0, isAsync: false);
            session = new Session(reader, writer, found.InputLength, found.OutputLength);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            if (reader is not null)
            {
                reader.Dispose();
            }
            else
            {
                readHandle.Dispose();
            }

            writeHandle.Dispose();
            error = $"INZONE Buds のドングルを開けません: {ex.Message}";
            return false;
        }
    }

    private static string OpenFailedMessage(string purpose, int code) =>
        $"INZONE Buds のドングルを{purpose}用に開けません（Win32 エラー {code}: {new Win32Exception(code).Message}）";

    // 1 回開いてから閉じるまでのハンドル・読み取りタスク・キュー。開き直すたびに作り直す
    private sealed class Session(FileStream reader, FileStream writer, int inputLength, int outputLength)
    {
        public FileStream Reader { get; } = reader;
        public FileStream Writer { get; } = writer;
        public int InputLength { get; } = inputLength;
        public int OutputLength { get; } = outputLength;
        public CancellationTokenSource Cancellation { get; } = new();
        public BlockingCollection<byte[]> Frames { get; } = new(QueueCapacity);
        public Task? ReadTask { get; set; }

        private volatile Exception? _readError;

        public Exception? ReadError
        {
            get => _readError;
            set => _readError = value;
        }

        // 読み取りを取り消し、終わりを CloseTimeout まで待ってからハンドルを閉じる。
        // 待ちきれなくても先へ進む（ハンドルを閉じれば残った読み取りも終わる）
        public void Shutdown()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (AggregateException)
            {
                // 取り消しに登録された処理の失敗。閉じる処理は続ける
            }

            var finished = false;
            try
            {
                finished = ReadTask?.Wait(CloseTimeout) ?? true;
            }
            catch (AggregateException)
            {
                finished = true;
            }

            // 待っている WaitForFrame を起こす（読み取りタスクの finally と重なっても問題ない）
            Frames.CompleteAdding();

            Reader.Dispose();
            Writer.Dispose();

            if (finished)
            {
                // 読み取りタスクがまだトークンを使っているかもしれないうちは破棄しない
                Cancellation.Dispose();
            }
        }
    }
}
