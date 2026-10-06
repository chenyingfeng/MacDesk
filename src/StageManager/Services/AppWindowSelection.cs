using System;
using System.Collections.Generic;
using System.Linq;
namespace StageManager.Services;
// Process identity, not captions. Selection memory is scoped to live HWNDs in this session.
internal sealed class AppWindowSelection
{
    private readonly Dictionary<int,IntPtr> _last=new();
    internal IntPtr Pick(int process,IntPtr[] handles,IntPtr preferred,bool cycle)
    {
        if(handles.Length==0)return IntPtr.Zero;
        _last.TryGetValue(process,out var previous);
        var chosen=handles.Contains(preferred)?preferred:handles.Contains(previous)?previous:handles[0];
        if(cycle && handles.Contains(previous) && preferred==IntPtr.Zero)
            chosen=handles[(Array.IndexOf(handles,previous)+1)%handles.Length];
        _last[process]=chosen;return chosen;
    }
    internal IntPtr Remembered(int process)=>_last.TryGetValue(process,out var h)?h:IntPtr.Zero;
}
