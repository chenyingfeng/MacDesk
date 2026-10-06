using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
namespace StageManager.Services;
internal sealed class PortableControl : IDisposable
{
    [DllImport("user32.dll", SetLastError=true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    private readonly Window owner;
    private readonly HwndSource source;
    private readonly DispatcherTimer timer;
    private readonly bool hotkeyRegistered;
    private readonly bool restoreHotkeyRegistered;
    private readonly System.Collections.Generic.List<int> featureHotkeys=new();
    private bool disposed;
    private long lastRuntime;
    internal static string RecoveryFile=>Path.Combine(PortablePreferences.Root,"sidebar-recovery.request");
    internal bool HotkeyReady => hotkeyRegistered;
    internal PortableControl(Window window)
    {
        owner=window;
        source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
        source.AddHook(Message);
        hotkeyRegistered=RegisterHotKey(source.Handle, 7141, 0x4007, 0x7B);
        restoreHotkeyRegistered=RegisterHotKey(source.Handle,7142,0x4007,0x7A);
        for(int n=0;n<9;n++)if(RegisterHotKey(source.Handle,7150+n,0x4007,(uint)(0x31+n)))featureHotkeys.Add(7150+n);
        if(RegisterHotKey(source.Handle,7160,0x4007,0x44))featureHotkeys.Add(7160);
        if(RegisterHotKey(source.Handle,7161,0x4007,0x47))featureHotkeys.Add(7161);
        if(RegisterHotKey(source.Handle,7162,0x4007,0x57))featureHotkeys.Add(7162);
        timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(400)};
        timer.Tick += (_, _) => {
            (Application.Current as App)?.UiHealth?.Pulse();
            if(owner is MainWindow runtimeOwner && Environment.TickCount64-lastRuntime>=1000) {
                lastRuntime=Environment.TickCount64;runtimeOwner.WriteRuntimeHeartbeat();
            }
            if(File.Exists(PortablePreferences.StopFile)) {owner.Close();return;}
            if(owner is MainWindow dockOwner) {
                // Files and independently closing shell targets can fail transiently.
                // Unknown UI/model errors still reach the normal fatal restoration path.
                try {DockBridge.Poll(dockOwner);}
                catch(Exception error) when(error is IOException||error is UnauthorizedAccessException
                    ||error is System.Runtime.InteropServices.COMException||error is System.ComponentModel.Win32Exception) {
                    (Application.Current as App)?.UiHealth?.MaintenanceFault("DockBridge",error);
                }
                PollRecovery(dockOwner);
            }
            var request=Path.Combine(PortablePreferences.Root,"groups.request");
            if(File.Exists(request)) {
                try {File.ReadAllText(request);File.Delete(request);}
                catch(IOException){return;}catch(UnauthorizedAccessException){return;}
                if(owner is MainWindow main)main.OpenTaskGroups();
            }
        };
        timer.Start();
    }
    private static void PollRecovery(MainWindow main) {
        try {
            if(!File.Exists(RecoveryFile))return;
            var info=new FileInfo(RecoveryFile);
            if(info.Length>128){File.Delete(RecoveryFile);return;}
            var token=File.ReadAllText(RecoveryFile);File.Delete(RecoveryFile);
            if(RuntimeSnapshotPolicy.RecoveryToken(token,info.LastWriteTimeUtc,DateTime.UtcNow))main.RearmSidebar();
        }catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private IntPtr Message(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if(message==0x312 && w.ToInt32()==7141) {handled=true;owner.Dispatcher.BeginInvoke(new Action(owner.Close));}
        if(message==0x312 && w.ToInt32()==7142) {handled=true;File.WriteAllText(TaskbarGuard.RestoreFile,"1");}
        if(message==0x312 && owner is MainWindow main && featureHotkeys.Contains(w.ToInt32())) {
            handled=true;int id=w.ToInt32();
            owner.Dispatcher.BeginInvoke(new Action(()=> {
                if(id>=7150 && id<=7158)main.SelectGroupByNumber(id-7150);
                else if(id==7160)main.ToggleDesktopFromKeyboard();
                else if(id==7162)main.CycleCurrentTaskWindows();else main.OpenTaskGroups();
            }));
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if(disposed) return; disposed=true; timer.Stop();
        if(hotkeyRegistered) UnregisterHotKey(source.Handle,7141);
        if(restoreHotkeyRegistered) UnregisterHotKey(source.Handle,7142);
        foreach(int id in featureHotkeys)UnregisterHotKey(source.Handle,id);
        source.RemoveHook(Message);
    }
}
