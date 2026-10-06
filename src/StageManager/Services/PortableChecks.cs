using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StageManager.Services
{
    internal static class PortableChecks
    {
        internal static async Task<string> RunAsync()
        {
            CheckDailyProfile();
            await ResilienceChecks.RunAsync();
            FeatureChecks.Run();
            WorkspaceChecks.Run();
            await SceneManager.CheckGroupTransactionsAsync();
            if(WindowIntegrity.Read(Environment.ProcessId)==null
                || WindowIntegrity.MayControl(8192,12288) || WindowIntegrity.MayControl(4096,8192)
                || !WindowIntegrity.MayControl(8192,8192) || !WindowIntegrity.MayControl(12288,8192)
                || !WindowIntegrity.MayControl(null,8192) || !WindowIntegrity.MayControl(8192,null))
                throw new InvalidOperationException("Window integrity read/classification");
            if (!PortablePreferences.IsExcludedProcess("PersonalDock-v4.exe")
                || !PortablePreferences.IsExcludedProcess("MailBridge-v2.exe")
                || PortablePreferences.IsExcludedProcess("chrome.exe"))
                throw new InvalidOperationException("Companion process exclusions");

            var order = new List<int>();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new List<Exception>();
            var queue = new LatestRequestQueue<int>(async target =>
            {
                order.Add(target);
                if (target == 1) { started.SetResult(); await release.Task; }
                if (target == 4) throw new InvalidOperationException("Expected test failure");
            }, errors.Add);
            var drain = queue.RequestAsync(1);
            await started.Task;
            _ = queue.RequestAsync(1); // Duplicate while first operation is active.
            _ = queue.RequestAsync(2);
            _ = queue.RequestAsync(3); // Newest pending target wins.
            _ = queue.RequestAsync(3);
            release.SetResult();
            await drain;
            await queue.RequestAsync(3); // Completed double click is ignored.
            if (!order.SequenceEqual(new[] { 1, 3 }) || queue.IsBusy)
                throw new InvalidOperationException("Rapid-click queue/coalescing");
            await queue.RequestAsync(4);
            await queue.RequestAsync(5);
            if (errors.Count != 1 || queue.IsBusy || order.Last() != 5)
                throw new InvalidOperationException("Queue exception recovery");
            if(RightSidebarGeometry.PhysicalLeft(1920,240,1.5)!=1560
                || RightSidebarGeometry.PhysicalLeft(0,240,2)!=-480
                || RightSidebarGeometry.OutwardDistance(240,0)!=240
                || RightSidebarGeometry.OutwardDistance(240,-120)!=360
                || RightSidebarGeometry.BufferProgress(1500,1440,1560)!=0.5
                || RightSidebarGeometry.BufferProgress(1600,1440,1560)!=1
                || RightSidebarGeometry.BufferProgress(1400,1440,1560)!=0)
                throw new InvalidOperationException("Right sidebar/DPI/drag buffer geometry");
            RegistrationChecks.Run();
            TaskbarLeaseChecks.Run();
            await QqRestoreChecks.RunAsync();
            await WeChatRestoreChecks.RunAsync();
            InputHandoffChecks.Run();
            SidebarInputChecks.Run();
            var navigation = new SystemSelectionPolicy();
            var sidebarResume=new SidebarResumePolicy();
            if(sidebarResume.Observe(false,false)||sidebarResume.Observe(false,true)||!sidebarResume.Observe(false,false)
                ||sidebarResume.Observe(false,false)||sidebarResume.Observe(true,false)||!sidebarResume.Observe(false,false)
                ||sidebarResume.Observe(false,false))throw new InvalidOperationException("Sidebar reveal not rearmed exactly once after taskbar/Task View closes");
            if(navigation.Observe(false,true,0)) throw new InvalidOperationException("Automatic foreground mistaken for system choice");
            navigation.Observe(true,false,100);
            if(!navigation.Active) throw new InvalidOperationException("System selector not active");
            navigation.Observe(true,false,100000); // A long-open Task View has no intent expiry.
            if(navigation.Epoch != 1) throw new InvalidOperationException("Duplicate selector events changed navigation generation");
            navigation.Observe(false,false,100100); // Intermediate desktop frame.
            if(navigation.Active || !navigation.Observe(false,true,100200)
                || navigation.Observe(false,true,100201)) throw new InvalidOperationException("System selection handoff/deduplication");
            navigation.Observe(true,false,200000);
            if(navigation.Epoch != 2) throw new InvalidOperationException("New selector failed to invalidate delayed focus");
            navigation.Observe(false,false,200100);
            if(navigation.Observe(false,true,202000)) throw new InvalidOperationException("Stale shell choice restored a window");
            navigation.Observe(true,false,300000);
            if(!navigation.Observe(false,true,300100)) throw new InvalidOperationException("Direct Task View selection ignored");
            if(!SystemSelectionPolicy.IsSelector("explorer","MultitaskingViewFrame")
                || !SystemSelectionPolicy.IsSelector("explorer","XamlExplorerHostIslandWindow")
                || !SystemSelectionPolicy.IsSelector("ShellHost","Windows.UI.Core.CoreWindow")
                || SystemSelectionPolicy.IsSelector("explorer","CabinetWClass")
                || SystemSelectionPolicy.IsSelector("explorer","ExploreWClass")
                || SystemSelectionPolicy.IsSelector("explorer","WorkerW")
                || SystemSelectionPolicy.IsSelector("msedge","Chrome_WidgetWin_1"))
                throw new InvalidOperationException("Shell selector/app/desktop classification");
            var stage=new StageSelection<object>();var a=new object();var b=new object();var c=new object();
            stage.Select(a,true);stage.Select(b,true);
            if(stage.Items.Count!=2 || !stage.Contains(a) || stage.Current!=b)throw new InvalidOperationException("Coexisting groups");
            stage.Remove(b);
            if(stage.Current!=a || stage.Items.Count!=1)throw new InvalidOperationException("Close current group preserves other groups");
            stage.Select(c,true);stage.Remove(a);
            if(stage.Current!=c || stage.Items.Count!=1)throw new InvalidOperationException("Stow one group preserves other groups");
            stage.Select(b,false);
            if(stage.Items.Count!=1 || stage.Contains(c))throw new InvalidOperationException("Exclusive stage option");
            stage.Select(null,true);
            if(stage.Items.Count!=0)throw new InvalidOperationException("Stow all groups");
            bool disposed=false;int reports=0;
            CaptureCleanup.Run(()=>throw new ArgumentException("Closed CompositionTarget"),()=>disposed=true,_=>reports++);
            if(!disposed || reports!=1)throw new InvalidOperationException("Capture close failure interrupted cleanup");
            var desktop=new System.Windows.Rect(-1920,0,1920,1040);
            foreach(int count in new[]{1,2,5,12}) {
                var tiles=RightSidebarGeometry.TileBounds(desktop,count);
                if(tiles.Length!=count || tiles.Any(r=>!desktop.Contains(r) || r.Width<=0 || r.Height<=0))
                    throw new InvalidOperationException("Tiled windows left desktop bounds");
                for(int i=0;i<count;i++)for(int j=i+1;j<count;j++)
                    if(tiles[i].IntersectsWith(tiles[j]))throw new InvalidOperationException("Tiled windows overlap");
            }
            var mapped=ThumbnailViewport.Map(new System.Windows.Rect(100,-50,200,200),
                new System.Windows.Rect(0,0,400,100),new System.Windows.Size(800,800));
            if(mapped.Destination!=new System.Windows.Rect(100,0,200,100)
                || mapped.Source!=new System.Windows.Rect(0,200,800,400))throw new InvalidOperationException("Scroll viewport crop mapping");
            if(!ThumbnailViewport.Map(new System.Windows.Rect(0,200,100,100),new System.Windows.Rect(0,0,400,100),
                new System.Windows.Size(800,800)).Destination.IsEmpty)throw new InvalidOperationException("Off-viewport card visible");
            if(ThumbnailViewport.ScrollOffset(10,120,1000,500)!=0
                || ThumbnailViewport.ScrollOffset(450,-120,1000,500)!=500
                || ThumbnailViewport.ScrollOffset(250,10,1000,500)!=244)throw new InvalidOperationException("Wheel/trackpad scrolling bounds");
            if(PreviewPolicy.RetainTrackedWindow(false,true) || PreviewPolicy.RetainTrackedWindow(true,false)
                || !PreviewPolicy.RetainTrackedWindow(true,true))throw new InvalidOperationException("Closed/hidden windows retained");
            return "PASS: WeChat main-root native SC_MINIMIZE/SC_RESTORE at 96/144/192 DPI, four owned minimize/restore cycles without parking/style changes, duplicate command ownership, no forced normal-window restore, user-minimize preservation, hidden/tray/destroyed refusal; blocked desktop-query worker allows 5000 immediate UI reads/requests with bounded duplicate queue, resumes after release, rejects stale HWND/PID and never waits on disposal; stale taskbar snapshot expiration, edge latch recovery and delayed-UI taskbar fail-open/resume/exit; daily profile clears legacy Mac/exclusive/one-member flags and remains idempotent without touching user files; owned-window sidebar and Dock selection retain earlier apps and every app member; sidebar rearmed once after taskbar/Task View release, without requiring pointer retreat; Dock activation token/expiry/PID validation and own-window shared restoration queue; idempotent taskbar recovery supersedes prior dismissal without pointer retreat and remains closeable by button; taskbar first/second/third toggle and shell-hover dismissal; display/virtual-desktop scoped selection and registration; fullscreen exclusion; restart title ambiguity refusal; document target validation; animation endpoints; Mac mode isolation, drag merge, reverse split and last member stow; bounded Mac cards; app window selection; taskbar lease peek/resume/exit and crash restoration; QQ native-minimize/tray/login and integrity policies; rapid-click queue recovery; DWM preview lifetime/input geometry; scroll/viewport bounds; Task View handoff and stale-focus suppression. No existing desktop window changes.";
        }
        private static void CheckDailyProfile()
        {
            foreach(var initial in new[]{Array.Empty<string>(),new[]{"mac-mode","exclusive-stage","one-window-per-app","sidebar-always-visible","hide-desktop-icons","disable-window-motion"}}) {
                var state=new HashSet<string>(initial,StringComparer.Ordinal);
                int writes=0;
                void Write(string key,bool enabled) {writes++;if(enabled)state.Add(key);else state.Remove(key);}
                SimpleProfile.Apply(state.Contains,Write);
                if(!state.SetEquals(new[]{"recent-first","hide-taskbar","disable-wallpaper-toggle"}))
                    throw new InvalidOperationException("Daily profile must retain multiple applications and all app windows without setup");
                int firstWrites=writes;SimpleProfile.Apply(state.Contains,Write);
                if(writes!=firstWrites)throw new InvalidOperationException("Daily profile rewrites unchanged preferences");
            }
        }
    }
}
