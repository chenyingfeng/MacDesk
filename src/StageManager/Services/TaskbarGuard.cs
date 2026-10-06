using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StageManager.Services;

// A separate watcher owns the lease and restores it even if the sidebar process crashes.
internal sealed class TaskbarGuard : IDisposable
{
    private Process? child;
    private readonly EventWaitHandle stop;
    private readonly string token=Guid.NewGuid().ToString("N");
    internal static string RestoreFile => Path.Combine(AppContext.BaseDirectory,"taskbar-restore.request");
    internal static string PeekFile => Path.Combine(AppContext.BaseDirectory,"taskbar-peek.request");
    internal static string ToggleFile => Path.Combine(AppContext.BaseDirectory,"taskbar-toggle.request");
    private static string EventName(string token,string kind)=>"Local\\CampusStage.Taskbar."+token+"."+kind;
    private TaskbarGuard(){stop=new EventWaitHandle(false,EventResetMode.ManualReset,EventName(token,"stop"));}
    internal static async Task<TaskbarGuard?> StartAsync()
    {
        if(!PortablePreferences.Read("hide-taskbar"))return null;
        var guard=new TaskbarGuard();
        try {
            if(File.Exists(RestoreFile))File.Delete(RestoreFile);
            if(File.Exists(ToggleFile))File.Delete(ToggleFile);
            if(File.Exists(PeekFile))File.Delete(PeekFile);
            TaskbarToggleQueue.Consume(AppContext.BaseDirectory,()=>{},true);
            using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,EventName(guard.token,"ready"));
            using var parent=Process.GetCurrentProcess();
            var info=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true,
                WorkingDirectory=AppContext.BaseDirectory};
            info.ArgumentList.Add("--taskbar-guard");info.ArgumentList.Add(parent.Id.ToString());
            info.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString());info.ArgumentList.Add(guard.token);
            guard.child=Process.Start(info);
            for(int i=0;i<40;i++) {
                if(ready.WaitOne(0))return guard;
                if(guard.child==null || guard.child.HasExited)break;
                await Task.Delay(100);
            }
        } catch { }
        guard.Dispose();return null;
    }
    public void Dispose()
    {
        try {stop.Set();if(child!=null && !child.HasExited)child.WaitForExit(3000);}catch{}
        child?.Dispose();child=null;stop.Dispose();
    }
    internal static int Run(string[] args)
    {
        TaskbarVisibilityLease? lease=null;
        string state="unavailable";
        try {
            if(args.Length!=4 || !int.TryParse(args[1],out int id) || !long.TryParse(args[2],out long ticks)
                || !Guid.TryParseExact(args[3],"N",out _))return 2;
            using var parent=Process.GetProcessById(id);
            if(parent.StartTime.ToUniversalTime().Ticks!=ticks || parent.HasExited
                || !string.Equals(parent.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))return 2;
            using var stop=EventWaitHandle.OpenExisting(EventName(args[3],"stop"));
            using var ready=EventWaitHandle.OpenExisting(EventName(args[3],"ready"));
            lease=new TaskbarVisibilityLease(Enumerate,Valid,IsWindowVisible,SetVisible);
            // A stop during launch must never hide taskbars after the parent has given up.
            if(stop.WaitOne(0) || parent.HasExited)return 0;
            lease.Refresh();state="hidden";ready.Set();
            var peek=new TaskbarRevealPolicy();long lastStatus=0,lastHealth=0;string lastState="";bool uiRecovery=false;
            while(!stop.WaitOne(100) && !parent.HasExited && !File.Exists(RestoreFile)) {
                TaskbarToggleQueue.Consume(AppContext.BaseDirectory,()=>peek.Toggle(Environment.TickCount64),false);
                if(File.Exists(ToggleFile)) {
                    try {bool fresh=DateTime.UtcNow-File.GetLastWriteTimeUtc(ToggleFile)<TimeSpan.FromSeconds(5);
                        var token=File.ReadAllText(ToggleFile);File.Delete(ToggleFile);
                        if(fresh&&Guid.TryParseExact(token,"N",out _))peek.Toggle(Environment.TickCount64);
                    }catch(IOException){}catch(UnauthorizedAccessException){}
                }
                if(File.Exists(PeekFile)) {
                    try {bool fresh=DateTime.UtcNow-File.GetLastWriteTimeUtc(PeekFile)<TimeSpan.FromSeconds(30);
                        File.ReadAllText(PeekFile);File.Delete(PeekFile);if(fresh)peek.Request(Environment.TickCount64);}
                    catch(IOException){}catch(UnauthorizedAccessException){}
                }
                CursorState(out bool edge,out bool inside,out bool shell);
                // The companion owns the bottom edge while its heartbeat is fresh.
                try {var heartbeat=Path.Combine(AppContext.BaseDirectory,"mac-dock.status");
                    if(File.Exists(heartbeat)&&DateTime.UtcNow-File.GetLastWriteTimeUtc(heartbeat)<TimeSpan.FromSeconds(5))edge=false;
                }catch{}
                if(Environment.TickCount64-lastHealth>=1000) {
                    lastHealth=Environment.TickCount64;
                    try {uiRecovery=!RuntimeSnapshotPolicy.UiResponsive(File.ReadAllText(StageUiHealth.PathName),id,DateTime.UtcNow);}
                    catch(IOException){uiRecovery=true;}catch(UnauthorizedAccessException){uiRecovery=true;}
                }
                // Keep shell entry points reachable if the parent UI loop stops responding.
                // This only restores our taskbar lease; no app is killed/restarted/elevated.
                bool reveal=uiRecovery||peek.Update(edge,inside,shell,Environment.TickCount64);
                lease.Refresh(reveal);string nextState=reveal?"peek":"hidden";
                if(nextState!=lastState||Environment.TickCount64-lastStatus>=1000) {
                    WriteStatus(nextState,lease.HiddenCount,uiRecovery);lastState=nextState;lastStatus=Environment.TickCount64;
                }
            }
            state="restored";return 0;
        }catch{state="error";return 1;}
        finally {
            if(lease!=null) {
                lease.Dispose();
                for(int i=0;i<30 && !lease.RestorationComplete;i++){Thread.Sleep(50);lease.RetryRestore();}
                if(!lease.RestorationComplete)state="restore-incomplete";
            }
            WriteStatus(state,0);
        }
    }
    private static void CursorState(out bool edge,out bool inside,out bool shell)
    {
        edge=false;inside=false;shell=false;
        if(!GetCursorPos(out var point))return;
        foreach(var t in Enumerate()) {
            if(!GetWindowRect(t.Handle,out var r))continue;
            var screen=System.Windows.Forms.Screen.FromHandle(t.Handle).Bounds;
            if(r.Right-r.Left>r.Bottom-r.Top) {
                bool along=point.X>=r.Left && point.X<r.Right;
                bool bottom=r.Top>=screen.Top+screen.Height/2;
                edge|=along && (bottom?point.Y>=screen.Bottom-4 && point.Y<screen.Bottom:point.Y>=screen.Top && point.Y<screen.Top+4);
            } else {
                bool along=point.Y>=r.Top && point.Y<r.Bottom;
                bool right=r.Left>=screen.Left+screen.Width/2;
                edge|=along && (right?point.X>=screen.Right-4 && point.X<screen.Right:point.X>=screen.Left && point.X<screen.Left+4);
            }
            inside|=IsWindowVisible(t.Handle) && point.X>=r.Left && point.X<r.Right && point.Y>=r.Top && point.Y<r.Bottom;
        }
        shell=ShellSurfaceAt(WindowFromPoint(point))||ShellSurfaceAt(GetForegroundWindow());
        if(WorkspaceEnvironment.IsFullscreenNative(GetForegroundWindow())){edge=false;inside=false;shell=false;}
    }
    private static bool ShellSurfaceAt(IntPtr handle)
    {
        handle=GetAncestor(handle,2);GetWindowThreadProcessId(handle,out var pid);
        if(pid==0||!IsWindowVisible(handle)||WorkspaceEnvironment.IsCloaked(handle))return false;
        try {
            using var process=Process.GetProcessById((int)pid);
            if(process.ProcessName.Equals("StartMenuExperienceHost",StringComparison.OrdinalIgnoreCase)
                ||process.ProcessName.Equals("SearchHost",StringComparison.OrdinalIgnoreCase))return true;
            var cls=new StringBuilder(256);GetClassName(handle,cls,cls.Capacity);
            return SystemSelectionPolicy.IsSelector(process.ProcessName,cls.ToString())
                ||process.ProcessName.Equals("explorer",StringComparison.OrdinalIgnoreCase)
                && (cls.ToString().Contains("Tray",StringComparison.OrdinalIgnoreCase)
                    ||cls.ToString().Contains("Overflow",StringComparison.OrdinalIgnoreCase));
        }catch{return false;}
    }
    private static void WriteStatus(string state,int count,bool uiRecovery=false)
    {
        try {var path=Path.Combine(AppContext.BaseDirectory,"taskbar-status.ini");
            File.WriteAllText(path+".pending",$"State={state}\nHiddenTaskbars={count}\nUiRecovery={uiRecovery}\nRegistryChanged=False\nAppbarSettingsChanged=False\nUtc={DateTime.UtcNow:O}\n");
            File.Move(path+".pending",path,true);
        }catch{}
    }
    private static IEnumerable<TaskbarVisibilityLease.Target> Enumerate()
    {
        var targets=new List<TaskbarVisibilityLease.Target>();
        EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out var pid);var t=new TaskbarVisibilityLease.Target(h,pid);
            if(Valid(t))targets.Add(t);return true;},IntPtr.Zero);
        return targets;
    }
    private static bool Valid(TaskbarVisibilityLease.Target t)
    {
        GetWindowThreadProcessId(t.Handle,out var pid);
        if(pid==0 || pid!=t.ProcessId)return false;
        var text=new StringBuilder(256);GetClassName(t.Handle,text,text.Capacity);
        if(text.ToString()!="Shell_TrayWnd" && text.ToString()!="Shell_SecondaryTrayWnd")return false;
        try {using var p=Process.GetProcessById((int)pid);return p.ProcessName.Equals("explorer",StringComparison.OrdinalIgnoreCase);}catch{return false;}
    }
    private static bool SetVisible(IntPtr h,bool show)=>SetWindowPos(h,IntPtr.Zero,0,0,0,0,
        0x4000|0x0010|0x0004|0x0002|0x0001|(show?0x0040u:0x0080u));
    private delegate bool EnumProc(IntPtr h,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumProc proc,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr h,StringBuilder text,int count);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
    [StructLayout(LayoutKind.Sequential)]private struct Point {internal int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect {internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr handle,out Rect rect);
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr handle,uint flags);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
}
