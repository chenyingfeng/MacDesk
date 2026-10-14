using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

internal sealed partial class MacBottomDock {
    void CloseWindowChooser(){var window=windowChooser;windowChooser=null;chooserKey="";if(window!=null)window.Close();}
    void ShowWindowChooser(string key,MacDockApp[] targets) {
        if(windowChooser!=null&&chooserKey==key){CloseWindowChooser();return;}
        CloseWindowChooser();ResetHover();
        var identities=new HashSet<string>(targets.Select(a=>a.Exe),StringComparer.OrdinalIgnoreCase);
        var chooser=new DockWindowChooser(delegate {RefreshClickSnapshot();return apps.Where(a=>identities.Contains(a.Exe)).ToArray();},Request,delegate {
            DockNative.PixelRect rect;
            return DockNative.GetWindowRect(new WindowInteropHelper(this).Handle,out rect)?new Rect(rect.Left,rect.Top,Math.Max(0,rect.Right-rect.Left),Math.Max(0,rect.Bottom-rect.Top)):Rect.Empty;
        });
        windowChooser=chooser;chooserKey=key;menuOpen=true;chooser.Owner=this;
        chooser.Closed+=delegate {if(Object.ReferenceEquals(windowChooser,chooser)){windowChooser=null;chooserKey="";}menuOpen=false;};
        try{chooser.Show();}catch{CloseWindowChooser();throw;}
    }
}

