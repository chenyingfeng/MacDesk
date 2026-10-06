using System;
using System.Runtime.InteropServices;
using System.Windows;
using StageManager.Native.PInvoke;
namespace StageManager.Services;
internal static class WindowRestoreGeometry
{
    // Read only the restored SIZE. WINDOWPLACEMENT positions use workspace coordinates,
    // whereas actual dropped positions use SetWindowPos physical screen coordinates.
    internal static Size NormalSize(IntPtr handle,Size fallback)
    {
        var p=new Placement{Length=(uint)Marshal.SizeOf<Placement>()};
        if(GetWindowPlacement(handle,ref p)) {
            int width=p.Normal.Right-p.Normal.Left,height=p.Normal.Bottom-p.Normal.Top;
            if(width>0 && height>0)return new Size(width,height);
        }
        return fallback;
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativePoint{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Placement {
        public uint Length,Flags,Show;public NativePoint Min,Max;public Win32.Rect Normal;
    }
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetWindowPlacement(IntPtr h,ref Placement p);
}
