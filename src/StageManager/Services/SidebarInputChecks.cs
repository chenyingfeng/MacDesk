using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StageManager.Controls;

namespace StageManager.Services;
internal static class SidebarInputChecks
{
    internal static void Run()
    {
        var gesture=new SidebarPointerGesture();
        var p=new Point(90,50);
        gesture.Begin(p,100);
        if (!gesture.End(new Point(94,53),100) || gesture.End(p,100)) throw new InvalidOperationException("Click/jitter/duplicate release");
        gesture.Begin(p,100);gesture.Move(new Point(90,80));gesture.Move(p);
        if (gesture.End(p,100)) throw new InvalidOperationException("Movement returning to origin reopened a card");
        gesture.Begin(p,100);
        if (gesture.IsOutwardDrag(new Point(89,80)) || !gesture.IsOutwardDrag(new Point(65,55))) throw new InvalidOperationException("Vertical movement confused with outward drag");
        if (gesture.End(new Point(65,55),100)) throw new InvalidOperationException("Drag also activated a card");
        gesture.Begin(p,100);
        if (gesture.End(p,103)) throw new InvalidOperationException("Scrolling activated a card");
        gesture.Begin(p,100);gesture.Cancel();
        if (gesture.End(p,100)) throw new InvalidOperationException("Lost capture activated a card");

        // WPF routed input, including an already-handled descendant event: no user's HWNDs/input involved.
        var child=new Border();var viewport=new ScrollViewer {Content=child};
        int down=0,up=0;
        using (new SidebarInputRoutes(viewport,(_,e)=>{down++;e.Handled=true;},(_,_)=>{},(_,e)=>{up++;e.Handled=true;})) {
            foreach (var kind in new[]{UIElement.PreviewMouseDownEvent,UIElement.PreviewMouseUpEvent}) {
                var e=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left) {RoutedEvent=kind,Handled=true};
                child.RaiseEvent(e);
            }
        }
        if (down!=1 || up!=1) throw new InvalidOperationException($"Viewport lost handled preview click input: down={down}, up={up}");
    }
}
