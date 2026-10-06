using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using StageManager.Controls;
using StageManager.Composition;
using StageManager.Native;
using StageManager.Native.PInvoke;

namespace StageManager.Services;

// Manual/Computer Use integration fixture. Only its own HWNDs are rendered; no window manager or hooks.
internal sealed class SidebarInputFixture : Window
{
    readonly string output;
    readonly ScrollViewer viewport;
    readonly List<Border> cards=new();
    readonly List<DwmPreviewSurface> previews=new();
    readonly SidebarPointerGesture gesture=new();
    readonly TextBlock status;
    SidebarInputRoutes? routes;
    HwndSource? source;
    Border? pressed;
    Point start;
    bool dragging;
    int presses,releases,clicks,drags,scrolls;
    internal SidebarInputFixture(string output)
    {
        this.output=output;
        Title="CampusStage sidebar input fixture";
        Width=480;Height=620;Left=400;Top=90;
        WindowStyle=WindowStyle.SingleBorderWindow;AllowsTransparency=false;ShowInTaskbar=true;
        Background=Brushes.White;
        var root=new Grid {Background=Brushes.White};
        root.RowDefinitions.Add(new RowDefinition {Height=new GridLength(65)});
        root.RowDefinitions.Add(new RowDefinition());
        status=new TextBlock {Text="Sidebar input fixture — own windows only",Margin=new Thickness(14),Foreground=Brushes.Black};
        root.Children.Add(status);
        viewport=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,
            PanningMode=PanningMode.VerticalOnly,Background=new SolidColorBrush(Color.FromArgb(1,0,0,0)),Focusable=true};
        Grid.SetRow(viewport,1);root.Children.Add(viewport);
        var stack=new StackPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,22,8)};
        viewport.Content=stack;
        for(int i=0;i<8;i++) {
            var card=new Border {Width=200,Height=140,Margin=new Thickness(0,0,0,28),Background=Brushes.Purple,
                Tag=i,Child=new TextBlock {Text="Card "+i,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center}};
            stack.Children.Add(card);cards.Add(card);
        }
        Content=root;
        Loaded+=(_,_)=> {
            source=new HwndSource(new HwndSourceParameters("Input fixture own preview source") {
                WindowStyle=unchecked((int)0x90000000),ExtendedWindowStyle=0x08000080,
                PositionX=-20000,PositionY=-20000,Width=400,Height=280});
            source.CompositionTarget.BackgroundColor=Colors.SteelBlue;
            var sourceVisual=new DrawingVisual();
            using(var dc=sourceVisual.RenderOpen()) {
                dc.DrawRectangle(Brushes.SteelBlue,null,new Rect(0,0,400,280));
                dc.DrawRectangle(Brushes.LightSteelBlue,null,new Rect(30,30,340,220));
            }
            source.RootVisual=sourceVisual;
            foreach(var card in cards) {
                var preview=new DwmPreviewSurface(new WindowInteropHelper(this).Handle,new PreviewPointerInput {
                    Down=p=>Begin(PointFromScreen(p),true),Move=(p,left)=>Moving(PointFromScreen(p),left),
                    Up=p=>End(PointFromScreen(p)),Wheel=Wheel,Cancel=Cancel
                });preview.Bind(source.Handle);previews.Add(preview);
            }
            routes=new SidebarInputRoutes(viewport,Down,Move,Up);
            CompositionTarget.Rendering+=Render;
            Report();
        };
        viewport.PreviewMouseWheel+=(_,e)=> {
            Wheel(e.Delta);
            e.Handled=true;
        };
        viewport.ScrollChanged+=(_,e)=> {if(e.VerticalChange!=0){scrolls++;gesture.Cancel();Report();}};
        var timer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(300)};
        timer.Tick+=(_,_)=> {if(File.Exists(output+".stop"))Close();};timer.Start();
        Closed+=(_,_)=> {timer.Stop();CompositionTarget.Rendering-=Render;routes?.Dispose();foreach(var p in previews)p.Dispose();source?.Dispose();File.WriteAllText(output+".closed","1");};
    }
    Rect Pixels(FrameworkElement element) => new Rect(element.PointToScreen(new Point()),element.PointToScreen(new Point(element.ActualWidth,element.ActualHeight)));
    Border? At(Point physical) => cards.FirstOrDefault(c=>Pixels(c).Contains(physical));
    void Down(object sender,MouseButtonEventArgs e) {
        if(Begin(e.GetPosition(this),false))e.Handled=true;
    }
    void Move(object sender,MouseEventArgs e) {
        Moving(e.GetPosition(this),e.LeftButton==MouseButtonState.Pressed);
    }
    void Up(object sender,MouseButtonEventArgs e) {
        End(e.GetPosition(this));e.Handled=true;
    }
    bool Begin(Point local,bool native) {
        presses++;start=local;pressed=At(PointToScreen(start));dragging=false;
        if(pressed!=null){gesture.Begin(start,viewport.VerticalOffset);if(!native)Mouse.Capture(viewport,CaptureMode.SubTree);}
        Report();return pressed!=null;
    }
    void Moving(Point local,bool left) {
        if(pressed==null || !left)return;
        gesture.Move(local);
        if(gesture.IsOutwardDrag(local))dragging=true;
    }
    void End(Point local) {
        releases++;
        if(pressed!=null) {
            if(dragging)drags++;
            else if(gesture.End(local,viewport.VerticalOffset) && ReferenceEquals(pressed,At(PointToScreen(local))))clicks++;
            Cancel();
        }
        Report();
    }
    void Cancel() {pressed=null;dragging=false;gesture.Cancel();if(Mouse.Captured==viewport)Mouse.Capture(null);foreach(var p in previews)p.ReleaseInputCapture();}
    void Wheel(int delta) {Cancel();viewport.ScrollToVerticalOffset(ThumbnailViewport.ScrollOffset(viewport.VerticalOffset,delta,viewport.ExtentHeight,viewport.ViewportHeight));}
    void Render(object? sender,EventArgs e) {
        for(int i=0;i<previews.Count;i++)previews[i].Update(Pixels(cards[i]),Pixels(viewport));
    }
    void Report() {
        status.Text=$"Presses={presses} Releases={releases} Clicks={clicks}\nDrags={drags} Scrolls={scrolls} Offset={viewport.VerticalOffset:F0}";
        File.WriteAllText(output,status.Text);
    }
}
