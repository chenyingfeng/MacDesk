using System;
using System.Runtime.InteropServices;
using System.Windows;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Strategies;
namespace StageManager.Services;
internal static class WorkspaceEnvironment
{
    [ComImport,Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager {
        [PreserveSig]int IsWindowOnCurrentVirtualDesktop(IntPtr h,[MarshalAs(UnmanagedType.Bool)]out bool current);
        [PreserveSig]int GetWindowDesktopId(IntPtr h,out Guid id);
        [PreserveSig]int MoveWindowToDesktop(IntPtr h,ref Guid id);
    }
    private static IVirtualDesktopManager? manager;
    private static bool attempted;
    private static long managerAttempt;
    private static readonly Lazy<DesktopQueryWorker> queryWorker=new(()=>new DesktopQueryWorker(QueryWindow,QueryCurrentDesktop,
        (h,id)=>{try{if(Manager is { } service)ForgetDisconnectedManager(service.MoveWindowToDesktop(h,ref id));}
            catch(COMException error){ForgetDisconnectedManager(error.HResult);}},PidOf));
    private static DesktopQueryWorker Queries=>queryWorker.Value;
    internal static Guid CurrentDesktop {get;private set;}
    internal static string ActiveMonitor {get;set;}=System.Windows.Forms.Screen.PrimaryScreen!.DeviceName;
    private static IVirtualDesktopManager? Manager {
        get {
            if(!attempted||manager==null&&Environment.TickCount64-managerAttempt>=2000) {
                attempted=true;managerAttempt=Environment.TickCount64;
                try {manager=(IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"))!)!;}
                catch(COMException){}catch(InvalidCastException){}
            }
            return manager;
        }
    }
    internal static Guid DesktopOf(IntPtr h) {
        var pid=PidOf(h);Queries.Request(h,pid);
        return Queries.Cached(h,pid)?.Desktop??Guid.Empty;
    }
    internal static bool IsCurrent(IntPtr h) {
        if(h==IntPtr.Zero||IsCloaked(h))return false;
        var pid=PidOf(h);if(pid<=0)return false;Queries.Request(h,pid);
        return Queries.Cached(h,pid)?.Current??true;
    }
    internal static bool IsCloaked(IntPtr h) {
        try {return DwmGetWindowAttribute(h,14,out int cloaked,4)==0&&cloaked!=0;}catch{return false;}
    }
    internal static bool Refresh(IntPtr ownWindow) {
        Queries.RefreshCurrent();var id=Queries.CurrentDesktop;
        bool changed=id!=CurrentDesktop;CurrentDesktop=id;
        if(ownWindow!=IntPtr.Zero&&id!=Guid.Empty&&!IsCurrent(ownWindow))Queries.MoveOwned(ownWindow,id);
        return changed;
    }
    // Executed exclusively on the metadata STA. Never called by WPF or the guard.
    private static DesktopQueryResult QueryWindow(IntPtr h) {
        Guid id=Guid.Empty;bool? current=null;
        try {if(Manager is { } service){int hr=service.GetWindowDesktopId(h,out var answer);if(hr==0)id=answer;else ForgetDisconnectedManager(hr);}}
        catch(COMException error){ForgetDisconnectedManager(error.HResult);}
        try {if(Manager is { } service){int hr=service.IsWindowOnCurrentVirtualDesktop(h,out var answer);if(hr==0)current=answer;else ForgetDisconnectedManager(hr);}}
        catch(COMException error){ForgetDisconnectedManager(error.HResult);}
        return new DesktopQueryResult(id,current);
    }
    private static void ForgetDisconnectedManager(int hr) {
        if(hr==unchecked((int)0x80010108)||hr==unchecked((int)0x800706BA)){
            manager=null;attempted=true;managerAttempt=Environment.TickCount64;
        }
    }
    private static Guid QueryCurrentDesktop() {
        var foreground=Win32.GetForegroundWindow();var queried=QueryWindow(foreground);
        var id=queried.Current!=false?queried.Desktop:Guid.Empty;
        if(id==Guid.Empty && Manager!=null) {
            // Tool/owned windows can have S_OK with GUID_NULL. Query ordinary visible
            // windows on the current desktop instead; never create a taskbar probe.
            Guid ordinary=Guid.Empty;
            Win32.EnumWindows((handle,_)=> {
                if(!Win32.IsWindowVisible(handle)||IsCloaked(handle))return true;
                var candidate=QueryWindow(handle);
                if(candidate.Desktop==Guid.Empty||candidate.Current!=true)return true;
                ordinary=candidate.Desktop;return false;
            },IntPtr.Zero);
            id=ordinary;
        }
        return id;
    }
    internal static void AttachOwnPreview(IntPtr handle) {
        if(CurrentDesktop==Guid.Empty||IsCurrent(handle))return;
        Win32.GetWindowThreadProcessId(handle,out var pid);if(pid!=Environment.ProcessId)return;
        Queries.MoveOwned(handle,CurrentDesktop);
    }
    internal static System.Windows.Forms.Screen ScreenFor(string name)=>
        Array.Find(System.Windows.Forms.Screen.AllScreens,s=>s.DeviceName==name)??System.Windows.Forms.Screen.PrimaryScreen!;
    internal static string MonitorOf(IWindow w) {
        var r=WorkspaceWindowGeometry.Bounds(w);
        if(OpacityWindowStrategy.TryGetOriginalPosition(w.Handle,out int x,out int y))r=new Rect(x,y,r.Width,r.Height);
        return System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle((int)r.X,(int)r.Y,(int)r.Width,(int)r.Height)).DeviceName;
    }
    internal static string Key(string monitor,Guid desktop)=>desktop.ToString("N")+"|"+monitor;
    internal static string ActiveKey=>Key(ActiveMonitor,CurrentDesktop);
    internal static string WindowKey(IWindow w)=>Key(MonitorOf(w),DesktopOf(w.Handle));
    internal static string MonitorFromKey(string key)=>key.Contains('|')?key[(key.IndexOf('|')+1)..]:ActiveMonitor;
    internal static bool CoversDisplay(Rect window,Rect display,bool caption,bool minimized)=>!caption&&!minimized
        && Math.Abs(window.Left-display.Left)<=3&&Math.Abs(window.Top-display.Top)<=3
        && window.Right>=display.Right-3&&window.Bottom>=display.Bottom-3;
    internal static bool IsFullscreen(IntPtr h) => IsFullscreenNative(h)&&IsCurrent(h);
    internal static bool IsFullscreenNative(IntPtr h) {
        if(h==IntPtr.Zero||!Win32.IsWindowVisible(h)||Win32.IsIconic(h)||IsCloaked(h))return false;
        var r=new Win32.Rect();if(!Win32.GetWindowRect(h,ref r))return false;
        var b=System.Windows.Forms.Screen.FromHandle(h).Bounds;
        bool caption=((uint)Win32.GetWindowStyleLongPtr(h)&0x00C00000u)!=0;
        return CoversDisplay(new Rect(r.Left,r.Top,r.Width,r.Height),new Rect(b.X,b.Y,b.Width,b.Height),caption,false);
    }
    internal static bool ForegroundFullscreen(string monitor)=>IsFullscreen(Win32.GetForegroundWindow())
        && System.Windows.Forms.Screen.FromHandle(Win32.GetForegroundWindow()).DeviceName==monitor;
    internal static bool DesktopQueryResponsive=>Queries.CurrentFresh;
    internal static int DesktopQueriesPending=>Queries.PendingCount;
    internal static void ShutdownQueries(){if(queryWorker.IsValueCreated)queryWorker.Value.Dispose();}
    private static int PidOf(IntPtr h){Win32.GetWindowThreadProcessId(h,out var pid);return (int)pid;}
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(IntPtr h,int attribute,out int value,int size);
}
