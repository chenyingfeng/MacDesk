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
        // Native minimize must remain intact; showing a scene alone is not a restore request.
        Win32.ShowWindow(h,Win32.SW.SW_SHOWMINNOACTIVE);
        strategy.Hide(window);strategy.Show(window);
        if(!Win32.IsIconic(h) || Win32.GetWindowExStyleLongPtr(h)!=ex)
            throw new InvalidOperationException("Native minimize was automatically undone");
        OpacityWindowStrategy.CleanupWindow(h);
    }
}
