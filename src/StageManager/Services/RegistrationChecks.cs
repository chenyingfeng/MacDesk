using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using StageManager.Model;
using StageManager.Native.Window;

namespace StageManager.Services
{
    internal static class RegistrationChecks
    {
        internal static void Run()
        {
            var scenes=new List<Scene>();
            // Reproduce a new app that raises SHOW before obtaining focus.
            var unfocused=new CheckWindow(100,10,false);
            var first=SceneBindings.Bind(scenes,unfocused);
            if(!first.Created || scenes.Count!=1 || first.Scene.Windows.Count()!=1)
                throw new InvalidOperationException("Unfocused new app omitted from sidebar");
            // Foreground and reconciliation can arrive after SHOW, and may use a fresh wrapper.
            SceneBindings.Bind(scenes,new CheckWindow(100,10,true));
            SceneBindings.Bind(scenes,unfocused);
            if(scenes.Count!=1 || first.Scene.Windows.Count()!=1)
                throw new InvalidOperationException("SHOW/foreground duplicated a window");
            SceneBindings.Bind(scenes,new CheckWindow(101,10,false));
            if(first.Scene.Windows.Count()!=2) throw new InvalidOperationException("App grouping");
            SceneBindings.Bind(scenes,new CheckWindow(102,11,false));
            if(scenes.Count!=2) throw new InvalidOperationException("Independent new app grouping");
            var mixed=new Scene("task:fixture",new CheckWindow(103,10,false),new CheckWindow(104,12,false));
            mixed.Rename("研究");mixed.IsSelected=true;scenes.Add(mixed);
            var member=SceneBindings.Bind(scenes,new CheckWindow(105,10,false));
            if(member.Scene!=mixed||!member.Added||member.Created||first.Scene.Windows.Count()!=2)
                throw new InvalidOperationException("New app window escaped active mixed task");
            var resolved=new List<Scene>();
            var warming=SceneBindings.Bind(resolved,new CheckWindow(110,20,false),"unknown|display").Scene;
            warming.WorkspaceKey="resolved|display";
            var late=SceneBindings.Bind(resolved,new CheckWindow(111,20,false),"resolved|display");
            if(late.Scene!=warming||late.Created||warming.Windows.Count()!=2||resolved.Count!=1)
                throw new InvalidOperationException("Resolved desktop metadata duplicates an unstaged app group");
            SceneBindings.Bind(resolved,new CheckWindow(112,20,false),"other-desktop|display");
            if(resolved.Count!=2)throw new InvalidOperationException("Live workspace process fallback merges foreign desktop groups");
            var drop=RightSidebarGeometry.DropBounds(new System.Windows.Rect(0,0,1920,1080),
                new System.Windows.Point(960,540),new System.Windows.Size(800,600));
            if(drop.X!=560 || drop.Y!=468 || drop.Width!=800 || drop.Height!=600)
                throw new InvalidOperationException("Center drop positioning");
            var negative=RightSidebarGeometry.DropBounds(new System.Windows.Rect(-1920,-300,1920,1080),
                new System.Windows.Point(200,900),new System.Windows.Size(5000,4000));
            if(negative.Left < -1920 || negative.Top < -300 || negative.Right > 0 || negative.Bottom > 780)
                throw new InvalidOperationException("Oversized/negative-monitor drop bounds");
        }

        private sealed class CheckWindow : IWindow
        {
            internal CheckWindow(int handle,int process,bool focused)
            {Handle=new IntPtr(handle);ProcessId=process;IsFocused=focused;}
            public IntPtr Handle {get;}
            public int ProcessId {get;}
            public bool IsFocused {get;}
            public string Title=>"Registration fixture";
            public string Class=>"CustomMainWindow";
            public string ProcessFileName=>"test.exe";
            public string ProcessName=>"test";
            public bool CanLayout=>true;
            public bool IsMinimized=>false;
            public bool IsMaximized=>false;
            public bool IsMouseMoving=>false;
            public IWindowLocation Location=>new WindowLocation(0,0,800,600,WindowState.Normal);
            public Rectangle Offset=>Rectangle.Empty;
            public event IWindowDelegate? WindowClosed {add{} remove{}}
            public event IWindowDelegate? WindowUpdated {add{} remove{}}
            public event IWindowDelegate? WindowFocused {add{} remove{}}
            private static void Unexpected() => throw new InvalidOperationException("Registering an unfocused app attempted a window operation");
            public void Focus()=>Unexpected(); public void Hide()=>Unexpected();
            public void ShowNormal()=>Unexpected(); public void ShowMaximized()=>Unexpected();
            public void ShowMinimized()=>Unexpected(); public void ShowInCurrentState()=>Unexpected();
            public void BringToTop()=>Unexpected(); public void Close()=>Unexpected();
            public void NotifyUpdated()=>Unexpected();
        }
    }
}
