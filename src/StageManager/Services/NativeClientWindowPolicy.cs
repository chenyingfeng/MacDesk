using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;

namespace StageManager.Services;

// Preserve client-owned renderer visibility for the known messenger main roots.
// This does not discover or restore hidden tray windows and does not apply to
// other Qt applications, login surfaces, dialogs or child/helper windows.
internal static class NativeClientWindowPolicy
{
    internal static bool UsesNativeMinimize(IWindow window) =>
        QqWindowPolicy.UsesNativeMinimize(window) || IsWeChatMain(window);

    internal static bool IsWeChatMain(IWindow window)
    {
        if(!WindowRestore.SameWindow(window) || !Win32.IsWindowVisible(window.Handle))return false;
        var ex=Win32.GetWindowExStyleLongPtr(window.Handle);
        bool ordinary=!ex.HasFlag(Win32.WS_EX.WS_EX_TOOLWINDOW)
            && !ex.HasFlag(Win32.WS_EX.WS_EX_NOACTIVATE)
            && !Win32.GetWindowStyleLongPtr(window.Handle).HasFlag(Win32.WS.WS_CHILD);
        var location=window.Location;
        var size=WindowRestoreGeometry.NormalSize(window.Handle,new Size(location.Width,location.Height));
        return IsWeChatMain(window.ProcessName,window.Class,window.Title,
            Win32.GetWindow(window.Handle,Win32.GW.GW_OWNER)==IntPtr.Zero,ordinary,
            size.Width,size.Height,GetDpiForWindow(window.Handle));
    }

    internal static bool IsWeChatMain(string process,string cls,string title,bool ownerless,
        bool ordinary,double physicalWidth,double physicalHeight,uint dpi)
    {
        if(!process.Equals("Weixin",StringComparison.OrdinalIgnoreCase)
            && !process.Equals("WeChat",StringComparison.OrdinalIgnoreCase))return false;
        if(cls!="WeChatMainWndForPC" && !Regex.IsMatch(cls??"",@"\AQt[0-9]+QWindowIcon\z"))return false;
        if(title!="微信" && title!="WeChat" && title!="Weixin")return false;
        if(dpi==0)dpi=96;
        double width=physicalWidth*96/dpi,height=physicalHeight*96/dpi;
        return ownerless && ordinary && width>=480 && height>=300 && width>=height*.95;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);
}
