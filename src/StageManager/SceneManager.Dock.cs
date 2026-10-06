using System;
using System.Linq;
using System.Threading.Tasks;
using StageManager.Services;
namespace StageManager;
public partial class SceneManager
{
    internal Task ActivateDockWindowAsync(IntPtr handle,int pid) {
        var window=FeatureWindows.FirstOrDefault(w=>w.Handle==handle&&w.ProcessId==pid);
        if(window==null||!WindowRestore.SameWindow(window)||!WorkspaceEnvironment.IsCurrent(handle))return Task.CompletedTask;
        // Dock selections share the Task View/taskbar restore queue, including minimized QQ.
        return _systemChoices.RequestAsync(window);
    }
}
