using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AutoSwitch.Audio;

/// <summary>
/// Core Audio の IMMDeviceEnumerator で再生デバイスを扱う。
/// 通知の購読やキャッシュは持たず、呼ばれるたびに取得する。
/// </summary>
public sealed class CoreAudioEndpointService : IDisposable
{
    private const ushort VtLpwstr = 31;
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly ERole[] PlaybackRoles = [ERole.Console, ERole.Multimedia, ERole.Communications];

    private readonly StrategyBasedComWrappers _wrappers = new();
    private readonly IMMDeviceEnumerator _enumerator;
    private bool _disposed;

    /// <exception cref="AudioServiceException">Core Audio を初期化できなかった。</exception>
    public CoreAudioEndpointService()
    {
        _enumerator = Guard("initialize Core Audio", () => CreateInstance<IMMDeviceEnumerator>(ComIds.MMDeviceEnumerator, "MMDeviceEnumerator"));
    }

    /// <summary>有効（DEVICE_STATE_ACTIVE）な再生デバイスの一覧。</summary>
    /// <exception cref="AudioServiceException">取得に失敗した。</exception>
    public IReadOnlyList<AudioEndpoint> GetPlaybackEndpoints() => Guard("enumerate playback devices", () =>
    {
        ThrowIfDisposed();
        Check(_enumerator.EnumAudioEndpoints(EDataFlow.Render, NativeMethods.DeviceStateActive, out var collectionPointer), "IMMDeviceEnumerator.EnumAudioEndpoints");
        var collection = Wrap<IMMDeviceCollection>(collectionPointer);
        try
        {
            Check(collection.GetCount(out var count), "IMMDeviceCollection.GetCount");

            var endpoints = new List<AudioEndpoint>((int)count);
            for (uint i = 0; i < count; i++)
            {
                Check(collection.Item(i, out var devicePointer), "IMMDeviceCollection.Item");
                endpoints.Add(ToEndpoint(Wrap<IMMDevice>(devicePointer)));
            }

            return (IReadOnlyList<AudioEndpoint>)endpoints;
        }
        finally
        {
            Release(collection);
        }
    });

    /// <summary>既定の再生デバイス（Multimedia ロール）。再生デバイスが無ければ null。</summary>
    /// <exception cref="AudioServiceException">取得に失敗した。</exception>
    public AudioEndpoint? GetDefaultPlayback() => Guard("get the default playback device", () =>
    {
        ThrowIfDisposed();
        var hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var pointer);
        if (hr == NativeMethods.HResultElementNotFound)
        {
            return null;
        }

        Check(hr, "IMMDeviceEnumerator.GetDefaultAudioEndpoint");
        return ToEndpoint(Wrap<IMMDevice>(pointer));
    });

    /// <summary>
    /// 再生の既定デバイスを Console・Multimedia・Communications の順に変更する。
    /// 変更の前に、ID が有効な再生デバイスの一覧に含まれることを確認する。含まれなければ何も変更しない（録音デバイスの ID も含まれないので変更されない）。
    /// </summary>
    /// <exception cref="AudioServiceException">
    /// ID が有効な再生デバイスに無い（HRESULT は E_NOTFOUND、変更なし）、または変更に失敗した。
    /// 2 つ目以降のロールで失敗したときは、メッセージに変更済みのロールを含める（既定が部分的に変わっている可能性がある）。
    /// </exception>
    public void SetDefaultPlayback(string endpointId)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        Guard("set the default playback device", () =>
        {
            ThrowIfDisposed();
            if (!GetPlaybackEndpoints().Any(endpoint => endpoint.Id == endpointId))
            {
                throw new AudioServiceException(
                    $"No active playback device has id={endpointId}; the default playback device was not changed",
                    NativeMethods.HResultElementNotFound);
            }

            var policy = CreateInstance<IPolicyConfig>(ComIds.PolicyConfigClient, "PolicyConfigClient");
            try
            {
                var changed = new List<ERole>();
                foreach (var role in PlaybackRoles)
                {
                    try
                    {
                        Check(policy.SetDefaultEndpoint(endpointId, role), $"IPolicyConfig.SetDefaultEndpoint({role})");
                    }
                    catch (Exception ex) when (changed.Count > 0)
                    {
                        throw new AudioServiceException(
                            $"{ex.Message}; the default playback device may be partially changed (already changed: {string.Join(", ", changed)})",
                            ex.HResult,
                            ex);
                    }

                    changed.Add(role);
                }
            }
            finally
            {
                Release(policy);
            }

            return true;
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release(_enumerator);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static T Guard<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not AudioServiceException and not ObjectDisposedException)
        {
            throw new AudioServiceException($"Failed to {operation}: {ex.Message}", ex.HResult, ex);
        }
    }

    private T CreateInstance<T>(Guid clsid, string className)
    {
        var iid = typeof(T).GUID;
        Check(
            NativeMethods.CoCreateInstance(in clsid, 0, NativeMethods.ClsctxInprocServer, in iid, out var pointer),
            $"CoCreateInstance({className})");
        return Wrap<T>(pointer);
    }

    /// <summary>
    /// ポインタをラッパーにし、受け取った参照を手放す。
    /// UniqueInstance にして、Release で他の呼び出しと共有するラッパーを壊さないようにする。
    /// </summary>
    private T Wrap<T>(nint pointer)
    {
        try
        {
            return (T)_wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is ComObject wrapper)
        {
            wrapper.FinalRelease();
        }
    }

    private AudioEndpoint ToEndpoint(IMMDevice device)
    {
        try
        {
            Check(device.GetId(out var id), "IMMDevice.GetId");
            return new AudioEndpoint(id, ReadFriendlyName(device) ?? id);
        }
        finally
        {
            Release(device);
        }
    }

    private string? ReadFriendlyName(IMMDevice device)
    {
        Check(device.OpenPropertyStore(NativeMethods.StgmRead, out var storePointer), "IMMDevice.OpenPropertyStore");
        var store = Wrap<IPropertyStore>(storePointer);
        try
        {
            Check(store.GetValue(in FriendlyNameKey, out var value), "IPropertyStore.GetValue(PKEY_Device_FriendlyName)");
            try
            {
                return value.VarType == VtLpwstr ? Marshal.PtrToStringUni(value.Data1) : null;
            }
            finally
            {
                NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new AudioServiceException($"{what} failed (HRESULT 0x{hr:X8})", hr);
        }
    }
}

/// <summary>音声操作の失敗。メッセージに操作名、HResult に失敗の HRESULT を持つ。</summary>
public sealed class AudioServiceException : Exception
{
    public AudioServiceException(string message, int hresult, Exception? innerException = null)
        : base(message, innerException)
    {
        HResult = hresult;
    }
}
