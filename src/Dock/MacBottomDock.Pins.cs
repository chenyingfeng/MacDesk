using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

internal sealed partial class MacBottomDock {
    sealed class PendingPinLaunch {
        internal long Generation;
        internal readonly Stopwatch Age=Stopwatch.StartNew();
    }
    sealed class RecoveryPresentation {
        internal PendingPinLaunch Operation;
        internal readonly Stopwatch Age=Stopwatch.StartNew();
        internal readonly string Token=Guid.NewGuid().ToString("N");
        internal int StagePid;
        internal long QueueStamp,ManualEpoch,CanceledAt=-1;
        internal DateTime StartedUtc;
        internal bool OwnsPeek,Completed,Shown,CloseQueued;
        internal long CompletedAt,CloseQueuedAt,Handle;
        internal int WindowPid;
    }
    RecoveryPresentation recoveryPresentation;
    long manualTaskbarEpoch;
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetForegroundWindow")]
    static extern IntPtr RecoveryForegroundWindow();
    static string RecoveryField(string text,string name) {
        string prefix=name+"=";foreach(string line in text.Split('\n'))if(line.StartsWith(prefix,StringComparison.Ordinal))return line.Substring(prefix.Length).Trim();
        return "";
    }
    static DateTime RecoveryUtc(string text) {
        DateTime utc;return DateTime.TryParse(RecoveryField(text,"Utc"),null,System.Globalization.DateTimeStyles.RoundtripKind,out utc)?utc.ToUniversalTime():DateTime.MinValue;
    }
    static bool RecoveryFresh(string text,DateTime now) {
        return Math.Abs((now-RecoveryUtc(text)).TotalSeconds)<=4;
    }
    int RecoveryStagePid() {
        try {string text=File.ReadAllText(Path.Combine(stageRoot,"runtime-status.ini"));int pid;
            return RecoveryFresh(text,DateTime.UtcNow)&&RecoveryField(text,"State")=="running"&&Int32.TryParse(RecoveryField(text,"Pid"),out pid)&&pid>0?pid:0;
        }catch{return 0;}
    }
    string RecoveryTaskbarStatus() {
        try{return File.ReadAllText(Path.Combine(stageRoot,"taskbar-status.ini"));}catch{return "";}
    }
    long RecoveryToggleStamp() {
        string folder=Path.Combine(stageRoot,"taskbar-toggles");return Directory.Exists(folder)?Directory.GetLastWriteTimeUtc(folder).Ticks:0;
    }
    bool RecoveryRequestsPending() {
        try {string folder=Path.Combine(stageRoot,"taskbar-toggles");return File.Exists(Path.Combine(stageRoot,"taskbar-toggle.request"))
            ||Directory.Exists(folder)&&Directory.GetFiles(folder,"*.toggle").Length!=0;
        }catch{return true;}
    }
    static bool OwnRecoveryPeek(bool freshHidden,bool uiRecovery,int stagePid,bool peekPending,bool togglePending) {
        return freshHidden&&!uiRecovery&&stagePid>0&&!peekPending&&!togglePending;
    }
    static bool CloseRecoveryPeek(bool owned,bool current,bool shown,bool foreground,bool sameStage,bool freshPeek,bool uiRecovery,bool peekPending,bool toggleChanged,bool togglePending) {
        return owned&&current&&shown&&foreground&&sameStage&&freshPeek&&!uiRecovery&&!peekPending&&!toggleChanged&&!togglePending;
    }
    static bool TransferRecoveryPeek(bool owned,bool closing,bool manualCurrent,bool sameStage,bool sameQueue,bool togglePending,bool withinDeadline) {
        return owned&&!closing&&manualCurrent&&sameStage&&sameQueue&&!togglePending&&withinDeadline;
    }
    void RecordRecoveryPresentation(RecoveryPresentation presentation,string phase) {
        try {DockRuntime.AtomicText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dock-recovery-presentation.status"),
            "Token="+presentation.Token+"\nPhase="+phase+"\nOwnsPeek="+presentation.OwnsPeek+"\nCloseQueued="+presentation.CloseQueued+"\nElapsedMs="+presentation.Age.ElapsedMilliseconds+"\nUtc="+DateTime.UtcNow.ToString("o"));}catch{}
    }
    void BeginRecoveryPresentation(PendingPinLaunch operation) {
        string status=RecoveryTaskbarStatus();int pid=RecoveryStagePid();
        var presentation=new RecoveryPresentation {Operation=operation,StagePid=pid,StartedUtc=DateTime.UtcNow,ManualEpoch=Interlocked.Read(ref manualTaskbarEpoch)};
        bool transferred=false;
        try {presentation.QueueStamp=RecoveryToggleStamp();bool togglePending=RecoveryRequestsPending();var prior=recoveryPresentation;
            transferred=prior!=null&&TransferRecoveryPeek(prior.OwnsPeek,prior.CloseQueued,prior.ManualEpoch==presentation.ManualEpoch,
                prior.StagePid==pid&&pid>0,prior.QueueStamp==presentation.QueueStamp,togglePending,prior.Age.ElapsedMilliseconds<8000);
            presentation.OwnsPeek=transferred||OwnRecoveryPeek(
            RecoveryFresh(status,DateTime.UtcNow)&&RecoveryField(status,"State")=="hidden",RecoveryField(status,"UiRecovery")!="False",pid,
            File.Exists(Path.Combine(stageRoot,"taskbar-peek.request")),togglePending);
            if(transferred)presentation.StartedUtc=prior.StartedUtc;
        }catch{presentation.OwnsPeek=false;}
        recoveryPresentation=presentation;RecordRecoveryPresentation(presentation,transferred?"transfer-internal-recovery":presentation.OwnsPeek?"internal-recovery":"existing-system-entry");
    }
    void ReleaseRecoveryPresentation(string phase,bool cancelQueuedClose=false) {
        var presentation=recoveryPresentation;if(presentation==null)return;
        if(cancelQueuedClose&&presentation.CloseQueued) {
            try {string file=Path.Combine(stageRoot,"taskbar-toggles",presentation.Token+".toggle");
                if(File.Exists(file)&&File.ReadAllText(file)==presentation.Token)File.Delete(file);
            }catch(Exception error){DockRuntime.Record(AppDomain.CurrentDomain.BaseDirectory,"recovery-dismiss-cancel",error);}
        }
        recoveryPresentation=null;RecordRecoveryPresentation(presentation,phase);
    }
    void CompleteRecoveryPresentation(PendingPinLaunch operation,DockTrayRecovery.Result restored) {
        if(Dispatcher.HasShutdownStarted)return;
        Dispatcher.BeginInvoke(new Action(delegate {
            var presentation=recoveryPresentation;if(presentation==null||!Object.ReferenceEquals(presentation.Operation,operation))return;
            presentation.Completed=true;presentation.CompletedAt=presentation.Age.ElapsedMilliseconds;
            presentation.Shown=restored.Shown;presentation.Handle=restored.Handle;presentation.WindowPid=restored.Pid;
            if(!presentation.OwnsPeek)ReleaseRecoveryPresentation("existing-system-entry");
            else if(!restored.Shown&&Interlocked.Read(ref activationGeneration)==Interlocked.Read(ref operation.Generation))ReleaseRecoveryPresentation("manual-system-entry");
            else RecordRecoveryPresentation(presentation,"restored-waiting-dismiss");
        }));
    }
    bool RecoveryKeepsDockVisible(bool freshPeek) {
        var presentation=recoveryPresentation;if(presentation==null||!presentation.OwnsPeek)return false;
        bool manualCurrent=Interlocked.Read(ref manualTaskbarEpoch)==presentation.ManualEpoch;
        if(closed||Dispatcher.HasShutdownStarted||!manualCurrent||presentation.Age.ElapsedMilliseconds>=8000){ReleaseRecoveryPresentation(manualCurrent?"lease-expired":"user-system-entry",!manualCurrent);return false;}
        bool canceled=Interlocked.Read(ref activationGeneration)!=Interlocked.Read(ref presentation.Operation.Generation);
        if(canceled&&presentation.CanceledAt<0)presentation.CanceledAt=presentation.Age.ElapsedMilliseconds;
        if(!presentation.Completed&&!canceled)return true;
        string status=RecoveryTaskbarStatus();
        if(presentation.CloseQueued) {
            if(RecoveryFresh(status,DateTime.UtcNow)&&RecoveryField(status,"State")=="hidden"){ReleaseRecoveryPresentation("system-entry-hidden");return false;}
            if(presentation.Age.ElapsedMilliseconds-presentation.CloseQueuedAt<2000)return true;
            ReleaseRecoveryPresentation("dismiss-ack-timeout");return false;
        }
        try {
            bool foreground=NativeDockIcons.LiveWindow(presentation.Handle,presentation.WindowPid)
                &&RecoveryForegroundWindow()==new IntPtr(presentation.Handle);
            bool changed=RecoveryToggleStamp()!=presentation.QueueStamp;
            // Cancellation has already invalidated the worker's native action. Its
            // private peek can be cleaned without moving the user's newer foreground.
            bool canClose=CloseRecoveryPeek(presentation.OwnsPeek,manualCurrent,canceled||presentation.Shown,canceled||foreground,RecoveryStagePid()==presentation.StagePid,
                freshPeek&&DockRuntime.FreshPeek(status,DateTime.UtcNow)&&RecoveryUtc(status)>=presentation.StartedUtc,RecoveryField(status,"UiRecovery")!="False",
                File.Exists(Path.Combine(stageRoot,"taskbar-peek.request")),changed,RecoveryRequestsPending());
            if(canClose) {
                // The guard's existing queue dismisses an active peek; never send a
                // toggle against a hidden taskbar or a user's newer manual entry.
                string folder=Path.Combine(stageRoot,"taskbar-toggles");Directory.CreateDirectory(folder);
                string file=Path.Combine(folder,presentation.Token+".toggle");File.WriteAllText(file+".pending",presentation.Token);File.Move(file+".pending",file);
                presentation.CloseQueued=true;presentation.CloseQueuedAt=presentation.Age.ElapsedMilliseconds;
                RecordRecoveryPresentation(presentation,canceled?"dismiss-canceled-private-entry":"dismiss-own-system-entry");return true;
            }
            if(changed){ReleaseRecoveryPresentation("user-system-toggle");return false;}
        }catch(Exception error){DockRuntime.Record(AppDomain.CurrentDomain.BaseDirectory,"recovery-presentation",error);ReleaseRecoveryPresentation("dismiss-check-failed");return false;}
        if(presentation.Age.ElapsedMilliseconds-(canceled?presentation.CanceledAt:presentation.CompletedAt)<1000)return true;
        ReleaseRecoveryPresentation("dismiss-preconditions-unconfirmed");return false;
    }
    readonly System.Collections.Generic.Dictionary<string,PendingPinLaunch> pendingPinLaunches=new System.Collections.Generic.Dictionary<string,PendingPinLaunch>();
    bool RecoveryCurrent(PendingPinLaunch operation) {
        return !closed&&!Dispatcher.HasShutdownStarted&&operation.Age.ElapsedMilliseconds<5000
            &&Interlocked.Read(ref activationGeneration)==Interlocked.Read(ref operation.Generation);
    }
    void RecordPinRecovery(PendingPinLaunch operation,string phase,string route) {
        if(Interlocked.Read(ref activationGeneration)!=Interlocked.Read(ref operation.Generation))return;
        try{DockRuntime.AtomicText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dock-pin-activation.status"),
            "Version=4.7.7.0\nPhase="+phase+"\nState="+(operation.Age.ElapsedMilliseconds>=5000?"expired":"current")+"\nRoute="+route+"\nElapsedMs="+operation.Age.ElapsedMilliseconds+"\nUtc="+DateTime.UtcNow.ToString("o"));}catch{}
    }
    internal void ShowPinPicker() {
        if(!String.IsNullOrEmpty(pins.LoadError)){MessageBox.Show(pins.LoadError,"固定入口");return;}
        menuOpen=true;ResetHover();
        try {new DockPinPicker(pins,apps){Owner=this}.ShowDialog();}
        finally {menuOpen=false;if(!closed&&!Dispatcher.HasShutdownStarted){RenderApps();Position();}}
    }
    void AddPinnedEntry(DockPin pin,MacDockApp[] targets) {
        string iconPath=pin.Kind=="web"?null:pin.Target;
        var button=AddButton(pin.Name,iconPath==null?null:AppIcon(iconPath,targets.FirstOrDefault()),"↗",targets.Length>0,targets.Any(a=>a.Active),delegate {
            RefreshClickSnapshot();
            var current=apps.Where(a=>DockPinStore.Matches(pin,a)&&NativeDockIcons.LiveWindow(a.Handle,a.Pid)).ToArray();
            if(current.Length>0){Activate("pin:"+pin.Id,current);return;}LaunchPin(pin);
        },iconPath);
        if(targets.Length>0)visuals[visuals.Count-1].Key=targets[0].Key;
        var menu=new ContextMenu();menu.Opened+=delegate {
            menuOpen=true;Magnify(Double.NaN);menu.Items.Clear();menu.Items.Add(new MenuItem {Header=pin.Name,IsEnabled=false});
            foreach(var target in apps.Where(a=>DockPinStore.Matches(pin,a))) {
                var chosen=target;string title=NativeDockIcons.WindowTitle(target.Handle,target.Pid);
                var window=new MenuItem {Header=String.IsNullOrWhiteSpace(title)?"显示窗口":title.Replace("_","__"),IsCheckable=true,IsChecked=target.Active};
                window.Click+=delegate{Request(chosen);};menu.Items.Add(window);
            }
            menu.Items.Add(new Separator());
            AddPinMenu(menu,"向左移动",delegate{pins.Move(pin.Id,-1);RefreshPins();});
            AddPinMenu(menu,"向右移动",delegate{pins.Move(pin.Id,1);RefreshPins();});
            AddPinMenu(menu,"从程序坞移除",delegate{pins.Remove(pin.Id);RefreshPins();});
        };menu.Closed+=delegate{menuOpen=false;};button.ContextMenu=menu;
    }
    static void AddPinMenu(ContextMenu menu,string title,Action action) {
        var item=new MenuItem {Header=title};item.Click+=delegate{try{action();}catch(Exception ex){MessageBox.Show(ex.Message,"固定入口");}};menu.Items.Add(item);
    }
    void RefreshPins(){Dispatcher.BeginInvoke(new Action(delegate{RenderApps();Position();}));}
    void RefreshClickSnapshot() {
        try {
            var path=Path.Combine(stageRoot,"stage-apps.json");
            if(new FileInfo(path).Length>262144)throw new InvalidDataException();
            apps=MacDockProtocol.Read(File.ReadAllText(path),DateTime.UtcNow,out fullscreen);
        }catch{apps=new MacDockApp[0];}
    }
    void ActivateRunningApp(string key) {
        RefreshClickSnapshot();
        var current=apps.Where(a=>a.Key==key&&NativeDockIcons.LiveWindow(a.Handle,a.Pid)).ToArray();
        if(current.Length>0){Activate(key,current);return;}
        var pin=pins.Items.FirstOrDefault(p=>p.Kind=="app"&&DockPinStore.Key(p.Identity)==key);
        if(pin!=null){LaunchPin(pin);return;}
        if(key=="explorer")OpenShell("shell:MyComputerFolder");
        else if(key=="msedge")System.Diagnostics.Process.Start(new ProcessStartInfo("microsoft-edge:"){UseShellExecute=true});
        else if(key=="codex")DockAppLauncher.Open(true,delegate{});
        else ShowTrayForRecovery("软件窗口已收进后台，已请求显示系统托盘，请从托盘恢复。");
    }
    void ShowTrayForRecovery(string detail) {
        QueueTrayForRecovery(detail,null,Interlocked.Read(ref activationGeneration),false);
    }
    void ShowTrayForRecovery(string detail,PendingPinLaunch operation,bool allowExpired=false) {
        QueueTrayForRecovery(detail,operation,0,allowExpired);
    }
    bool TrayRecoveryCurrent(PendingPinLaunch operation,long generation,bool allowExpired) {
        if(closed||Dispatcher.HasShutdownStarted)return false;
        if(operation==null)return Interlocked.Read(ref activationGeneration)==generation;
        return Interlocked.Read(ref activationGeneration)==Interlocked.Read(ref operation.Generation)
            &&(allowExpired||operation.Age.ElapsedMilliseconds<5000);
    }
    void QueueTrayForRecovery(string detail,PendingPinLaunch operation,long generation,bool allowExpired) {
        if(!TrayRecoveryCurrent(operation,generation,allowExpired))return;
        Dispatcher.BeginInvoke(new Action(delegate {
            if(!TrayRecoveryCurrent(operation,generation,allowExpired))return;
            ReleaseRecoveryPresentation("manual-system-entry");
            // A recovery request is idempotent; two fast clicks must not toggle the
            // tray closed again before the guardian publishes its next status.
            try{File.WriteAllText(Path.Combine(stageRoot,"taskbar-peek.request"),"1");}
            catch(Exception error){DockRuntime.Record(AppDomain.CurrentDomain.BaseDirectory,"tray-request",error);notify("系统托盘入口暂未能显示，请稍后重试。");return;}
            if(TrayRecoveryCurrent(operation,generation,allowExpired))notify(detail);
        }));
    }
    bool WaitForRecoveredWindow(DockTrayRecovery.Result restored,PendingPinLaunch operation) {
        long waitStarted=operation.Age.ElapsedMilliseconds;
        while(operation.Age.ElapsedMilliseconds-waitStarted<1200&&RecoveryCurrent(operation)) {
            if(!NativeDockIcons.LiveWindow(restored.Handle,restored.Pid))return false;
            try {
                var path=Path.Combine(stageRoot,"stage-apps.json");
                if(new FileInfo(path).Length<=262144) {
                    bool full;var latest=MacDockProtocol.Read(File.ReadAllText(path),DateTime.UtcNow,out full);
                    var target=latest.FirstOrDefault(a=>a.Handle==restored.Handle&&a.Pid==restored.Pid);
                    if(target!=null&&RecoveryCurrent(operation)){
                        Dispatcher.BeginInvoke(new Action(delegate {
                            if(RecoveryCurrent(operation))RequestRecovered(target,Interlocked.Read(ref operation.Generation));
                        }));return true;
                    }
                }
            }catch{}
            Thread.Sleep(100);
        }
        return false;
    }
    void LaunchPin(DockPin pin) {
        PendingPinLaunch existing;
        if(pin.Kind!="web"&&pendingPinLaunches.TryGetValue(pin.Id,out existing)) {
            // A repeated click on this same pending entry keeps one operation alive.
            // It does not extend the original deadline or invoke the tray twice.
            Interlocked.Exchange(ref existing.Generation,Interlocked.Read(ref activationGeneration));
            if(existing.Age.ElapsedMilliseconds>=5000)ShowTrayForRecovery("软件正在响应上一条恢复请求。可先从系统托盘打开。",existing,true);
            return;
        }
        long now=DateTime.UtcNow.Ticks,previous;string key="launch:"+pin.Id;
        if(clicks.TryGetValue(key,out previous)&&now-previous<500*TimeSpan.TicksPerMillisecond)return;clicks[key]=now;
        if(pin.Kind=="web") {
            if(pinWebLauncher==null)pinWebLauncher=new DockLinkLauncher();
            string marker=StageManager.Services.BrowserActivationIntent.CreateMarker(pinWebLauncher.DefaultBrowser);
            pinWebLauncher.Open(pin.Target,stageRoot,marker,delegate(Exception error){if(error!=null&&!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(delegate{MessageBox.Show(error.Message,"打开网页");}));});return;
        }
        var operation=new PendingPinLaunch {Generation=Interlocked.Read(ref activationGeneration)};
        pendingPinLaunches.Add(pin.Id,operation);
        string presentationKey=DockPinStore.Key(pin.Identity);
        if(pin.Kind=="app"&&(presentationKey=="qq"||presentationKey=="wechat"||presentationKey=="weixin"))BeginRecoveryPresentation(operation);
        RecordPinRecovery(operation,"queued","none");
        var timeout=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromSeconds(8)};
        timeout.Tick+=delegate {timeout.Stop();PendingPinLaunch pending;if(pendingPinLaunches.TryGetValue(pin.Id,out pending)&&Object.ReferenceEquals(pending,operation)){RecordPinRecovery(operation,"provider-delayed","none");ShowTrayForRecovery("软件响应较慢，已显示系统托盘，可从托盘打开。",operation,true);}};timeout.Start();
        string lastRoute="none";
        var worker=new Thread(delegate() {
            try {
                if(!RecoveryCurrent(operation))return;
                // A tray-only messaging client must be recovered from its existing
                // session, not by spawning its executable and producing another login.
                string identity=DockPinStore.Key(pin.Identity);
                if(pin.Kind=="app"&&(identity=="qq"||identity=="wechat"||identity=="weixin"||identity=="qqmusic"||identity=="clash-verge")) {
                    var processes=Process.GetProcessesByName(Path.GetFileNameWithoutExtension(pin.Identity));
                    bool trayOnly=processes.Length>0;foreach(var process in processes)process.Dispose();
                    if(trayOnly){
                        if(!RecoveryCurrent(operation))return;
                        RecordPinRecovery(operation,"native-recovery","pending");
                        var restored=DockTrayRecovery.Restore(pin,stageRoot,delegate {return RecoveryCurrent(operation);});
                        CompleteRecoveryPresentation(operation,restored);
                        lastRoute=restored.Route;RecordPinRecovery(operation,"native-returned",lastRoute);
                        if(!RecoveryCurrent(operation))return;
                        try{DockRuntime.AtomicText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dock-recovery.status"),"State="+(restored.Shown?"shown":restored.WakeRequested?"wake-requested":"tray-fallback")+"\nRoute="+restored.Route+"\nForeground="+restored.Foreground+"\nUtc="+DateTime.UtcNow.ToString("o"));}catch{}
                        if(restored.Shown){
                            if(!restored.Foreground&&!WaitForRecoveredWindow(restored,operation))ShowTrayForRecovery("软件窗口已显示，正在等待台前调度识别。也可从系统托盘切到前台。",operation);
                            return;
                        }
                        if(restored.ActionUncertain){ShowTrayForRecovery("软件托盘的默认动作结果尚未确认，可手动从托盘打开。",operation);return;}
                        if(restored.WakeRequested){Dispatcher.BeginInvoke(new Action(delegate{if(RecoveryCurrent(operation))notify("已请求软件自行恢复主界面。");}));return;}
                        ShowTrayForRecovery("软件仍在后台运行，自动托盘恢复暂未成功。系统托盘已显示，可从托盘找回窗口。",operation);return;
                    }
                }
                if(!File.Exists(pin.Target)&&!Directory.Exists(pin.Target))throw new FileNotFoundException("入口已不存在。请右键移除后重新添加。");
                if(!RecoveryCurrent(operation))return;
                var start=new ProcessStartInfo(pin.Target){UseShellExecute=true};
                if(pin.Kind=="app"&&Path.GetExtension(pin.Target).Equals(".exe",StringComparison.OrdinalIgnoreCase))start.WorkingDirectory=Path.GetDirectoryName(pin.Target);
                Process.Start(start);
            }catch(Exception ex){DockRuntime.Record(AppDomain.CurrentDomain.BaseDirectory,"pin-launch",ex);if(RecoveryCurrent(operation))Dispatcher.BeginInvoke(new Action(delegate{if(RecoveryCurrent(operation))notify("这个入口暂未能打开，请稍后重试或右键检查固定入口。");}));}
            finally{RecordPinRecovery(operation,"finished",lastRoute);if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(delegate{timeout.Stop();PendingPinLaunch pending;if(pendingPinLaunches.TryGetValue(pin.Id,out pending)&&Object.ReferenceEquals(pending,operation))pendingPinLaunches.Remove(pin.Id);var presentation=recoveryPresentation;if(presentation!=null&&Object.ReferenceEquals(presentation.Operation,operation)&&!presentation.Completed){if(Interlocked.Read(ref activationGeneration)==Interlocked.Read(ref operation.Generation))ReleaseRecoveryPresentation("operation-finished");else{presentation.Completed=true;presentation.CompletedAt=presentation.Age.ElapsedMilliseconds;}}}));}
        });worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);worker.Start();
    }
    internal static void CheckRecoveryPresentation(string report) {
        if(!OwnRecoveryPeek(true,false,42,false,false)||OwnRecoveryPeek(false,false,42,false,false)||OwnRecoveryPeek(true,true,42,false,false)
            ||OwnRecoveryPeek(true,false,0,false,false)||OwnRecoveryPeek(true,false,42,true,false)||OwnRecoveryPeek(true,false,42,false,true))throw new InvalidOperationException("Internal peek ownership policy");
        if(!CloseRecoveryPeek(true,true,true,true,true,true,false,false,false,false))throw new InvalidOperationException("Owned restored foreground peek was not dismissible");
        for(int flag=0;flag<10;flag++) {
            bool[] flags={true,true,true,true,true,true,false,false,false,false};flags[flag]=!flags[flag];
            if(CloseRecoveryPeek(flags[0],flags[1],flags[2],flags[3],flags[4],flags[5],flags[6],flags[7],flags[8],flags[9]))throw new InvalidOperationException("Unsafe peek dismissal condition "+flag);
        }
        if(!TransferRecoveryPeek(true,false,true,true,true,false,true)||TransferRecoveryPeek(true,true,true,true,true,false,true)
            ||TransferRecoveryPeek(true,false,false,true,true,false,true)||TransferRecoveryPeek(true,false,true,false,true,false,true)
            ||TransferRecoveryPeek(true,false,true,true,false,false,true)||TransferRecoveryPeek(true,false,true,true,true,true,true)
            ||TransferRecoveryPeek(true,false,true,true,true,false,false))throw new InvalidOperationException("Superseding private recovery ownership policy");
        if(!VisibleFor(true,false,true,true)||VisibleFor(false,false,true,true)||VisibleFor(true,true,true,true)||VisibleFor(true,false,true,false))throw new InvalidOperationException("Internal recovery Dock visibility policy");
        var utc=DateTime.UtcNow;
        if(!RecoveryFresh("State=hidden\nUtc="+utc.ToString("o"),utc)||RecoveryFresh("State=hidden\nUtc="+utc.AddSeconds(-5).ToString("o"),utc)||RecoveryFresh("State=hidden",utc))throw new InvalidOperationException("Presentation snapshot freshness");
        File.WriteAllText(report,"PASS: internally owned tray recovery keeps Dock visible; restored foreground closes only its fresh active peek; existing/manual/failure/changed-stage/pending-request/reordered-toggle conditions never auto-dismiss; canceled internal operations clean only owned entry and retain newer app foreground; consecutive internal recovery transfers only current ownership; fullscreen and stopped Stage still yield; bounded presentation snapshots expire. Policy fixture only, no existing desktop window or taskbar controlled.");
    }
}
