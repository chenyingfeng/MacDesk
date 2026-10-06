using System;
using System.Runtime.InteropServices;
using System.Windows;
using StageManager.Native.Window;
namespace StageManager.Services;
internal static class WorkspaceWindowGeometry
{
    internal static Rect Bounds(IWindow w)
    {
        var location=w.Location;
        var fallback=new Rect(location.X,location.Y,Math.Max(1,location.Width),Math.Max(1,location.Height));
        if(!w.IsMinimized && !w.IsMaximized)return fallback;
        var p=new Placement {Length=Marshal.SizeOf<Placement>()};
        if(!GetWindowPlacement(w.Handle,ref p)||p.Normal.Right<=p.Normal.Left||p.Normal.Bottom<=p.Normal.Top)return fallback;
        // WINDOWPLACEMENT ordinary windows use workspace, whereas SetWindowPos uses screen.
        var screen=System.Windows.Forms.Screen.FromHandle(w.Handle);
        int x=p.Normal.Left+screen.WorkingArea.Left-screen.Bounds.Left;
        int y=p.Normal.Top+screen.WorkingArea.Top-screen.Bounds.Top;
        return new Rect(x,y,p.Normal.Right-p.Normal.Left,p.Normal.Bottom-p.Normal.Top);
    }
    [StructLayout(LayoutKind.Sequential)]private struct Point {internal int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Rectangle {internal int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct Placement {internal int Length,Flags,Show;internal Point Min,Max;internal Rectangle Normal;}
    [DllImport("user32.dll")]private static extern bool GetWindowPlacement(IntPtr h,ref Placement placement);
}
