using System.Collections.Generic;
using System.Linq;
using StageManager.Model;
using StageManager.Native.Window;

namespace StageManager.Services
{
    internal static class SceneBindings
    {
        internal readonly record struct Result(Scene Scene,bool Created,bool Added);
        // Register visible app windows independently of foreground timing. Repeated
        // show/foreground/reconciliation events must not duplicate handles or groups.
        internal static Result Bind(List<Scene> scenes,IWindow window,string workspace="")
        {
            var bound=scenes.FirstOrDefault(s=>s.Windows.Any(w=>w.Handle==window.Handle));
            if(bound!=null) return new Result(bound,false,false);
            var key=window.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)+(workspace.Length>0?"@"+workspace:"");
            var local=scenes.Where(s=>s.WorkspaceKey==workspace);
            var group=local.FirstOrDefault(s=>s.IsSelected && s.Windows.Any(w=>w.ProcessId==window.ProcessId))
                ?? local.FirstOrDefault(s=>s.Key==key)
                // Metadata can resolve an initially unknown desktop, or an app can move
                // to another workspace. Immutable historical keys must not split an
                // existing ordinary process group once its live scope is known.
                ?? local.FirstOrDefault(s=>s.CustomName==null&&s.Windows.Any()
                    &&s.Windows.All(w=>w.ProcessId==window.ProcessId))
                ?? local.FirstOrDefault(s=>s.CustomName!=null && s.Windows.Any(w=>w.ProcessId==window.ProcessId));
            if(group!=null) {group.Add(window);return new Result(group,false,true);}
            group=new Scene(key,window){WorkspaceKey=workspace};scenes.Add(group);
            return new Result(group,true,true);
        }
    }
}
