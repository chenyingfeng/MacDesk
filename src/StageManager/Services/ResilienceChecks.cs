using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
namespace StageManager.Services;
internal static class ResilienceChecks
{
    internal static async Task RunAsync()
    {
        var now=DateTime.UtcNow;
        string Peek(DateTime utc)=>$"State=peek\nUtc={utc:O}\n";
        if(!RuntimeSnapshotPolicy.TaskbarPeek(Peek(now),now)
            ||RuntimeSnapshotPolicy.TaskbarPeek(Peek(now.AddSeconds(-5)),now)
            ||RuntimeSnapshotPolicy.TaskbarPeek(Peek(now.AddSeconds(2)),now)
            ||RuntimeSnapshotPolicy.TaskbarPeek("State=peek\nUtc=invalid",now))
            throw new InvalidOperationException("Stale/malformed/future taskbar peek retained");
        var resume=new SidebarResumePolicy();resume.Observe(false,true);
        if(!resume.Observe(false,RuntimeSnapshotPolicy.TaskbarPeek(Peek(now.AddSeconds(-5)),now)))
            throw new InvalidOperationException("Expired guardian peek did not rearm sidebar");
        if(!RuntimeSnapshotPolicy.ShouldRearm(true,false,true,now.AddMilliseconds(-1),now)
            ||RuntimeSnapshotPolicy.ShouldRearm(true,false,true,now.AddMilliseconds(1),now)
            ||RuntimeSnapshotPolicy.ShouldRearm(true,false,false,now.AddMilliseconds(-1),now))
            throw new InvalidOperationException("Hidden edge latch requires pointer retreat or ignores suppression");
        var healthy=$"Pid=123\nState=responsive\nUiUtc={now:O}\nUtc={now:O}\n";
        if(!RuntimeSnapshotPolicy.UiResponsive(healthy,123,now)
            ||RuntimeSnapshotPolicy.UiResponsive(healthy,124,now)
            ||RuntimeSnapshotPolicy.UiResponsive(healthy,123,now.AddSeconds(13)))
            throw new InvalidOperationException("UI health PID/expiry safety");
        var token=Guid.NewGuid().ToString("N");
        if(!RuntimeSnapshotPolicy.RecoveryToken(token,now,now)
            ||RuntimeSnapshotPolicy.RecoveryToken(token,now,now.AddSeconds(6))
            ||RuntimeSnapshotPolicy.RecoveryToken("bad",now,now))
            throw new InvalidOperationException("Own UI recovery marker expiration/token");
        bool visible=true;
        var target=new TaskbarVisibilityLease.Target(new IntPtr(1),1);
        using(var lease=new TaskbarVisibilityLease(()=>new[]{target},_=>true,_=>visible,(_,show)=>{visible=show;return true;})) {
            lease.Refresh();if(visible)throw new InvalidOperationException("Health fixture initial hide");
            lease.Refresh(!RuntimeSnapshotPolicy.UiResponsive(healthy,123,now.AddSeconds(13)));
            if(!visible)throw new InvalidOperationException("Delayed UI leaves taskbar inaccessible");
            lease.Refresh(!RuntimeSnapshotPolicy.UiResponsive(healthy,123,now));
            if(visible)throw new InvalidOperationException("Responsive UI cannot resume taskbar lease");
        }
        if(!visible)throw new InvalidOperationException("Health fixture exit restoration");
        await CheckBlockedDesktopQuery();
    }
    private static async Task CheckBlockedDesktopQuery()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var guid=Guid.NewGuid();int pid=123;var now=DateTime.UtcNow;int moves=0,completed=0;
        using var worker=new DesktopQueryWorker(h=>{entered.Set();release.Wait();Interlocked.Increment(ref completed);return new DesktopQueryResult(guid,true);},
            ()=>guid,(_,_)=>Interlocked.Increment(ref moves),_=>Volatile.Read(ref pid),()=>now);
        try {
            worker.Request(new IntPtr(1),123);
            for(int i=0;i<100&&!entered.IsSet;i++)await Task.Delay(10);
            if(!entered.IsSet)throw new InvalidOperationException("Blocked metadata fixture did not enter");
            var elapsed=Stopwatch.StartNew();
            for(int i=0;i<5000;i++) {
                worker.Request(new IntPtr(1),123);worker.RefreshCurrent();
                if(worker.Cached(new IntPtr(1),123)!=null||worker.CurrentDesktop!=Guid.Empty)
                    throw new InvalidOperationException("Blocked query fabricated desktop membership");
            }
            if(elapsed.Elapsed>TimeSpan.FromSeconds(1)||worker.PendingCount>2)
                throw new InvalidOperationException("Blocked shell query waited on UI or grew unbounded duplicate jobs");
            worker.MoveOwned(new IntPtr(2),guid);if(moves!=0)throw new InvalidOperationException("Foreign window move queued");
            release.Set();
            for(int i=0;i<100&&worker.Cached(new IntPtr(1),123)==null;i++)await Task.Delay(10);
            if(worker.Cached(new IntPtr(1),123)?.Desktop!=guid)
                throw new InvalidOperationException("Metadata worker failed to recover after shell query release");
            if(worker.Cached(new IntPtr(1),124)!=null)throw new InvalidOperationException("Desktop cache ignores HWND/PID reuse");
            now=now.AddSeconds(3);
            if(worker.Cached(new IntPtr(1),123)!=null||worker.CurrentDesktop!=Guid.Empty)
                throw new InvalidOperationException("Stale desktop scope survived bounded cache lifetime");
            entered.Reset();release.Reset();worker.Request(new IntPtr(3),123);
            for(int i=0;i<100&&!entered.IsSet;i++)await Task.Delay(10);
            elapsed.Restart();worker.Dispose();
            if(elapsed.Elapsed>TimeSpan.FromMilliseconds(200))throw new InvalidOperationException("Shutdown waits for blocked shell COM");
            release.Set();
            for(int i=0;i<100&&Volatile.Read(ref completed)<2;i++)await Task.Delay(10);
            if(completed<2)throw new InvalidOperationException("Released metadata fixture remained blocked after disposal");
        }finally{release.Set();}
    }
}
