using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StageManager.Model;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Strategies;
namespace StageManager;
public partial class SceneManager
{
    private readonly Dictionary<string,Scene[]> _workspaceReturn=new();
    internal string SceneWorkspace(Scene scene) {
        if(scene.WorkspaceKey.Length==0 && scene.Windows.FirstOrDefault() is IWindow w)
            scene.WorkspaceKey=WorkspaceEnvironment.WindowKey(w);
        return scene.WorkspaceKey;
    }
    internal bool InSidebarWorkspace(Scene scene,string monitor)=>
        scene.Windows.Any(w=>WorkspaceEnvironment.IsCurrent(w.Handle))
        && WorkspaceEnvironment.MonitorFromKey(SceneWorkspace(scene))==monitor;
    internal void SelectWorkspace(string monitor) {
        WorkspaceEnvironment.ActiveMonitor=monitor;
        _stage.ActiveScope=WorkspaceEnvironment.ActiveKey;_current=_stage.Current;
    }
    internal async Task RefreshWindowWorkspaceAsync(IWindow window)
    {
        if(_disposed||_suspend||!WorkspaceEnvironment.IsCurrent(window.Handle))return;
        var known=FindSceneForWindow(window);if(known==null||SceneWorkspace(known)==WorkspaceEnvironment.WindowKey(window))return;
        await _switchGate.WaitAsync();
        try {
            var source=FindSceneForWindow(window);if(source==null||_disposed)return;
            var key=WorkspaceEnvironment.WindowKey(window);
            if(SceneWorkspace(source)==key)return;
            foreach(var member in source.Windows) {
                if(!OpacityWindowStrategy.TryGetOriginalPosition(member.Handle,out int savedX,out int savedY))continue;
                var size=WorkspaceWindowGeometry.Bounds(member);
                var original=new System.Drawing.Rectangle(savedX,savedY,(int)size.Width,(int)size.Height);
                if(System.Windows.Forms.Screen.AllScreens.Any(s=>s.WorkingArea.IntersectsWith(original)))continue;
                var area=WorkspaceEnvironment.ScreenFor(WorkspaceEnvironment.MonitorFromKey(key)).WorkingArea;
                OpacityWindowStrategy.SetRestorePosition(member.Handle,
                    Math.Clamp(savedX,area.Left,area.Right-(int)Math.Min(size.Width,area.Width)),
                    Math.Clamp(savedY,area.Top,area.Bottom-(int)Math.Min(size.Height,area.Height)));
            }
            if(source.Windows.All(w=>WorkspaceEnvironment.WindowKey(w)==key)) {
                var prior=_current;
                source.WorkspaceKey=key;
                _current=_stage.Current;
                foreach(var scene in GetScenes())if(SceneWorkspace(scene)==_stage.ActiveScope)scene.IsSelected=ReferenceEquals(scene,_current);
                SceneChanged?.Invoke(this,new SceneChangedEventArgs(source,window,ChangeType.Updated));
                if(!ReferenceEquals(prior,_current))CurrentSceneSelectionChanged?.Invoke(this,new CurrentSceneSelectionChangedEventArgs(prior,_current));
                if(window.IsFocused)await SwitchToCore(source,true,window);
                return;
            }
            bool staged=_stage.Contains(source);source.Remove(window);
            if(!source.Windows.Any()) {
                lock(_scenesLock)_scenes.Remove(source);_stage.Remove(source);
                SceneChanged?.Invoke(this,new SceneChangedEventArgs(source,window,ChangeType.Removed));
            }else SceneChanged?.Invoke(this,new SceneChangedEventArgs(source,window,ChangeType.Updated));
            SceneBindings.Result binding;lock(_scenesLock)binding=SceneBindings.Bind(_scenes,window,key);
            if(staged)_stage.Seed(binding.Scene);
            SceneChanged?.Invoke(this,new SceneChangedEventArgs(binding.Scene,window,binding.Created?ChangeType.Created:ChangeType.Updated));
            _current=_stage.Current;
            if(window.IsFocused)await SwitchToCore(binding.Scene,true,window);
        }finally {_switchGate.Release();}
    }
}
