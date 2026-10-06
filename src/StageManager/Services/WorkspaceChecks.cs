using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;
using StageManager.Model;
using StageManager.Native.Window;
namespace StageManager.Services;
internal static class WorkspaceChecks
{
    internal static void Run()
    {
        var requestTime=DateTime.UtcNow;var requestToken=Guid.NewGuid().ToString("N");
        if(!DockBridge.ValidRequest(requestToken,requestTime,12,100,requestTime)
            ||DockBridge.ValidRequest(requestToken,requestTime,12,100,requestTime.AddSeconds(6))
            ||DockBridge.ValidRequest("bad",requestTime,12,100,requestTime)
            ||DockBridge.ValidRequest(requestToken,requestTime,0,100,requestTime))
            throw new InvalidOperationException("Dock activation identity/token/expiration validation");
        var a=new Scene("a"){WorkspaceKey="desktop1|left"};var b=new Scene("b"){WorkspaceKey="desktop1|right"};
        var c=new Scene("c"){WorkspaceKey="desktop1|right"};var d=new Scene("d"){WorkspaceKey="desktop2|right"};
        var stages=new StageSelection<Scene>(s=>s.WorkspaceKey);stages.Seed(a);stages.Seed(b);stages.Seed(d);
        stages.Select(c,false);
        if(!stages.Items.SequenceEqual(new[]{a,d,c})||stages.Current!=c||stages.ActiveItems.Count!=1)
            throw new InvalidOperationException("Switch on one display/desktop affected another workspace");
        stages.Select(null,false);
        if(!stages.Items.SequenceEqual(new[]{a,d})||stages.Current!=null)throw new InvalidOperationException("Desktop reveal stowed other workspaces");
        stages.ActiveScope=d.WorkspaceKey;if(stages.Current!=d)throw new InvalidOperationException("Virtual desktop selection memory");
        stages.Select(c,true);stages.Select(b,true);
        if(stages.ActiveItems.Count!=2||stages.Items.Count!=4)throw new InvalidOperationException("Multi-window mode lost workspace isolation");
        if(WorkspaceEnvironment.CoversDisplay(new Rect(0,0,1920,1040),new Rect(0,0,1920,1080),false,false)
            ||WorkspaceEnvironment.CoversDisplay(new Rect(0,0,1920,1080),new Rect(0,0,1920,1080),true,false)
            ||!WorkspaceEnvironment.CoversDisplay(new Rect(-1920,-200,1920,1080),new Rect(-1920,-200,1920,1080),false,false))
            throw new InvalidOperationException("Fullscreen/ordinary maximize/negative display classification");
        if(!WorkspaceSwitchPolicy.MayPark("a","a",false,true,false)
            ||WorkspaceSwitchPolicy.MayPark("a","b",false,true,false)
            ||WorkspaceSwitchPolicy.MayPark("a","a",true,true,false)
            ||WorkspaceSwitchPolicy.MayPark("a","a",false,false,false)
            ||WorkspaceSwitchPolicy.MayPark("a","a",false,true,true))throw new InvalidOperationException("Foreign display/desktop/fullscreen park policy");
        var scenes=new List<Scene>();var left=new MatchWindow(1,"left");var right=new MatchWindow(2,"right");
        SceneBindings.Bind(scenes,left,a.WorkspaceKey);SceneBindings.Bind(scenes,right,b.WorkspaceKey);
        if(scenes.Count!=2)throw new InvalidOperationException("Same-process windows on different displays were merged");
        var member=new WorkspaceMember("fixture.exe",0,0,0,.5,.5,false,TitleHint:"文件 B");
        var first=new MatchWindow(3,"文件 A");var second=new MatchWindow(4,"文件 B");
        var preset=new WorkspacePreset("写作",[member]);
        var plan=WorkspaceRestoration.Plan(preset,[first,second]);
        if(plan[0].Suggested!=second)throw new InvalidOperationException("Restart title matching trusted old window ordinal");
        if(WorkspaceRestoration.Plan(preset,[second,new MatchWindow(5,"文件 B")])[0].Suggested!=null
            ||WorkspaceRestoration.Plan(preset,[first])[0].Suggested!=null
            ||WorkspaceRestoration.Plan(preset with {Members=[member with {TitleHint=null}]},[first])[0].Suggested!=null)
            throw new InvalidOperationException("Ambiguous/changed/legacy title silently matched another document");
        if(!DocumentTarget.Valid(@"C:\fixtures\notes.docx")||!DocumentTarget.Valid("https://example.org/project")
            ||DocumentTarget.Valid(@"C:\fixtures\tool.exe")||DocumentTarget.Valid(@"C:\fixtures\notes.pdf.bat")
            ||DocumentTarget.Valid("javascript:alert(1)")||DocumentTarget.Valid("https://user:secret@example.org/")
            ||DocumentTarget.Valid(@"..\notes.pdf"))throw new InvalidOperationException("Invalid document/credential/shell target accepted");
        var start=new Rect(-1900,-100,160,100);var end=new Rect(-1800,0,800,600);
        if(WindowMotion.Frame(start,end,0)!=start||WindowMotion.Frame(start,end,1)!=end
            ||!new Rect(-1900,-100,900,700).Contains(WindowMotion.Frame(start,end,.5)))
            throw new InvalidOperationException("Bounded animation endpoints/interpolation");
    }
    private sealed class MatchWindow(int id,string title):IWindow
    {
        public IntPtr Handle=>new(id);public string Title=>title;public int ProcessId=>10;
        public string ProcessFileName=>"fixture.exe";public string ProcessName=>"fixture";public string Class=>"fixture";
        public IWindowLocation Location=>new WindowLocation(0,0,800,600,Native.Window.WindowState.Normal);
        public Rectangle Offset=>Rectangle.Empty;public bool CanLayout=>true;public bool IsFocused=>false;
        public bool IsMinimized=>false;public bool IsMaximized=>false;public bool IsMouseMoving=>false;
        public event IWindowDelegate? WindowClosed {add{} remove{}}public event IWindowDelegate? WindowUpdated {add{} remove{}}
        public event IWindowDelegate? WindowFocused {add{} remove{}}
        public void Focus()=>throw new NotSupportedException();public void Hide()=>throw new NotSupportedException();
        public void ShowNormal()=>throw new NotSupportedException();public void ShowMaximized()=>throw new NotSupportedException();
        public void ShowMinimized()=>throw new NotSupportedException();public void ShowInCurrentState()=>throw new NotSupportedException();
        public void BringToTop()=>throw new NotSupportedException();public void Close()=>throw new NotSupportedException();public void NotifyUpdated(){}
    }
}
internal static class WorkspaceSwitchPolicy
{
    internal static bool MayPark(string active,string window,bool kept,bool currentDesktop,bool fullscreen)=>
        currentDesktop&&!fullscreen&&!kept&&active==window;
}
