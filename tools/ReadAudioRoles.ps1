# Read-only: print default audio endpoints for Render/Capture x Console/Multimedia/Communications.
# Does not change any default. ASCII only (Windows PowerShell 5.1 reads BOM-less files as ANSI).
# COM methods are declared with [PreserveSig] so each HRESULT is returned as-is, and PROPVARIANT
# uses the native layout (16 bytes on x86, 24 bytes on x64), the same as src/AutoSwitch.Audio/ComInterop.cs.
$ErrorActionPreference = 'Stop'

$src = @'
using System;
using System.Runtime.InteropServices;

namespace ReadAudioRoles
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr activated);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
    }

    // Native PROPVARIANT layout. Only VT_LPWSTR is read, so the union is two pointer-sized words.
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr data1;
        public IntPtr data2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorComObject { }

    public static class Reader
    {
        private const int ENotFound = unchecked((int)0x80070490);
        private const ushort VtLpwstr = 31;

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        public static string Describe(int dataFlow, int role)
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
            try
            {
                IMMDevice device;
                int hr = enumerator.GetDefaultAudioEndpoint(dataFlow, role, out device);
                if (hr == ENotFound) { return "(no default)"; }
                if (hr < 0) { return "error: GetDefaultAudioEndpoint hr=0x" + hr.ToString("X8"); }
                try
                {
                    string id;
                    hr = device.GetId(out id);
                    if (hr < 0) { return "error: GetId hr=0x" + hr.ToString("X8"); }

                    IPropertyStore store;
                    hr = device.OpenPropertyStore(0, out store);
                    if (hr < 0) { return "id=" + id + " error: OpenPropertyStore hr=0x" + hr.ToString("X8"); }
                    try
                    {
                        PropertyKey key = new PropertyKey();
                        key.fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
                        key.pid = 14;
                        PropVariant value;
                        hr = store.GetValue(ref key, out value);
                        if (hr < 0) { return "id=" + id + " error: GetValue hr=0x" + hr.ToString("X8"); }
                        try
                        {
                            string name = value.vt == VtLpwstr ? Marshal.PtrToStringUni(value.data1) : "(no name)";
                            return "id=" + id + " name=" + name;
                        }
                        finally { PropVariantClear(ref value); }
                    }
                    finally { Marshal.ReleaseComObject(store); }
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
    }
}
'@

Add-Type -TypeDefinition $src -Language CSharp | Out-Null

$flows = @{ 0 = 'Render'; 1 = 'Capture' }
$roles = @{ 0 = 'Console'; 1 = 'Multimedia'; 2 = 'Communications' }

Write-Output ("timestamp: " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
foreach ($f in 0, 1) {
    foreach ($r in 0, 1, 2) {
        $label = $flows[$f] + ' ' + $roles[$r]
        $value = [ReadAudioRoles.Reader]::Describe($f, $r)
        Write-Output ($label.PadRight(22) + ' : ' + $value)
    }
}
