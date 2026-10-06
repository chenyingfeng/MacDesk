using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using StageManager.Model;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Strategies;
namespace StageManager;
public partial class SceneManager
{
    internal bool OneWindowPerApp {get;set;}=PortablePreferences.Read("one-window-per-app");
    internal bool MacMode {get;private set;}=PortablePreferences.Read("mac-mode");
    internal async Task SetStageModeAsync(bool mac,bool keepOthers)
    {
        await _switchGate.WaitAsync();
        try {
            if(_disposed)return;MacMode=mac;KeepWindowsVisible=!mac&&keepOthers;
            var currents=_stage.Items.GroupBy(SceneWorkspace).Select(g=>g.Last()).ToArray();
            foreach(var current in currents)await SwitchToCore(current,true);
        }finally {_switchGate.Release();}
    }
    private void MergeDraggedWindow(Scene source,IWindow window,Scene target)
    {
        source.Remove(window);target.Add(window);
        if(source.Windows.Any())SceneChanged?.Invoke(this,new SceneChangedEventArgs(source,window,ChangeType.Updated));
        else {
            lock(_scenesLock)_scenes.Remove(source);_stage.Remove(source);
            if(ReferenceEquals(_lastScene,source))_lastScene=null;
            if(ReferenceEquals(_current,source)) {
                var prior=_current;_current=_stage.Current;
                CurrentSceneSelectionChanged?.Invoke(this,new CurrentSceneSelectionChangedEventArgs(prior,_current));
            }
            SceneChanged?.Invoke(this,new SceneChangedEventArgs(source,window,ChangeType.Removed));
        }
        SceneChanged?.Invoke(this,new SceneChangedEventArgs(target,window,ChangeType.Updated));
    }
    private Scene? SeparateWindowCore(IWindow window)
    {
        var source=FindSceneForWindow(window);
        if(source==null || !_stage.Contains(source) || !WindowRestore.SameWindow(window))return null;
        if(source.Windows.Count()<=1) {StowSceneCore(source);return source;}
        source.Remove(window);
        var separated=new Scene(GetWindowGroupKey(window),window);
        lock(_scenesLock)_scenes.Add(separated);
        SceneChanged?.Invoke(this,new SceneChangedEventArgs(source,window,ChangeType.Updated));
        SceneChanged?.Invoke(this,new SceneChangedEventArgs(separated,window,ChangeType.Created));
        WindowStrategy.Hide(window);return separated;
    }
    public async Task<Scene?> SeparateWindowToNewSceneAsync(IWindow window)
    {
        await _switchGate.WaitAsync();
        try {
            if(_disposed)return null;_suspend=true;
            return SeparateWindowCore(window);
        }finally {_suspend=false;_switchGate.Release();}
    }
    private readonly AppWindowSelection _appWindows=new();
    private Scene[] _desktopReturn=[];
    internal IReadOnlyList<IWindow> FeatureWindows=>GetScenes().SelectMany(s=>s.Windows)
        .Where(w=>WindowRestore.SameWindow(w) && QqWindowPolicy.Retain(w)).DistinctBy(w=>w.Handle).ToArray();
    internal void RenameTaskGroup(Scene scene,string name) {
        scene.Rename(name);
        if(scene.Windows.FirstOrDefault() is IWindow w)SceneChanged?.Invoke(this,new SceneChangedEventArgs(scene,w,ChangeType.Updated));
    }
    internal void SetDesktopIconsPreference(bool value) {
        _hideDesktopIcons=value;
        if(value && !IsDesktopView)_desktop.HideIcons();else _desktop.ShowIcons();
    }
    private IWindow[] Presented(Scene scene,IWindow? preferred=null,bool cycle=false)
    {
        if(!OneWindowPerApp)return scene.Windows.ToArray();
        return scene.Windows.GroupBy(w=>w.ProcessId).Select(g=> {
            var members=g.Where(w=>WindowRestore.SameWindow(w) && QqWindowPolicy.Retain(w)).ToArray();
            var focus=preferred??(_appWindows.Remembered(g.Key)==IntPtr.Zero?_lastFocusedWindow:null);
            var h=_appWindows.Pick(g.Key,members.Select(w=>w.Handle).ToArray(),
                focus?.ProcessId==g.Key?focus.Handle:IntPtr.Zero,cycle);
            return members.FirstOrDefault(w=>w.Handle==h);
        }).OfType<IWindow>().ToArray();
    }
    internal async Task ToggleDesktopAsync()
    {
        await _switchGate.WaitAsync();
        bool keep=KeepWindowsVisible;
        try {
            if(_disposed || WindowsManager.SystemNavigationActive)return;
            if(!IsDesktopView) {_desktopReturn=_stage.ActiveItems.ToArray();_workspaceReturn[_stage.ActiveScope]=_desktopReturn;await SwitchToCore(null,false);}
            else {
                KeepWindowsVisible=true;
                var remembered=_workspaceReturn.TryGetValue(_stage.ActiveScope,out var saved)?saved:_desktopReturn;
                var returning=remembered.Where(s=>GetScenes().Contains(s)&&SceneWorkspace(s)==_stage.ActiveScope).ToArray();
                foreach(var s in MacMode?returning.TakeLast(1):returning)await SwitchToCore(s,false);
            }
        } finally {KeepWindowsVisible=keep;_switchGate.Release();}
    }
    internal async Task<Scene> CreateTaskGroupAsync(string name,IWindow[] members)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>60)throw new ArgumentException("分组名需要 1–60 个字符。");
        await _switchGate.WaitAsync();
        try {
            if(_disposed)throw new InvalidOperationException("台前调度已退出。");
            members=members.Where(w=>FeatureWindows.Any(x=>x.Handle==w.Handle && x.ProcessId==w.ProcessId))
                .DistinctBy(w=>w.Handle).ToArray();
            if(members.Length==0)throw new InvalidOperationException("所选窗口已经关闭，请刷新列表。");
            if(members.Select(w=>FindSceneForWindow(w) is Scene s?SceneWorkspace(s):WorkspaceEnvironment.WindowKey(w)).Distinct().Count()>1)
                throw new InvalidOperationException("一个任务组属于同一显示器和虚拟桌面。请先把这些窗口移到同一屏幕。");
            _suspend=true;
            var group=new Scene("task:"+Guid.NewGuid().ToString("N"),members){WorkspaceKey=FindSceneForWindow(members[0]) is Scene s?SceneWorkspace(s):WorkspaceEnvironment.WindowKey(members[0])};group.Rename(name.Trim());
            foreach(var source in GetScenes()) {
                var removed=source.Windows.Where(w=>members.Any(m=>m.Handle==w.Handle)).ToArray();
                foreach(var w in removed)source.Remove(w);
                if(removed.Length==0)continue;
                if(!source.Windows.Any()) {
                    lock(_scenesLock)_scenes.Remove(source);
                    _stage.Remove(source);
                    if(ReferenceEquals(_lastScene,source))_lastScene=null;
                    SceneChanged?.Invoke(this,new SceneChangedEventArgs(source,removed[0],ChangeType.Removed));
                }else SceneChanged?.Invoke(this,new SceneChangedEventArgs(source,removed[0],ChangeType.Updated));
            }
            lock(_scenesLock)_scenes.Add(group);
            SceneChanged?.Invoke(this,new SceneChangedEventArgs(group,members[0],ChangeType.Created));
            foreach(var w in members)ForgetUserMinimized(w);
            await SwitchToCore(group,true,members[0]);return group;
        } finally {_suspend=false;_switchGate.Release();}
    }
    internal async Task SplitTaskGroupAsync(Scene scene)
    {
        await _switchGate.WaitAsync();
        try {
            if(_disposed || !GetScenes().Contains(scene))return;
            _suspend=true;
            var members=scene.Windows.ToArray();if(members.Length==0)return;
            bool staged=_stage.Contains(scene);_stage.Remove(scene);
            lock(_scenesLock)_scenes.Remove(scene);
            SceneChanged?.Invoke(this,new SceneChangedEventArgs(scene,members[0],ChangeType.Removed));
            var split=new List<Scene>();
            foreach(var part in members.GroupBy(GetWindowGroupKey)) {
                var s=new Scene(part.Key,part.ToArray());lock(_scenesLock)_scenes.Add(s);
                split.Add(s);if(staged&&!MacMode)_stage.Seed(s);
                SceneChanged?.Invoke(this,new SceneChangedEventArgs(s,part.First(),ChangeType.Created));
            }
            if(staged&&MacMode) {
                var selected=split.FirstOrDefault(s=>s.Windows.Any(w=>w.Handle==_lastFocusedWindow?.Handle))??split[0];
                await SwitchToCore(selected,true);return;
            }
            var prior=_current;_current=_stage.Current;
            CurrentSceneSelectionChanged?.Invoke(this,new CurrentSceneSelectionChangedEventArgs(prior,_current));
        }finally {_suspend=false;_switchGate.Release();}
    }
    internal WorkspacePreset CapturePreset(Scene scene)
    {
        if(_stage.Contains(scene))CaptureZOrder(scene.Windows.ToArray());
        var screen=WorkspaceEnvironment.ScreenFor(WorkspaceEnvironment.MonitorFromKey(SceneWorkspace(scene))).WorkingArea;
        var work=new Rect(screen.X,screen.Y,screen.Width,screen.Height);
        var all=FeatureWindows.OrderBy(w=>w.Handle.ToInt64()).ToArray();
        var items=scene.Windows.Where(w=>WindowRestore.SameWindow(w)).Select(w=> {
            var r=WorkspaceWindowGeometry.Bounds(w);
            if(OpacityWindowStrategy.TryGetOriginalPosition(w.Handle,out int x,out int y))r=new Rect(x,y,r.Width,r.Height);
            string process=Path.GetFileName(w.ProcessFileName);
            int ordinal=Array.FindIndex(all.Where(x=>string.Equals(Path.GetFileName(x.ProcessFileName),process,StringComparison.OrdinalIgnoreCase)).ToArray(),x=>x.Handle==w.Handle);
            int depth;lock(_zDepthLock)depth=_zDepth.TryGetValue(w.Handle,out var savedDepth)?savedDepth:Array.IndexOf(scene.Windows.ToArray(),w);
            return new WorkspaceMember(process,ordinal,(r.X-work.X)/work.Width,(r.Y-work.Y)/work.Height,
                r.Width/work.Width,r.Height/work.Height,w.IsMaximized,Math.Max(0,depth),w.Title.Trim(),WorkspaceRestoration.Target(w),WorkspaceEnvironment.MonitorFromKey(SceneWorkspace(scene)));
        }).ToArray();
        return new WorkspacePreset(scene.CustomName??"任务分组",items);
    }
    internal async Task<int> ApplyPresetAsync(WorkspacePreset preset,(WorkspaceMember Member,IWindow Window)[] matches)
    {
        matches=matches.Where(x=>WindowRestore.SameWindow(x.Window)&&WorkspaceEnvironment.IsCurrent(x.Window.Handle)&&!WorkspaceEnvironment.IsFullscreen(x.Window.Handle))
            .DistinctBy(x=>x.Window.Handle).ToArray();
        if(matches.Length==0)throw new InvalidOperationException("所选窗口已关闭或位于另一个虚拟桌面，请刷新核对。");
        await _switchGate.WaitAsync();
        try {
            if(_disposed)return 0;_suspend=true;
            foreach(var pair in matches) {
                var w=pair.Window;if(!WindowRestore.SameWindow(w)||!QqWindowPolicy.Retain(w))continue;
                await WindowRestore.RunAsync(w,true);await WindowRestore.NormalizeAsync(w);
                var screen=WorkspaceEnvironment.ScreenFor(pair.Member.Monitor??WorkspaceEnvironment.ActiveMonitor).WorkingArea;
                var r=WorkspacePresets.Map(pair.Member,new Rect(screen.X,screen.Y,screen.Width,screen.Height));
                OpacityWindowStrategy.ForgetOriginalPosition(w.Handle);ForgetUserMinimized(w);
                if(!Win32.SetWindowPos(w.Handle,IntPtr.Zero,(int)r.X,(int)r.Y,(int)r.Width,(int)r.Height,
                    Win32.SetWindowPosFlags.IgnoreZOrder|Win32.SetWindowPosFlags.DoNotActivate|Win32.SetWindowPosFlags.AsynchronousWindowPosition))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),"应用未接受保存的布局。");
                await Task.Delay(120);
                if(pair.Member.Maximized)Win32.ShowWindowAsync(w.Handle,Win32.SW.SW_SHOWMAXIMIZED);
            }
            lock(_zDepthLock)foreach(var pair in matches)_zDepth[pair.Window.Handle]=pair.Member.ZOrder;
        }finally {_suspend=false;_switchGate.Release();}
        foreach(var pair in matches)await RefreshWindowWorkspaceAsync(pair.Window);
        foreach(var part in matches.GroupBy(x=>WorkspaceEnvironment.WindowKey(x.Window))) {
            var scene=await CreateTaskGroupAsync(preset.Name,part.Select(x=>x.Window).ToArray());
            var front=part.OrderBy(x=>x.Member.ZOrder).First().Window;
            await SwitchToSerialized(scene,true,front);
        }
        return matches.Length;
    }
}
