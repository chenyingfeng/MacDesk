using System;
using System.Windows;
using System.Windows.Input;

namespace StageManager.Controls;

// Listen on the viewport, before descendants can consume a click. No global hook or input injection.
internal sealed class SidebarInputRoutes : IDisposable
{
    private readonly UIElement root;
    private readonly MouseButtonEventHandler down, up;
    private readonly MouseEventHandler move;
    internal SidebarInputRoutes(UIElement root, MouseButtonEventHandler down, MouseEventHandler move, MouseButtonEventHandler up)
    {
        this.root=root;
        this.down=(sender,e)=> {if(e.ChangedButton==MouseButton.Left)down(sender,e);};
        this.up=(sender,e)=> {if(e.ChangedButton==MouseButton.Left)up(sender,e);};
        this.move=move;
        root.AddHandler(UIElement.PreviewMouseDownEvent,this.down,true);
        root.AddHandler(UIElement.PreviewMouseMoveEvent,move,true);
        root.AddHandler(UIElement.PreviewMouseUpEvent,this.up,true);
    }
    public void Dispose()
    {
        root.RemoveHandler(UIElement.PreviewMouseDownEvent,down);
        root.RemoveHandler(UIElement.PreviewMouseMoveEvent,move);
        root.RemoveHandler(UIElement.PreviewMouseUpEvent,up);
    }
}

internal sealed class SidebarPointerGesture
{
    private Point start;
    private double offset;
    private bool active, moved;
    internal void Begin(Point point,double scrollOffset) {start=point;offset=scrollOffset;active=true;moved=false;}
    internal void Move(Point point)
    {
        if (Math.Abs(point.X-start.X)>=10 || Math.Abs(point.Y-start.Y)>=10) moved=true;
    }
    internal bool IsOutwardDrag(Point point) => active && start.X-point.X>=10
        && start.X-point.X>Math.Abs(point.Y-start.Y);
    internal bool End(Point point,double scrollOffset)
    {
        Move(point);
        bool click=active && !moved && Math.Abs(offset-scrollOffset)<0.5;
        Cancel();return click;
    }
    internal void Cancel() {active=false;}
}
