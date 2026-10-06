using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
namespace StageManager.Services;
internal readonly record struct DesktopQueryResult(Guid Desktop,bool? Current);
// One bounded background STA owns shell COM. The UI reads only timestamped values.
internal sealed class DesktopQueryWorker : IDisposable
{
    private readonly object gate=new();
    private readonly Queue<(string Key,Action Run)> queue=new();
    private readonly HashSet<string> pending=new();
    private readonly Dictionary<IntPtr,(int Pid,DesktopQueryResult Value,DateTime Utc)> results=new();
    private readonly AutoResetEvent wake=new(false);
    private readonly Func<IntPtr,DesktopQueryResult> query;
    private readonly Func<Guid> currentQuery;
    private readonly Action<IntPtr,Guid> move;
    private readonly Func<IntPtr,int> pidOf;
    private readonly Func<DateTime> now;
    private readonly Thread thread;
    private Guid current;private DateTime currentUtc;
    private bool stopped;
    internal DesktopQueryWorker(Func<IntPtr,DesktopQueryResult> query,Func<Guid> currentQuery,
        Action<IntPtr,Guid> move,Func<IntPtr,int> pidOf,Func<DateTime>? clock=null)
    {
        this.query=query;this.currentQuery=currentQuery;this.move=move;this.pidOf=pidOf;now=clock??(()=>DateTime.UtcNow);
        thread=new Thread(Run){IsBackground=true,Name="Stage desktop metadata"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
    }
    internal static bool Fresh(DateTime stamp,DateTime utc)=>utc-stamp>=TimeSpan.FromSeconds(-1)&&utc-stamp<=TimeSpan.FromSeconds(2);
    internal DesktopQueryResult? Cached(IntPtr h,int pid) {
        lock(gate)return results.TryGetValue(h,out var entry)&&entry.Pid==pid&&Fresh(entry.Utc,now())?entry.Value:null;
    }
    internal Guid CurrentDesktop {get{lock(gate)return Fresh(currentUtc,now())?current:Guid.Empty;}}
    internal bool CurrentFresh {get{lock(gate)return Fresh(currentUtc,now());}}
    internal int PendingCount {get{lock(gate)return pending.Count;}}
    internal void Request(IntPtr h,int pid) {
        if(h==IntPtr.Zero||pid<=0)return;
        lock(gate)if(results.TryGetValue(h,out var cached)&&cached.Pid==pid&&now()-cached.Utc<TimeSpan.FromMilliseconds(400))return;
        Enqueue("query:"+h.ToInt64(),()=> {
            if(pidOf(h)!=pid)return;
            var answer=query(h);
            if(pidOf(h)!=pid)return;
            lock(gate){results[h]=(pid,answer,now());if(results.Count>2048)Prune();}
        });
    }
    internal void RefreshCurrent()=>Enqueue("current",()=>{var answer=currentQuery();lock(gate){current=answer;currentUtc=now();}});
    internal void MoveOwned(IntPtr h,Guid desktop) {
        if(h==IntPtr.Zero||desktop==Guid.Empty||pidOf(h)!=Environment.ProcessId)return;
        Enqueue("move:"+h.ToInt64(),()=>{if(pidOf(h)==Environment.ProcessId&&CurrentDesktop==desktop)move(h,desktop);});
    }
    private void Prune(){var stale=new List<IntPtr>();foreach(var p in results)if(!Fresh(p.Value.Utc,now()))stale.Add(p.Key);foreach(var h in stale)results.Remove(h);}
    private void Enqueue(string key,Action run) {
        lock(gate){if(stopped||pending.Contains(key)||queue.Count>=64)return;pending.Add(key);queue.Enqueue((key,run));wake.Set();}
    }
    private void Run() {
        while(true) {
            (string Key,Action Run) job;
            lock(gate){if(stopped)return;if(queue.Count==0)job=("",()=>{});else job=queue.Dequeue();}
            if(job.Key.Length==0){wake.WaitOne();continue;}
            try{job.Run();}
            catch(Exception error) when(error is COMException||error is System.ComponentModel.Win32Exception
                ||error is InvalidOperationException||error is ArgumentException){/* Unknown native identity stays unmanaged. */}
            finally{lock(gate)pending.Remove(job.Key);}
        }
    }
    public void Dispose(){lock(gate){if(stopped)return;stopped=true;queue.Clear();pending.Clear();wake.Set();}/* Never wait on a blocked shell server. */}
}
