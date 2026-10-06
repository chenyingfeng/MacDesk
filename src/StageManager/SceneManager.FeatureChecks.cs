using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Interop;
using StageManager.Model;
using StageManager.Native;
using StageManager.Native.Window;
namespace StageManager;
public partial class SceneManager
{
    internal static async Task CheckGroupTransactionsAsync()
    {
        HwndSource Fixture(int x) {
            var source=new HwndSource(new HwndSourceParameters("CampusStage group fixture") {
            WindowStyle=unchecked((int)0x90CF0000),ExtendedWindowStyle=0x08000080,
            PositionX=x,PositionY=-20000,Width=900,Height=650});
            source.RootVisual=new System.Windows.Controls.Border {Width=900,Height=650};
            StageManager.Native.PInvoke.Win32.ShowWindow(source.Handle,StageManager.Native.PInvoke.Win32.SW.SW_SHOWNOACTIVATE);
            return source;
        }
        using var first=Fixture(-21000);using var second=Fixture(-20000);
        var a=new FeatureFixtureWindow(new WindowsWindow(first.Handle));
        var b=new FeatureFixtureWindow(new WindowsWindow(second.Handle));
        using var manager=new SceneManager(new WindowsManager()) {KeepWindowsVisible=true,OneWindowPerApp=false};
        var s1=new Scene("fixture-a",a);var s2=new Scene("fixture-b",b);
        manager._scenes.AddRange([s1,s2]);manager._stage.Seed(s1);manager._stage.Seed(s2);manager._current=s2;
        await manager.SetStageModeAsync(false,true);
        await manager.ActivateFromSidebar(s1);
        await manager.ActivateDockWindowAsync(b.Handle,b.ProcessId);
        if(manager.MacMode || !manager.KeepWindowsVisible || manager.OneWindowPerApp
            ||manager._stage.Items.Count!=2 || !manager._stage.Contains(s1)||!manager._stage.Contains(s2)
            ||StageManager.Strategies.OpacityWindowStrategy.TryGetOriginalPosition(a.Handle,out _,out _)
            ||!StageManager.Native.PInvoke.Win32.IsWindowVisible(a.Handle))
            throw new InvalidOperationException("Sidebar and Dock selection hid an earlier app in the daily multi-window profile");
        if(manager.FeatureWindows.Count!=2)throw new InvalidOperationException($"Fixture precondition: tracked={manager.FeatureWindows.Count},a={a.Handle}/{a.ProcessId}/{Services.QqWindowPolicy.Retain(a)},b={b.Handle}/{b.ProcessId}/{Services.QqWindowPolicy.Retain(b)}");
        var task=await manager.CreateTaskGroupAsync("研究",[a,b,a]);
        if(manager.GetScenes().Count()!=1||task.Windows.Count()!=2||task.CustomName!="研究"||manager._current!=task)
            throw new InvalidOperationException($"Task grouping duplicate/source removal/current state: scenes={manager.GetScenes().Count()},members={task.Windows.Count()},name={task.CustomName},current={manager._current==task}");
        if(manager.Presented(task).Length!=2)throw new InvalidOperationException("Daily profile hid a member of an application");
        manager.OneWindowPerApp=true;
        if(manager.Presented(task).Length!=1||manager.Presented(task,b)[0].Handle!=b.Handle
            ||manager.Presented(task,null,true)[0].Handle!=a.Handle)
            throw new InvalidOperationException("Scene one-per-app/explicit member/cycle integration");
        await manager.ToggleDesktopAsync();
        if(!manager.IsDesktopView)throw new InvalidOperationException("Desktop stow state");
        await manager.ToggleDesktopAsync();
        if(manager.IsDesktopView||manager._current!=task)throw new InvalidOperationException("Desktop return task memory");
        manager.OneWindowPerApp=false;
        await manager.SplitTaskGroupAsync(task);
        if(manager.GetScenes().Count()!=1||manager.GetScenes().Single().Windows.Count()!=2
            ||manager.GetScenes().Single().CustomName!=null||manager._stage.Items.Count!=1)
            throw new InvalidOperationException("Split task into application group/current stage");
        var updated=await manager.CreateTaskGroupAsync("修改",[a]);
        if(manager.GetScenes().Count()!=2||updated.Windows.Count()!=1
            ||manager.GetScenes().Single(s=>s!=updated).Windows.Single().Handle!=b.Handle)
            throw new InvalidOperationException("Updating members lost an unselected window");
        first.Dispose();manager.WindowsManager_WindowDestroyed(a);
        if(manager.GetScenes().Count()!=1||manager._stage.Items.Count!=1||manager._current==updated)
            throw new InvalidOperationException("Closing a named task lost other stage groups");
        await manager.ActivateDockWindowAsync(b.Handle,b.ProcessId+1);
        var dockTarget=manager.FindSceneForWindow(b);
        await manager.ActivateDockWindowAsync(b.Handle,b.ProcessId);
        if(manager._current!=dockTarget)throw new InvalidOperationException("Dock selection failed shared owned-window restoration queue");
        using var third=Fixture(-23000);using var fourth=Fixture(-24000);
        var c=new FeatureFixtureWindow(new WindowsWindow(third.Handle));
        var d=new FeatureFixtureWindow(new WindowsWindow(fourth.Handle));
        using var mac=new SceneManager(new WindowsManager()) {KeepWindowsVisible=true};
        var sc=new Scene("fixture-c",c);var sd=new Scene("fixture-d",d);
        mac._scenes.AddRange([sc,sd]);mac._stage.Seed(sc);mac._stage.Seed(sd);mac._current=sd;
        await mac.SetStageModeAsync(true,true);
        if(!mac.MacMode||mac.KeepWindowsVisible||mac._stage.Items.Count!=1||mac._current!=sd)
            throw new InvalidOperationException("Mac mode did not isolate current group");
        await mac.MoveWindow(sc,c,sd);
        if(mac.GetScenes().Count()!=1||sd.Windows.Count()!=2||mac._current!=sd)
            throw new InvalidOperationException("Mac drag merge source removal/current group");
        var separated=await mac.SeparateWindowToNewSceneAsync(c);
        if(separated==null||separated==sd||sd.Windows.Count()!=1||mac._stage.Contains(separated)||mac._current!=sd)
            throw new InvalidOperationException("Mac reverse drag stowed whole task or lost member");
        await mac.MoveWindow(separated,c,sd);
        if(mac.GetScenes().Count()!=1||sd.Windows.Count()!=2)
            throw new InvalidOperationException("Mac merge after reverse drag lost membership");
        await mac.SplitTaskGroupAsync(sd);
        sd=mac.GetScenes().Single();
        if(mac._stage.Items.Count!=1||mac._current!=sd)
            throw new InvalidOperationException("Splitting task broke Mac mode isolation");
        await mac.SeparateWindowToNewSceneAsync(c);
        var last=await mac.SeparateWindowToNewSceneAsync(d);
        if(last!=sd||mac._stage.Items.Count!=0||mac._current!=null||!mac.GetScenes().Contains(sd))
            throw new InvalidOperationException("Mac last member drag did not return group to sidebar");
        await mac.SetStageModeAsync(false,true);
        if(mac.MacMode||!mac.KeepWindowsVisible)throw new InvalidOperationException("Multi-window mode cannot be restored");
    }
    // Native geometry/state is real; focus and UI callbacks are deliberately inert.
    private sealed class FeatureFixtureWindow(IWindow inner):IWindow
    {
        public IntPtr Handle=>inner.Handle;public string Title=>"Fixture";public string Class=>inner.Class;
        public IWindowLocation Location=>inner.Location;public Rectangle Offset=>inner.Offset;
        public int ProcessId=>inner.ProcessId;public string ProcessFileName=>"fixture.exe";public string ProcessName=>"fixture";
        public bool CanLayout=>true;public bool IsFocused=>false;public bool IsMinimized=>inner.IsMinimized;
        public bool IsMaximized=>inner.IsMaximized;public bool IsMouseMoving=>false;
        public event IWindowDelegate? WindowClosed {add{} remove{}}public event IWindowDelegate? WindowUpdated {add{} remove{}}
        public event IWindowDelegate? WindowFocused {add{} remove{}}
        public void Focus(){}public void BringToTop(){}public void NotifyUpdated(){}
        public void Hide()=>inner.Hide();public void ShowNormal()=>inner.ShowNormal();public void ShowMaximized()=>inner.ShowMaximized();
        public void ShowMinimized()=>inner.ShowMinimized();public void ShowInCurrentState()=>inner.ShowInCurrentState();
        public void Close()=>throw new InvalidOperationException("Fixture close not expected");
    }
}
