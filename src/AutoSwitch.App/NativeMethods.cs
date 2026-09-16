using System.Runtime.InteropServices;

namespace AutoSwitch.App;

/// <summary>
/// アイコンハンドルの解放だけに使う。音声操作の COM は Audio 側にある。
/// LibraryImport は AllowUnsafeBlocks を要求する（SYSLIB1062）。製品コードに unsafe を
/// 持ち込まないため DllImport を使う。SYSLIB1054 は既定で情報レベルなのでビルドは割れない。
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);
}
