using System;
using System.Threading.Tasks;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
namespace StageManager.Services;
internal static class WindowRestore
{
    internal static bool SameWindow(IWindow window)
    {
        if(!Win32.IsWindow(window.Handle))return false;
        Win32.GetWindowThreadProcessId(window.Handle,out var pid);
        return pid!=0 && pid==window.ProcessId;
    }
    internal static async Task RunAsync(IWindow window,bool explicitSelection=false)
    {
        if(!SameWindow(window))throw new InvalidOperationException("原窗口已关闭或已被替换，请重新选择应用。");
        WindowIntegrity.RequireControl(window.ProcessId);
        // Explicit selection restores a native-minimized window, never a tray-hidden
        // renderer and never another instance of the client's sign-in UI.
        if(!Win32.IsWindowVisible(window.Handle))
            throw new InvalidOperationException("应用已退到托盘，请从它的系统托盘图标重新打开。");
        if(QqWindowPolicy.IsLoginSurface(window))
            throw new InvalidOperationException("QQ 登录窗口不参与台前调度，请选择已登录的 QQ 主界面。");
        // A visible normal messenger needs no forced show at all. Minimize/restore must
        // pass through its normal system-command handler rather than a visibility flip.
        if(NativeClientWindowPolicy.UsesNativeMinimize(window)) {
            if(window.IsMinimized) RequestNativeRestore(window);
        } else Win32.ShowWindowAsync(window.Handle,Win32.SW.SW_RESTORE);
        for(int i=0;i<80;i++) {
            if(!SameWindow(window))throw new InvalidOperationException("恢复期间窗口已关闭，请重新打开应用。");
            if(Win32.IsWindowVisible(window.Handle) && !Win32.IsIconic(window.Handle))return;
            await Task.Delay(25);
        }
        throw new TimeoutException("应用暂未响应恢复，可稍后再拖出一次。");
    }
    internal static async Task NormalizeAsync(IWindow window)
    {
        if(!SameWindow(window))throw new InvalidOperationException("窗口已关闭。");
        if(!Win32.IsZoomed(window.Handle))return;
        if(NativeClientWindowPolicy.UsesNativeMinimize(window))RequestNativeRestore(window);
        else Win32.ShowWindowAsync(window.Handle,Win32.SW.SW_RESTORE);
        for(int i=0;i<80 && SameWindow(window) && Win32.IsZoomed(window.Handle);i++)await Task.Delay(25);
        if(!SameWindow(window) || Win32.IsZoomed(window.Handle))throw new TimeoutException("应用暂未退出最大化状态。");
    }
    internal static void RequestQqRestore(IWindow window)=>RequestNativeRestore(window);
    internal static void RequestNativeRestore(IWindow window)
    {
        if(!SameWindow(window) || !Win32.IsWindowVisible(window.Handle))
            throw new InvalidOperationException("应用已退到托盘，不能强制显示隐藏窗口。");
        if(!Win32.PostMessage(window.Handle,Win32.WM_SYSCOMMAND,new IntPtr(0xF120),IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),"应用未接受恢复请求。");
    }
}
