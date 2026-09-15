using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AutoSwitch.Audio;

// Core Audio の COM 定義のうち、段階1で使う範囲だけを置く。
// インターフェースのメソッドの並びは vtable の順序そのもの。途中のメソッドは省略できない（末尾は省略可）。

internal static class ComIds
{
    public static readonly Guid MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>非公開の CPolicyConfigClient。Windows 10/11 で既定デバイスの変更に使われている既知の CLSID。</summary>
    public static readonly Guid PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
}

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

internal static partial class NativeMethods
{
    public const uint DeviceStateActive = 0x1;
    public const uint ClsctxInprocServer = 0x1;
    public const uint StgmRead = 0x0;
    public const int HResultElementNotFound = unchecked((int)0x80070490);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(ref PropVariant pvar);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid formatId, uint propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly uint PropertyId = propertyId;
}

/// <summary>x64 で 24 バイト。文字列（VT_LPWSTR）しか読まないので共用体は 2 語分のポインタとして扱う。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort VarType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Data1;
    public nint Data2;
}

// 取得したインターフェースはサービス側で個別に解放するため、out はポインタ（nint）で受ける。

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out nint devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out nint endpoint);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out nint device);
}

/// <summary>IID の末尾は 363F（363E は E_NOINTERFACE になる）。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid iid, uint clsCtx, nint activationParams, out nint iface);

    [PreserveSig]
    int OpenPropertyStore(uint access, out nint store);

    [PreserveSig]
    int GetId(out string id);
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(in PropertyKey key, out PropVariant value);
}

/// <summary>非公開インターフェース。使うのは SetDefaultEndpoint だけだが、vtable の順序を保つため前のメソッドも並べる。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
internal partial interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat(string deviceId, out nint format);

    [PreserveSig]
    int GetDeviceFormat(string deviceId, int useDefault, out nint format);

    [PreserveSig]
    int ResetDeviceFormat(string deviceId);

    [PreserveSig]
    int SetDeviceFormat(string deviceId, nint endpointFormat, nint mixFormat);

    [PreserveSig]
    int GetProcessingPeriod(string deviceId, int useDefault, out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod(string deviceId, in long period);

    [PreserveSig]
    int GetShareMode(string deviceId, nint mode);

    [PreserveSig]
    int SetShareMode(string deviceId, nint mode);

    [PreserveSig]
    int GetPropertyValue(string deviceId, int fxStore, in PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetPropertyValue(string deviceId, int fxStore, in PropertyKey key, in PropVariant value);

    [PreserveSig]
    int SetDefaultEndpoint(string deviceId, ERole role);
}
