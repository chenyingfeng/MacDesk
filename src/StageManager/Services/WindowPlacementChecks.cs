using System;
using System.Threading.Tasks;
using System.Windows.Interop;
using StageManager.Native;
using StageManager.Native.PInvoke;
namespace StageManager.Services;
internal static class WindowPlacementChecks {
    internal static async Task RunAsync() {
        using var fixture=new HwndSource(new HwndSourceParameters("MacDesk owned placement fixture") {
            WindowStyle=unchecked((int)0x90CF0000),ExtendedWindowStyle=0x08000080,
            PositionX=-19000,PositionY=-19000,Width=640,Height=420
        });
        var window=new WindowsWindow(fixture.Handle);
        Win32.ShowWindow(window.Handle,Win32.SW.SW_SHOWMAXIMIZED);
        if(!Win32.IsZoomed(window.Handle))throw new InvalidOperationException("Placement fixture did not maximize");
        await WindowRestore.RunAsync(window,true);
        if(!Win32.IsZoomed(window.Handle))throw new InvalidOperationException("Selecting maximized window normalized it");
        Win32.ShowWindow(window.Handle,Win32.SW.SW_SHOWMINNOACTIVE);
        if(!Win32.IsIconic(window.Handle)||!WindowRestoreGeometry.RestoreMaximized(window.Handle))
            throw new InvalidOperationException("Minimized fixture lost restore-to-maximized intent");
        await WindowRestore.RunAsync(window,true);
        if(Win32.IsIconic(window.Handle)||!Win32.IsZoomed(window.Handle))
            throw new InvalidOperationException("Native minimize/restore lost maximization");
        await WindowRestore.NormalizeAsync(window);
        if(Win32.IsZoomed(window.Handle)||Win32.IsIconic(window.Handle))throw new InvalidOperationException("Explicit drag normalization stopped working");
        var before=new Win32.Rect();Win32.GetWindowRect(window.Handle,ref before);
        await WindowRestore.RunAsync(window,true);
        var after=new Win32.Rect();Win32.GetWindowRect(window.Handle,ref after);
        if(before.Left!=after.Left||before.Top!=after.Top||before.Width!=after.Width||before.Height!=after.Height)
            throw new InvalidOperationException("Selecting normal window changed its rectangle");
    }
}
