using System;
using System.Collections.Generic;
using System.Linq;

namespace StageManager.Services;

// Only visibility is borrowed. Never change appbar state, work area or registry settings.
internal sealed class TaskbarVisibilityLease : IDisposable
{
    internal readonly record struct Target(IntPtr Handle, uint ProcessId);
    private readonly Func<IEnumerable<Target>> enumerate;
    private readonly Func<Target, bool> valid;
    private readonly Func<IntPtr, bool> visible;
    private readonly Func<IntPtr, bool, bool> setVisible;
    private readonly HashSet<Target> owned = new();
    private readonly Dictionary<Target,bool> original = new();
    private bool disposed;
    internal int HiddenCount => owned.Count(t => valid(t) && !visible(t.Handle));
    internal TaskbarVisibilityLease(Func<IEnumerable<Target>> enumerate, Func<Target,bool> valid,
        Func<IntPtr,bool> visible, Func<IntPtr,bool,bool> setVisible)
    { this.enumerate=enumerate;this.valid=valid;this.visible=visible;this.setVisible=setVisible; }
    internal void Refresh(bool reveal=false)
    {
        if(disposed)return;
        owned.RemoveWhere(t=>!valid(t));
        foreach(var target in original.Keys.ToArray())if(!valid(target))original.Remove(target);
        foreach(var target in enumerate()) {
            if(!valid(target))continue;
            if(!original.ContainsKey(target))original[target]=visible(target.Handle);
            if(!original[target])continue;
            if(reveal) {
                if(owned.Contains(target) && !visible(target.Handle))setVisible(target.Handle,true);
                continue;
            }
            if(!visible(target.Handle))continue;
            // Record only successful requests; a pre-existing hidden bar stays untouched.
            if(setVisible(target.Handle,false))owned.Add(target);
        }
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        foreach(var target in owned)if(valid(target))setVisible(target.Handle,true);
    }
    internal bool RestorationComplete => owned.All(t=>!valid(t) || visible(t.Handle));
    internal void RetryRestore()
    { foreach(var target in owned)if(valid(target) && !visible(target.Handle))setVisible(target.Handle,true); }
}