// A short-lived, nonactivating window. Only its visible page owns DWM relations.
// Sources are never restored, moved, captured to disk, or otherwise changed here.
internal sealed class DockWindowChooser : Window {
    readonly Func<MacDockApp[]> snapshot;
    readonly Action<MacDockApp> select;
    readonly Func<Rect> dockBounds;
    readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
    readonly List<Card> cards=new List<Card>();
    readonly StackPanel root=new StackPanel {Margin=new Thickness(14)};
    readonly UniformGrid grid=new UniformGrid();
    readonly TextBlock heading=new TextBlock {FontSize=13,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock paging=new TextBlock {Foreground=Brushes.LightGray,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0)};
    readonly Button previous,next;
    MacDockApp[] current=new MacDockApp[0];
    int page,columns=3,capacity=6;
    long nextSnapshot;
    DateTime outsideSince=DateTime.MinValue;
    bool disposed,chosen;
    string signature="";
    HwndSource source;
    internal static int ActiveRelations;
    sealed class Card {internal MacDockApp Target;internal Button Button;internal Border Preview;internal TextBlock Title,Hint;internal Grid Fallback;internal IntPtr Thumbnail;}
    internal DockWindowChooser(Func<MacDockApp[]> read,Action<MacDockApp> pick,Func<Rect> anchor) {
        snapshot=read;select=pick;dockBounds=anchor;
        Title="MacDesk 窗口预览";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        ShowInTaskbar=false;ShowActivated=false;Topmost=true;Background=new SolidColorBrush(Color.FromRgb(38,34,43));
        FontFamily=new FontFamily("Microsoft YaHei UI");UseLayoutRounding=true;SnapsToDevicePixels=true;
        var header=new DockPanel {Margin=new Thickness(3,0,3,10)};
        var controls=new StackPanel {Orientation=Orientation.Horizontal};
        previous=SmallButton("‹",delegate{ChangePage(-1);});next=SmallButton("›",delegate{ChangePage(1);});
        previous.ToolTip="上一页";next.ToolTip="下一页 · 也可滚动鼠标滚轮";
        controls.Children.Add(previous);controls.Children.Add(paging);controls.Children.Add(next);
        controls.Children.Add(SmallButton("×",Close));DockPanel.SetDock(controls,Dock.Right);header.Children.Add(controls);header.Children.Add(heading);
        root.Children.Add(header);root.Children.Add(grid);
        Content=new Border {Child=root,BorderBrush=new SolidColorBrush(Color.FromRgb(98,86,110)),BorderThickness=new Thickness(1)};
        SourceInitialized+=delegate {source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);DockNative.SetNoActivate(source.Handle);
            source.AddHook((IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled)=>{if(m==0x21){handled=true;return new IntPtr(3);}return IntPtr.Zero;});
        };
        Loaded+=delegate{Refresh(true);timer.Start();};
        timer.Tick+=delegate{Refresh(false);};
        Closed+=delegate{disposed=true;timer.Stop();ReleaseCards();};
        PreviewMouseWheel+=delegate(object sender,System.Windows.Input.MouseWheelEventArgs e){if(e.Delta!=0)ChangePage(e.Delta<0?1:-1);e.Handled=true;};
        PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Escape){Close();e.Handled=true;}};
    }
    static Button SmallButton(string text,Action action) {
        var b=new Button {Content=text,Width=28,Height=26,Margin=new Thickness(2,0,2,0),Focusable=false,Cursor=System.Windows.Input.Cursors.Hand,
            Background=new SolidColorBrush(Color.FromRgb(65,57,74)),Foreground=Brushes.White,BorderThickness=new Thickness(0)};
        var template=new ControlTemplate(typeof(Button));var border=new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
        var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);template.VisualTree=border;
        var disabled=new Trigger {Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.35));template.Triggers.Add(disabled);b.Template=template;
        b.Click+=delegate{action();};return b;
    }
    internal static MacDockApp Resolve(MacDockApp target,IEnumerable<MacDockApp> latest) {
        return latest.FirstOrDefault(a=>a.Handle==target.Handle&&a.Pid==target.Pid&&String.Equals(a.Exe,target.Exe,StringComparison.OrdinalIgnoreCase));
    }
    internal static Rect Fit(Rect box,double width,double height) {
        if(width<=0||height<=0||box.IsEmpty||box.Width<=0||box.Height<=0)return Rect.Empty;
        double scale=Math.Min(box.Width/width,box.Height/height),w=width*scale,h=height*scale;
        return new Rect(box.X+(box.Width-w)/2,box.Y+(box.Height-h)/2,w,h);
    }
    internal static int LastPage(int count,int size){return Math.Max(0,(count-1)/Math.Max(1,size));}
    internal static Rect Place(Rect area,Rect anchor,double width,double height,double gap) {
        double x=Math.Max(area.Left,Math.Min(area.Right-width,anchor.Left+(anchor.Width-width)/2));
        double y=Math.Max(area.Top,Math.Min(area.Bottom-height,anchor.Bottom-height-gap));
        return new Rect(x,y,Math.Min(width,area.Width),Math.Min(height,area.Height));
    }
    void Refresh(bool force) {
        if(disposed)return;
        if(force||DateTime.UtcNow.Ticks>=nextSnapshot) {
            nextSnapshot=DateTime.UtcNow.AddSeconds(1).Ticks;
            try{current=snapshot().Where(a=>NativeDockIcons.LiveWindow(a.Handle,a.Pid)).GroupBy(a=>a.Handle).Select(g=>g.First()).OrderBy(a=>a.Handle).ToArray();}
            catch{current=new MacDockApp[0];}
            if(current.Length==0){Close();return;}
            string updated=String.Join("|",current.Select(a=>a.Handle+":"+a.Pid+":"+a.Exe));
            if(force||signature!=updated){signature=updated;BuildPage();}
        }
        if(disposed)return;
        UpdateThumbnails();
        DockNative.PixelPoint point;
        if(DockNative.GetCursorPos(out point)) {
            var cursor=new Point(point.X,point.Y);var own=WindowPixels();var dock=dockBounds();
            // Keep the short travel corridor between the Dock and cards usable.
            own.Union(dock);own.Inflate(12,12);
            if(own.Contains(cursor))outsideSince=DateTime.MinValue;
            else if(outsideSince==DateTime.MinValue)outsideSince=DateTime.UtcNow;
            else if((DateTime.UtcNow-outsideSince).TotalMilliseconds>650){Close();return;}
        }
        if((GetAsyncKeyState(0x1b)&0x8000)!=0)Close();
    }
    Rect WindowPixels(){DockNative.PixelRect r;return DockNative.GetWindowRect(new WindowInteropHelper(this).Handle,out r)?new Rect(r.Left,r.Top,Math.Max(0,r.Right-r.Left),Math.Max(0,r.Bottom-r.Top)):Rect.Empty;}
    void ChangePage(int direction){int wanted=Math.Max(0,Math.Min(LastPage(current.Length,capacity),page+direction));if(page==wanted)return;page=wanted;BuildPage();}
    void BuildPage() {
        ReleaseCards();grid.Children.Clear();
        var anchor=dockBounds();var screen=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(anchor.X+anchor.Width/2),(int)anchor.Bottom));
        double scale=DockNative.Scale(screen);double available=screen.WorkingArea.Width/scale-40;
        columns=Math.Max(1,Math.Min(3,(int)(available/218)));capacity=columns*(screen.WorkingArea.Height/scale>=510?2:1);
        page=Math.Min(page,LastPage(current.Length,capacity));var shown=current.Skip(page*capacity).Take(capacity).ToArray();
        // Keep the panel anchored while paging, including a partly filled last
        // page, so scrolling cannot move the next target away from the pointer.
        grid.Columns=Math.Min(columns,current.Length);grid.Rows=(Math.Min(current.Length,capacity)+columns-1)/columns;
        heading.Text="选择窗口 · "+current.Length;paging.Text=(page+1)+" / "+(LastPage(current.Length,capacity)+1);
        previous.IsEnabled=page>0;next.IsEnabled=page<LastPage(current.Length,capacity);
        foreach(var app in shown)AddCard(app);
        Width=grid.Columns*218+30;Height=grid.Rows*166+70;
        var rect=Place(DockNative.Bounds(screen.WorkingArea),anchor,Width*scale,Height*scale,110*scale);
        DockNative.SetWindowPos(new WindowInteropHelper(this).EnsureHandle(),IntPtr.Zero,(int)rect.X,(int)rect.Y,(int)rect.Width,(int)rect.Height,0x0014);
        UpdateLayout();UpdateThumbnails();
    }
    void AddCard(MacDockApp app) {
        var card=new Card {Target=app};
        var stack=new StackPanel {Margin=new Thickness(7)};
        var previewGrid=new Grid();
        card.Fallback=new Grid {Background=new SolidColorBrush(Color.FromRgb(51,47,58))};
        card.Hint=new TextBlock {Text="窗口预览",FontSize=12,Foreground=Brushes.LightGray,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        card.Fallback.Children.Add(card.Hint);previewGrid.Children.Add(card.Fallback);
        card.Preview=new Border {Height=111,Child=previewGrid,Background=new SolidColorBrush(Color.FromRgb(30,28,35))};stack.Children.Add(card.Preview);
        card.Title=new TextBlock {Margin=new Thickness(0,9,0,1),Foreground=Brushes.White,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};stack.Children.Add(card.Title);
        card.Button=new Button {Content=stack,Width=210,Height=158,Margin=new Thickness(4),Padding=new Thickness(0),Focusable=false,
            HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch,Cursor=System.Windows.Input.Cursors.Hand,
            Background=new SolidColorBrush(Color.FromRgb(48,43,55)),BorderBrush=new SolidColorBrush(Color.FromRgb(85,73,98)),BorderThickness=new Thickness(1)};
        // Explicit border template: an actual Button receives pointer input even
        // when DWM paints the thumbnail over its content rectangle.
        var template=new ControlTemplate(typeof(Button));var border=new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(10));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));border.AppendChild(presenter);template.VisualTree=border;card.Button.Template=template;
        card.Button.MouseEnter+=delegate{card.Button.BorderBrush=new SolidColorBrush(Color.FromRgb(200,164,228));};
        card.Button.MouseLeave+=delegate{card.Button.BorderBrush=new SolidColorBrush(Color.FromRgb(85,73,98));};
        card.Button.Click+=delegate{Pick(card.Target);};cards.Add(card);grid.Children.Add(card.Button);
    }
    void Pick(MacDockApp target) {
        if(disposed||chosen)return;MacDockApp live;
        try{live=Resolve(target,snapshot());}catch{live=null;}
        if(live==null||!NativeDockIcons.LiveWindow(live.Handle,live.Pid)){Refresh(true);return;}
        chosen=true;Close();select(live);
    }
    void UpdateThumbnails() {
        if(source==null||disposed)return;var transform=source.CompositionTarget.TransformToDevice;
        foreach(var card in cards) {
            var latest=Resolve(card.Target,current);if(latest!=null)card.Target=latest;
            string title=NativeDockIcons.WindowTitle(card.Target.Handle,card.Target.Pid);
            card.Title.Text=(card.Target.Active?"● ":"")+(String.IsNullOrWhiteSpace(title)?"未命名窗口":title);card.Button.ToolTip=title;
            bool live=NativeDockIcons.LiveWindow(card.Target.Handle,card.Target.Pid);card.Button.IsEnabled=live;
            bool minimized=IsIconic(new IntPtr(card.Target.Handle));
            card.Hint.Text=!live?"窗口已关闭":minimized?"已最小化 · 点击恢复":"预览不可用 · 点击打开";
            if(!live||minimized){Release(card);card.Fallback.Visibility=Visibility.Visible;continue;}
            if(card.Thumbnail==IntPtr.Zero) {
                if(DwmRegisterThumbnail(source.Handle,new IntPtr(card.Target.Handle),out card.Thumbnail)!=0){card.Thumbnail=IntPtr.Zero;continue;}
                ActiveRelations++;
            }
            NativeSize size;var point=card.Preview.TranslatePoint(new Point(0,0),this);
            var pixels=new MatrixTransform(transform).TransformBounds(new Rect(point,new Size(card.Preview.ActualWidth,card.Preview.ActualHeight)));
            if(DwmQueryThumbnailSourceSize(card.Thumbnail,out size)!=0){Release(card);card.Fallback.Visibility=Visibility.Visible;continue;}
            var fit=Fit(pixels,size.Width,size.Height);if(fit.IsEmpty)continue;
            var props=new ThumbnailProperties {Flags=0x1d,Destination=new NativeRect(fit),Opacity=255,Visible=true,ClientOnly=false};
            if(DwmUpdateThumbnailProperties(card.Thumbnail,ref props)!=0){Release(card);card.Fallback.Visibility=Visibility.Visible;continue;}
            card.Fallback.Visibility=Visibility.Hidden;
        }
    }
    static void Release(Card card){if(card.Thumbnail!=IntPtr.Zero){DwmUnregisterThumbnail(card.Thumbnail);card.Thumbnail=IntPtr.Zero;ActiveRelations--;}}
    void ReleaseCards(){foreach(var card in cards)Release(card);cards.Clear();}
    [StructLayout(LayoutKind.Sequential)]struct NativeRect {internal int Left,Top,Right,Bottom;internal NativeRect(Rect r){Left=(int)Math.Ceiling(r.Left);Top=(int)Math.Ceiling(r.Top);Right=(int)Math.Floor(r.Right);Bottom=(int)Math.Floor(r.Bottom);}}
    [StructLayout(LayoutKind.Sequential)]struct NativeSize {internal int Width,Height;}
    [StructLayout(LayoutKind.Sequential)]struct ThumbnailProperties {internal uint Flags;internal NativeRect Destination,Source;internal byte Opacity;[MarshalAs(UnmanagedType.Bool)]internal bool Visible;[MarshalAs(UnmanagedType.Bool)]internal bool ClientOnly;}
    [DllImport("dwmapi.dll")]static extern int DwmRegisterThumbnail(IntPtr destination,IntPtr source,out IntPtr thumbnail);
    [DllImport("dwmapi.dll")]static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")]static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail,out NativeSize size);
    [DllImport("dwmapi.dll")]static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail,ref ThumbnailProperties props);
    [DllImport("user32.dll")]static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
    internal static void Check(string report,bool interactive=false) {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};var windows=new List<Window>();DockWindowChooser chooser=null;
        try {
            var bounds=new Rect(-1920,0,1920,1080);var fit=Fit(new Rect(10,20,200,100),400,400);
            if(fit!=new Rect(60,20,100,100)||!Fit(bounds,0,20).IsEmpty)throw new Exception("Aspect fitting failed");
            var placement=Place(bounds,new Rect(-1000,950,600,130),700,400,110);
            if(!bounds.Contains(placement)||LastPage(300,6)!=49||LastPage(0,6)!=0)throw new Exception("Placement or page bound failed");
            int pid=Process.GetCurrentProcess().Id;var targets=new List<MacDockApp>();
            for(int i=0;i<8;i++) {
                var w=new Window {Title="Owned preview fixture "+i,Width=220,Height=140,ShowActivated=false,ShowInTaskbar=false,Content=new TextBlock {Text="Window "+i,FontSize=28},Left=40+i*12,Top=40+i*12};
                w.Show();windows.Add(w);targets.Add(new MacDockApp {Handle=new WindowInteropHelper(w).Handle.ToInt64(),Pid=pid,Exe="owned-fixture.exe"});
            }
            var selected=new List<MacDockApp>();Func<Rect> anchor=()=>new Rect(200,650,500,120);
            chooser=new DockWindowChooser(()=>targets.ToArray(),a=>selected.Add(a),anchor);chooser.ShowInTaskbar=interactive;chooser.Show();chooser.timer.Stop();chooser.UpdateLayout();chooser.UpdateThumbnails();
            if(interactive) {
                var frame=new DispatcherFrame();var stop=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};var elapsed=Stopwatch.StartNew();
                stop.Tick+=delegate{if(chooser.disposed||elapsed.Elapsed.TotalSeconds>300||File.Exists(report+".stop")){stop.Stop();frame.Continue=false;}};
                stop.Start();Dispatcher.PushFrame(frame);
                File.WriteAllText(report,"Selected="+selected.Count+"\nTarget="+(selected.Count>0?selected[0].Handle.ToString():"none")+"\nRelations="+ActiveRelations);
                return;
            }
            if(ActiveRelations!=chooser.cards.Count||ActiveRelations==0||chooser.cards.Count>6)throw new Exception("Owned DWM relation/page count failed");
            var originalBounds=chooser.WindowPixels();chooser.ChangePage(1);
            if(ActiveRelations!=chooser.cards.Count||chooser.page!=1||chooser.WindowPixels()!=originalBounds)throw new Exception("Page transition leaked DWM relations or moved the panel");
            var target=chooser.cards[0].Target;
            if(Resolve(target,new[]{new MacDockApp {Handle=target.Handle,Pid=pid+1,Exe=target.Exe}})!=null
                ||Resolve(target,new[]{new MacDockApp {Handle=target.Handle,Pid=pid,Exe="another.exe"}})!=null)throw new Exception("Stale process identity accepted");
            targets.Remove(target);chooser.Pick(target);if(selected.Count!=0)throw new Exception("Removed target activated");
            var desired=chooser.cards[0].Target;
            // WPF must retain a hit-testable element inside the DWM rectangle.
            var hit=chooser.cards[0].Preview.TranslatePoint(new Point(60,40),chooser);
            var visual=chooser.InputHitTest(hit) as DependencyObject;
            while(visual!=null&&visual!=chooser.cards[0].Button)visual=VisualTreeHelper.GetParent(visual);
            if(visual==null)throw new Exception("DWM card lost WPF input target");
            chooser.cards[0].Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(selected.Count!=1||selected[0].Handle!=desired.Handle||ActiveRelations!=0)throw new Exception("Exact choice or close cleanup failed");
            chooser.Pick(desired);if(selected.Count!=1)throw new Exception("Closed chooser invoked twice");
            chooser=new DockWindowChooser(()=>targets.ToArray(),a=>selected.Add(a),anchor);chooser.Show();chooser.timer.Stop();targets.Clear();chooser.Refresh(true);
            if(!chooser.disposed||ActiveRelations!=0)throw new Exception("Empty snapshot left preview visible");
            var minimized=new MacDockApp {Handle=new WindowInteropHelper(windows[0]).Handle.ToInt64(),Pid=pid,Exe="owned-fixture.exe"};
            targets.Add(minimized);chooser=new DockWindowChooser(()=>targets.ToArray(),a=>selected.Add(a),anchor);chooser.Show();chooser.timer.Stop();
            windows[0].WindowState=WindowState.Minimized;chooser.UpdateThumbnails();
            if(ActiveRelations!=0||chooser.cards[0].Fallback.Visibility!=Visibility.Visible||!chooser.cards[0].Button.IsEnabled)throw new Exception("Minimized window did not keep selectable fallback");
            windows[0].Close();chooser.Refresh(true);if(!chooser.disposed||ActiveRelations!=0)throw new Exception("Closed source left live card");
            File.WriteAllText(report,"PASS: 8 owned windows; bounded DWM preview pages and cleanup; WPF hit testing inside DWM bounds and button selection of exact handle/PID/executable; removed, reused and duplicate choices refused; empty snapshot closes; aspect ratio and negative-monitor placement. No third-party app activated. Actual pointer injection is a separate interactive fixture.");
        }finally{if(chooser!=null&&!chooser.disposed)chooser.Close();foreach(var w in windows)w.Close();app.Shutdown();}
    }
}
