using System;
using System.Collections.Generic;
using System.Linq;
namespace StageManager.Services;
internal static class TaskbarLeaseChecks
{
    internal static void Run()
    {
        var a=new TaskbarVisibilityLease.Target(new IntPtr(1),101);
        var b=new TaskbarVisibilityLease.Target(new IntPtr(2),101);
        var targets=new List<TaskbarVisibilityLease.Target>{a,b};
        var visible=new Dictionary<IntPtr,bool>{{a.Handle,true},{b.Handle,false}};
        var calls=new List<(IntPtr,bool)>();
        var lease=new TaskbarVisibilityLease(()=>targets,t=>targets.Contains(t),h=>visible[h],(h,v)=>{calls.Add((h,v));visible[h]=v;return true;});
        lease.Refresh();lease.Refresh();
        if(lease.HiddenCount!=1 || calls.Count!=1)throw new InvalidOperationException("Taskbar repeated hide or changed initially hidden bar");
        // Explorer restart / HWND reuse: never restore the replacement process's handle.
        targets.Remove(a);var replacement=new TaskbarVisibilityLease.Target(a.Handle,202);targets.Add(replacement);visible[a.Handle]=true;
        lease.Refresh();visible[a.Handle]=true;lease.Refresh();
        if(calls.Count!=3)throw new InvalidOperationException("Taskbar restart/reappearance not handled");
        lease.Dispose();lease.Dispose();lease.Refresh();
        if(!visible[a.Handle] || visible[b.Handle] || calls.Count!=4 || !lease.RestorationComplete)
            throw new InvalidOperationException("Taskbar restore/idempotence/original state");
        // Restore also runs when a watcher fails midway through its session.
        visible[a.Handle]=true;
        try {
            using var failing=new TaskbarVisibilityLease(()=>targets,t=>targets.Contains(t),h=>visible[h],(h,v)=>{visible[h]=v;return true;});
            failing.Refresh();throw new InvalidOperationException("fixture");
        }catch(InvalidOperationException){}
        if(!visible[a.Handle] || visible[b.Handle])throw new InvalidOperationException("Taskbar failure recovery");
        calls.Clear();visible[a.Handle]=true;
        using(var peekLease=new TaskbarVisibilityLease(()=>targets,t=>targets.Contains(t),h=>visible[h],(h,v)=>{calls.Add((h,v));visible[h]=v;return true;})) {
            peekLease.Refresh();peekLease.Refresh(true);peekLease.Refresh(true);
            if(!visible[a.Handle]||visible[b.Handle]||calls.Count!=2||peekLease.HiddenCount!=0)
                throw new InvalidOperationException("Taskbar peek changes original hidden state or repeats show");
            peekLease.Refresh();peekLease.Refresh();
            if(visible[a.Handle]||calls.Count!=3)throw new InvalidOperationException("Peek does not resume hiding");
            peekLease.Refresh(true);
        }
        if(!visible[a.Handle]||visible[b.Handle])throw new InvalidOperationException("Exit during peek fails restore");
        var policy=new TaskbarRevealPolicy();
        if(policy.Update(true,false,false,100)||policy.Update(true,false,false,279)
            ||!policy.Update(true,false,false,280)||!policy.Update(false,false,false,2079)
            ||policy.Update(false,false,false,2080))throw new InvalidOperationException("Taskbar edge dwell/grace timing");
        policy.Request(3000);
        if(!policy.Update(false,false,false,22999)||policy.Update(false,false,false,23000))
            throw new InvalidOperationException("Explicit taskbar request timeout");
        if(!policy.Update(false,false,true,24000)||!policy.Update(false,true,false,25000)
            ||policy.Update(false,false,false,26800))throw new InvalidOperationException("Taskbar shell interaction hold");
        policy.Toggle(30000);
        if(!policy.Update(false,false,false,90000))throw new InvalidOperationException("Taskbar toggle does not latch visible");
        policy.Toggle(91000);
        if(policy.Update(true,true,true,91001)||policy.Update(true,true,true,99999))
            throw new InvalidOperationException("Second toggle reopens through cursor/shell hold");
        policy.Update(false,false,false,100000);policy.Toggle(100001);
        if(!policy.Update(false,false,false,100002))throw new InvalidOperationException("Third toggle fails reopen");
        var recovery=new TaskbarRevealPolicy();
        recovery.Toggle(100);recovery.Toggle(200);
        if(recovery.Update(true,true,true,201))throw new InvalidOperationException("Recovery precondition: button dismissal does not hold");
        recovery.Request(300);
        if(!recovery.Update(true,true,true,301))throw new InvalidOperationException("Explicit recovery stays dismissed until pointer leaves the tray");
        recovery.Request(302);
        if(!recovery.Update(true,true,true,303))throw new InvalidOperationException("Repeated recovery requests cancel visible taskbar");
        recovery.Toggle(304);
        if(recovery.Update(true,true,true,305))throw new InvalidOperationException("User button cannot close a recovered taskbar");
        recovery.Request(500);
        if(!recovery.Update(false,false,false,20499)||recovery.Update(false,false,false,20500))
            throw new InvalidOperationException("Recovery request does not preserve bounded peek lifetime");
        string queueRoot=System.IO.Path.Combine(PortablePreferences.Root,"toggle-check-"+Guid.NewGuid().ToString("N"));
        TaskbarToggleQueue.Request(queueRoot);TaskbarToggleQueue.Request(queueRoot);int requests=0;
        TaskbarToggleQueue.Consume(queueRoot,()=>requests++,false);
        if(requests!=2)throw new InvalidOperationException("Rapid taskbar clicks lost toggle parity");
        System.IO.Directory.Delete(System.IO.Path.Combine(queueRoot,"taskbar-toggles"));System.IO.Directory.Delete(queueRoot);
    }
}
