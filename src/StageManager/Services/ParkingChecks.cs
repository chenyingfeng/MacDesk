using System;
using System.Windows.Interop;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Strategies;
namespace StageManager.Services;
internal static class ParkingChecks
{
    internal static void Run()
    {
        var p=new HwndSourceParameters("CampusStage parking fixture") {
            WindowStyle=unchecked((int)0x90000000),ExtendedWindowStyle=0x080800A0,
            PositionX=-18000,PositionY=-18000,Width=400,Height=300};
        using var fixture=new HwndSource(p);
        var h=fixture.Handle;
        Win32Helper.SetAlpha(h,127);
        var ex=Win32.GetWindowExStyleLongPtr(h);
        var window=new WindowsWindow(h);var strategy=new OpacityWindowStrategy();
        for(int i=0;i<16;i++) {
            strategy.Hide(window);strategy.Show(window);
            if(Win32.GetWindowExStyleLongPtr(h)!=ex)
                throw new InvalidOperationException("Parking changed app-owned layered/transparent styles");
            var rect=new Win32.Rect();Win32.GetWindowRect(h,ref rect);
            if(rect.Left!=-18000 || rect.Top!=-18000 || OpacityWindowStrategy.TryGetOriginalPosition(h,out _,out _))
                throw new InvalidOperationException("Rapid parking/restoration lost original position");
        }
        // Simulate a renderer changing its size while outside the visible desktop.
        strategy.Hide(window);
        if(!OpacityWindowStrategy.TryGetOriginalBounds(h,out var saved)||saved.Width!=400||saved.Height!=300)
            throw new InvalidOperationException("Parking failed to remember full rectangle");
        Win32.SetWindowPos(h,IntPtr.Zero,0,0,240,160,Win32.SetWindowPosFlags.IgnoreMove|Win32.SetWindowPosFlags.IgnoreZOrder|Win32.SetWindowPosFlags.DoNotActivate);
        strategy.Hide(window); // Repeated hide must not replace the original rectangle.
        OpacityWindowStrategy.SetRestorePosition(h,-17500,-17400);
        strategy.Show(window);
        var restored=new Win32.Rect();Win32.GetWindowRect(h,ref restored);
        if(restored.Left!=-17500||restored.Top!=-17400||restored.Width!=400||restored.Height!=300
            ||OpacityWindowStrategy.TryGetOriginalBounds(h,out _))
            throw new InvalidOperationException("Restore lost parked dimensions or explicit relocated position");
        // Native minimize must remain intact; showing a scene alone is not a restore request.
        Win32.ShowWindow(h,Win32.SW.SW_SHOWMINNOACTIVE);
        strategy.Hide(window);strategy.Show(window);
        if(!Win32.IsIconic(h) || Win32.GetWindowExStyleLongPtr(h)!=ex)
            throw new InvalidOperationException("Native minimize was automatically undone");
        OpacityWindowStrategy.CleanupWindow(h);
    }
}
