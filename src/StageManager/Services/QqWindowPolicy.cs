using System;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
namespace StageManager.Services;
internal static class QqWindowPolicy
{
    private static readonly System.Collections.Generic.Dictionary<int,IntPtr> representatives=new();
    // Visible QQ main-window classification; tray-hidden roots never act as launchers.
    internal static bool IsMain(IWindow window)=>IsMain(window.ProcessName,window.Class,window.Title,
        Win32.GetWindow(window.Handle,Win32.GW.GW_OWNER)==IntPtr.Zero,
        ((long)Win32.GetWindowExStyleLongPtr(window.Handle)&0x08000080L)==0,
        window.Location.Width,window.Location.Height);
    internal static bool IsMain(string process,string cls,string title,bool ownerless,bool ordinary,int width,int height)=>
        process.Equals("QQ",StringComparison.OrdinalIgnoreCase) && cls=="Chrome_WidgetWin_1"
        && title.Trim()=="QQ" && ownerless && ordinary && width>=300 && height>=200;
    internal static bool UsesNativeMinimize(IWindow window)=>
        window.ProcessName.Equals("QQ",StringComparison.OrdinalIgnoreCase) && window.Class=="Chrome_WidgetWin_1";
    // QQ's observed sign-in surface is 320x460 DIPs. Exclude only its exact QQ
    // caption and narrow size range; chats and ordinary main windows stay eligible.
    internal static bool IsLoginSize(double physicalWidth,double physicalHeight,uint dpi)
    {
        if(dpi==0)dpi=96;
        double w=physicalWidth*96/dpi,h=physicalHeight*96/dpi;
        return w>=300 && w<=360 && h>=420 && h<=520;
    }
    internal static bool IsLoginSurface(IWindow window)
    {
        if(!UsesNativeMinimize(window) || window.Title.Trim()!="QQ")return false;
        var r=window.Location;
        var size=WindowRestoreGeometry.NormalSize(window.Handle,new System.Windows.Size(r.Width,r.Height));
        return IsLoginSize(size.Width,size.Height,GetDpiForWindow(window.Handle));
    }
    internal static bool IsLoginSurface(IntPtr handle)
    {
        var text=new System.Text.StringBuilder(256);
        Win32.GetClassName(handle,text,text.Capacity);
        if(text.ToString()!="Chrome_WidgetWin_1")return false;
        text.Clear();Win32.GetWindowText(handle,text,text.Capacity);
        return text.ToString().Trim()=="QQ" && IsLoginSurface(new WindowsWindow(handle));
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);
    internal static bool Retain(IWindow window)=>WindowRestore.SameWindow(window)
        && Win32.IsWindowVisible(window.Handle) && !IsLoginSurface(window);
    internal static void RememberFocused(IWindow window)
    {if(window.IsFocused && IsMain(window))representatives[window.ProcessId]=window.Handle;}
    internal static bool IsLauncher(IWindow window)
    {
        // Closed-to-tray QQ roots are not launchers. Re-running QQ.exe can open a
        // second login surface, so only already visible real windows are tracked.
        if(!Win32.IsWindowVisible(window.Handle) || IsLoginSurface(window) || !IsMain(window))return false;
        if(representatives.TryGetValue(window.ProcessId,out var cached)) {
            var existing=new WindowsWindow(cached);
            if(WindowRestore.SameWindow(existing) && existing.ProcessId==window.ProcessId && IsMain(existing))
                return cached==window.Handle;
        }
        IntPtr chosen=IntPtr.Zero;long area=-1;
        Win32.EnumWindows((h,_)=> {
            Win32.GetWindowThreadProcessId(h,out var pid);
            if(pid!=window.ProcessId)return true;
            var candidate=new WindowsWindow(h);
            if(!IsMain(candidate))return true;
            var r=candidate.Location;long next=(long)r.Width*r.Height;
            if(next>area){chosen=h;area=next;}return true;
        },IntPtr.Zero);
        if(chosen!=IntPtr.Zero)representatives[window.ProcessId]=chosen;
        return chosen==window.Handle;
    }
    internal static bool Discover(IntPtr h)
    {
        if(!Win32.IsWindow(h) || !Win32.IsWindowVisible(h))return false;
        var text=new System.Text.StringBuilder(256);
        Win32.GetClassName(h,text,text.Capacity);
        if(text.ToString()!="Chrome_WidgetWin_1")return false;
        text.Clear();Win32.GetWindowText(h,text,text.Capacity);
        if(text.ToString().Trim()!="QQ")return false;
        return IsLauncher(new WindowsWindow(h));
    }
}
