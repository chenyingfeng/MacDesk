using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using StageManager.Native.PInvoke;
using StageManager.Services;
namespace StageManager;
public partial class MainWindow
{
    private string _sidebarMonitor=System.Windows.Forms.Screen.PrimaryScreen!.DeviceName;
    private long _desktopPoll;
    private System.Windows.Forms.Screen SidebarScreen=>WorkspaceEnvironment.ScreenFor(_sidebarMonitor);
    internal string SidebarMonitor=>_sidebarMonitor;
    private void RefreshWorkspaceForPointer(Win32.POINT pointer)
    {
        bool changed=false;
        if(Environment.TickCount64-_desktopPoll>=400) {
            _desktopPoll=Environment.TickCount64;
            changed=WorkspaceEnvironment.Refresh(new WindowInteropHelper(this).Handle);
        }
        var monitor=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pointer.X,pointer.Y)).DeviceName;
        if(monitor!=_sidebarMonitor||changed) {
            Mode=WindowMode.OffScreen;_sidebarMonitor=monitor;
            SceneManager.SelectWorkspace(monitor);
            _edgeArmed=true;_edgeHoverSince=DateTime.MinValue;
            SyncVisibilityByUpdatedTimeStamp();PlaceSidebarNative();
        }
    }
    private void PlaceSidebarNative()
    {
        if(!IsLoaded)return;
        var h=new WindowInteropHelper(this).Handle;if(h==IntPtr.Zero)return;
        var screen=SidebarScreen;double scale=GetMonitorScale(screen);
        int width=(int)Math.Ceiling(Math.Max(48,Width)*scale);
        int left=Mode==WindowMode.OffScreen?System.Windows.Forms.SystemInformation.VirtualScreen.Right+40:screen.WorkingArea.Right-width;
        Win32.SetWindowPos(h,IntPtr.Zero,left,screen.WorkingArea.Top,width,screen.WorkingArea.Height,
            Win32.SetWindowPosFlags.IgnoreZOrder|Win32.SetWindowPosFlags.DoNotActivate);
    }
    internal static double GetMonitorScale(System.Windows.Forms.Screen screen) {
        try {
            var point=new NativePoint {X=screen.Bounds.Left+screen.Bounds.Width/2,Y=screen.Bounds.Top+screen.Bounds.Height/2};
            if(GetDpiForMonitor(MonitorFromPoint(point,2),0,out uint x,out _) == 0)return x/96.0;
        }catch{}
        return 1;
    }
    public bool WindowMotion {
        get=>!PortablePreferences.Read("disable-window-motion");
        set {PortablePreferences.Write("disable-window-motion",!value);RaisePropertyChanged();}
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativePoint {internal int X,Y;}
    [DllImport("user32.dll")]private static extern IntPtr MonitorFromPoint(NativePoint point,uint flags);
    [DllImport("shcore.dll")]private static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
}
