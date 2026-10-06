using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using StageManager.Composition;
using StageManager.Native.PInvoke;
namespace StageManager.Services;
internal static class DwmSmokeChecks
{
    // Exercise only this application's off-screen fixtures, never the user's windows.
    internal static string Run()
    {
        var clock=Stopwatch.StartNew();
        bool desktopAssigned=false;
        for(int i=0;i<12;i++) {
            var p=new HwndSourceParameters("CampusStage internal preview check") {
                WindowStyle=unchecked((int)0x90000000),ExtendedWindowStyle=0x08000080,
                PositionX=-20000,PositionY=-20000,Width=400,Height=300
            };
            using var fixture=new HwndSource(p);
            desktopAssigned|=WorkspaceEnvironment.DesktopOf(fixture.Handle)!=Guid.Empty&&WorkspaceEnvironment.IsCurrent(fixture.Handle);
            using var preview=new DwmPreviewSurface(IntPtr.Zero);
            preview.Bind(fixture.Handle);
            if(!preview.Update(new Rect(-19000,-19000,200,150),new Rect(-19000,-19000,200,150)))
                throw new InvalidOperationException("DWM registration/update unavailable in this desktop session.");
            var host = preview.HostHandle;
            var rect = new Win32.Rect();
            if (!Win32.GetWindowRect(host, ref rect) || rect.Left != -19000 || rect.Top != -19000
                || rect.Width != 200 || rect.Height != 150) throw new InvalidOperationException("Preview host escaped card bounds.");
            if (!Win32.GetWindowStyleLongPtr(host).HasFlag((Win32.WS)0x08000000)
                || !Win32.GetWindowExStyleLongPtr(host).HasFlag(Win32.WS_EX.WS_EX_NOACTIVATE)
                || !Win32.GetWindowExStyleLongPtr(host).HasFlag(Win32.WS_EX.WS_EX_TRANSPARENT))
                throw new InvalidOperationException("Preview host can intercept activation/input.");
            if (Win32.SendMessage(host, 0x84, IntPtr.Zero, IntPtr.Zero).ToInt32() != -1
                || Win32.SendMessage(host, 0x21, IntPtr.Zero, IntPtr.Zero).ToInt32() != 3)
                throw new InvalidOperationException("Own preview host lost transparent/nonactivating hit behavior.");
            fixture.Dispose();
            if(preview.Update(new Rect(-19000,-19000,200,150),new Rect(-19000,-19000,200,150)))
                throw new InvalidOperationException("Closed source still shown.");
            if(DwmPreviewSurface.ActiveRelations!=0)throw new InvalidOperationException("DWM thumbnail leaked after source close.");
        }
        CheckSidebarInputRelay();
        ParkingChecks.Run();
        return "PASS: 16 own-window parking/restoration cycles preserve app-owned layered/transparent styles and positions; native minimize stays intact; 12 own-window DWM register/query/update/close cycles; exact card bounds; disabled/transparent/noactivate ghost styles; native ghost hit transparency; direct native pointer callbacks; signed screen-coordinate conversion; wheel delta preservation; no owner-message injection; no leaked thumbnail relationships. OwnToolWindowDesktopId="+(desktopAssigned?"ASSIGNED":"UNASSIGNED_OR_UNAVAILABLE")+" (not an API availability test). ElapsedMs="+clock.ElapsedMilliseconds;
    }
    private static void CheckSidebarInputRelay()
    {
        var p=new HwndSourceParameters("CampusStage internal input owner") {
            WindowStyle=unchecked((int)0x90000000),ExtendedWindowStyle=0x08000080,
            PositionX=-20000,PositionY=-20000,Width=400,Height=300};
        using var owner=new HwndSource(p);
        int ownerMessages=0,down=0,moves=0,up=0,wheel=0;
        Point last=default;bool left=false;int delta=0;
        owner.AddHook((IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled)=> {
            if(m==0x200 || m==0x201 || m==0x202 || m==0x20A){ownerMessages++;handled=true;}
            return IntPtr.Zero;
        });
        using(var preview=new DwmPreviewSurface(owner.Handle,new PreviewPointerInput {
            Down=p=>{down++;last=p;return false;},Move=(p,held)=>{moves++;last=p;left=held;},
            Up=p=>{up++;last=p;},Wheel=d=>{wheel++;delta=d;}
        })) {
            preview.Bind(owner.Handle);
            if(!preview.Update(new Rect(-19900,-19900,200,150),new Rect(-19900,-19900,200,150)))
                throw new InvalidOperationException("Interactive preview unavailable");
            var h=preview.HostHandle;
            if(Win32.GetWindowStyleLongPtr(h).HasFlag((Win32.WS)0x08000000)
                || !Win32.GetWindowExStyleLongPtr(h).HasFlag(Win32.WS_EX.WS_EX_NOACTIVATE)
                || Win32.SendMessage(h,0x84,IntPtr.Zero,IntPtr.Zero).ToInt32()!=1
                || Win32.SendMessage(h,0x21,IntPtr.Zero,IntPtr.Zero).ToInt32()!=3)
                throw new InvalidOperationException("Interactive preview swallows input or activates itself");
            foreach(uint message in new uint[]{0x200,0x201,0x202}) {
                Win32.SendMessage(h,message,new IntPtr(1),new IntPtr(unchecked((int)0xFFFBFFF6)));
                if(last!=new Point(-19910,-19905))
                    throw new InvalidOperationException("Native pointer callback screen coordinates incorrect");
            }
            var wheelPoint=new IntPtr(unchecked((int)0xFFFBFFF6));
            var wheelDelta=new IntPtr(120<<16);
            Win32.SendMessage(h,0x20A,wheelDelta,wheelPoint);
            if(down!=1 || moves!=1 || up!=1 || !left || wheel!=1 || delta!=120 || ownerMessages!=0)
                throw new InvalidOperationException("Pointer callbacks duplicated/dropped or injected owner messages");
        }
        if(DwmPreviewSurface.ActiveRelations!=0)throw new InvalidOperationException("Interactive preview relation leaked");
    }
    internal static void WriteStartupReport() {
        string result;
        try {result=Run();}catch(Exception ex){result="UNAVAILABLE: "+ex.GetType().Name+": "+ex.Message;}
        try {File.WriteAllText(Path.Combine(PortablePreferences.Root,"dwm-check.txt"),result);}catch{}
    }
}
