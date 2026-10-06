using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Forms=System.Windows.Forms;

[assembly: System.Reflection.AssemblyFileVersion("4.7.7.0")]

enum DockEdge { Left, Right, Top, Bottom }
class EdgeGeometry {
    internal static bool Hit(DockEdge edge,double offset,Rect bounds,Point pointer,double scale,bool wholeEdge=false) {
        double along=(edge==DockEdge.Left || edge==DockEdge.Right)?pointer.Y:pointer.X;
        double start=(edge==DockEdge.Left || edge==DockEdge.Right)?bounds.Top:bounds.Left;
        double length=(edge==DockEdge.Left || edge==DockEdge.Right)?bounds.Height:bounds.Width;
        bool near=edge==DockEdge.Left?pointer.X>=bounds.Left && pointer.X<bounds.Left+4*scale:
            edge==DockEdge.Right?pointer.X<bounds.Right && pointer.X>=bounds.Right-4*scale:
            edge==DockEdge.Top?pointer.Y>=bounds.Top && pointer.Y<bounds.Top+4*scale:
            pointer.Y<bounds.Bottom && pointer.Y>=bounds.Bottom-4*scale;
        return bounds.Contains(pointer) && near && (wholeEdge || Math.Abs(along-(start+length*offset))<=70*scale);
    }
    internal static DockEdge Nearest(Rect bounds,Point p) {
        double[] distances={Math.Abs(p.X-bounds.Left),Math.Abs(bounds.Right-p.X),Math.Abs(p.Y-bounds.Top),Math.Abs(bounds.Bottom-p.Y)};
        int chosen=0;for(int i=1;i<4;i++)if(distances[i]<distances[chosen])chosen=i;
        return (DockEdge)chosen;
    }
    internal static Rect Panel(DockEdge edge,double offset,Rect area,double width,double height,double scale) {
        double gap=8*scale;
        double x=area.Left+area.Width*offset-width/2,y=area.Top+area.Height*offset-height/2;
        if(edge==DockEdge.Left)x=area.Left+gap;
        if(edge==DockEdge.Right)x=area.Right-width-gap;
        if(edge==DockEdge.Top)y=area.Top+gap;
        if(edge==DockEdge.Bottom)y=area.Bottom-height-gap;
        return new Rect(Math.Max(area.Left,Math.Min(x,area.Right-width)),Math.Max(area.Top,Math.Min(y,area.Bottom-height)),width,height);
    }
}
class DockNative {
    [StructLayout(LayoutKind.Sequential)] internal struct PixelPoint {public int X,Y;}
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool GetCursorPos(out PixelPoint point);
    [StructLayout(LayoutKind.Sequential)] internal struct PixelRect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window,out PixelRect rect);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(PixelPoint point,uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetLong64(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetLong64(IntPtr h,int index,IntPtr value);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetLong32(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")] static extern int SetLong32(IntPtr h,int index,int value);
    internal static void SetNoActivate(IntPtr h) {
        if(IntPtr.Size==8)SetLong64(h,-20,new IntPtr(GetLong64(h,-20).ToInt64()|0x08000000L));
        else SetLong32(h,-20,GetLong32(h,-20)|0x08000000);
    }
    internal static double Scale(Forms.Screen screen) {
        try {var p=new PixelPoint {X=screen.Bounds.Left+screen.Bounds.Width/2,Y=screen.Bounds.Top+screen.Bounds.Height/2};uint x,y;
            if(GetDpiForMonitor(MonitorFromPoint(p,2),0,out x,out y)==0)return x/96.0;}catch{}
        return 1;
    }
    internal static Rect Bounds(System.Drawing.Rectangle r){return new Rect(r.X,r.Y,r.Width,r.Height);}
}

class DockWindow : Window {
    readonly string ownRoot=System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
    readonly DockLinkLauncher webLauncher = new DockLinkLauncher();
    readonly DockDocumentLauncher documentLauncher=new DockDocumentLauncher();
    readonly LinkClickPolicy linkClicks = new LinkClickPolicy();
    HwndSource inputSource;
    string stageRoot {get{return System.IO.Path.GetFullPath(System.IO.Path.Combine(ownRoot,"..","CampusStage"));}}
    readonly DispatcherTimer leaveTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(450)};
    readonly DispatcherTimer updateTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(5)};
    readonly DispatcherTimer edgeTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
    readonly DispatcherTimer linkHideTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
    readonly DispatcherTimer healthTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
    internal readonly string RunToken=Guid.NewGuid().ToString("N");
    string exitReason="";
    Forms.NotifyIcon tray;
    MacBottomDock macDock;
    System.Drawing.Icon trayIcon;
    Border frame;
    StackPanel body;
    TextBlock clock,date;
    TextBlock dragHint;
    bool expanded=false,light=false,menuOpen=false,dragging=false,started=false;
    DockEdge edge=DockEdge.Top;
    double offset=.42;
    string monitorDevice="",lastShow="";
    DateTime hoverSince=DateTime.MinValue,suppressUntil=DateTime.MinValue;
    long edgeTicks=0,edgeHits=0,expands=0;
    const double PanelWidth=500,PanelHeight=640;
    string lastExit="",lastRecovery="";
    string themePath {get{return System.IO.Path.Combine(ownRoot,"theme.txt");}}
    string placementPath {get{return System.IO.Path.Combine(ownRoot,"placement.ini");}}
    Brush Color(string code){return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(code));}
    Brush ForegroundColor {get{return Color(light?"#2F2039":"#FAF4FF");}}
    Brush Muted {get{return Color(light?"#84758C":"#B2A1BE");}}
    Brush Accent {get{return Color(light?"#660874":"#DEB8EA");}}
    public DockWindow(bool showPanel=false,bool? previewLight=null) {
        Title="MacDesk 快捷中心";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowActivated=false;
        AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;Topmost=true;
        UseLayoutRounding=true;SnapsToDevicePixels=true;
        SourceInitialized+=delegate {
            inputSource=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            DockNative.SetNoActivate(inputSource.Handle);
            inputSource.AddHook(NonActivatingMessage);
        };
        FontFamily=new FontFamily("Microsoft YaHei UI");
        TextOptions.SetTextFormattingMode(this,TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this,TextRenderingMode.Grayscale);
        if(File.Exists(themePath))light=File.ReadAllText(themePath).Trim()=="Light";
        if(previewLight.HasValue)light=previewLight.Value;
        LoadPlacement();Width=PanelWidth;Height=PanelHeight;
        frame=new Border {Width=PanelWidth,Height=PanelHeight};Content=new Viewbox {Stretch=Stretch.Uniform,Child=frame};
        MouseEnter+=delegate {leaveTimer.Stop();};
        MouseLeave+=delegate {if(expanded && !menuOpen && !dragging){leaveTimer.Stop();leaveTimer.Start();}};
        leaveTimer.Tick+=delegate {leaveTimer.Stop();if(expanded && !IsMouseOver && !PointerNearPanel() && !menuOpen && !dragging)Collapse();};
        var context=new ContextMenu();
        var dark=new MenuItem {Header="深色外观"};dark.Click+=delegate {SetTheme(false);};
        var bright=new MenuItem {Header="浅色外观"};bright.Click+=delegate {SetTheme(true);};
        var close=new MenuItem {Header="退出挂件"};close.Click+=delegate {Exit("user-menu");};
        var edges=new MenuItem {Header="停靠位置"};
        foreach(DockEdge choice in Enum.GetValues(typeof(DockEdge))) {DockEdge chosen=choice;var item=new MenuItem {Header=EdgeName(choice)};item.Click+=delegate {edge=chosen;SavePlacement();Position();UpdateDragHint();};edges.Items.Add(item);}
        context.Items.Add(dark);context.Items.Add(bright);context.Items.Add(edges);context.Items.Add(new Separator());
        var elevated=new MenuItem {Header="以管理员身份启动台前调度…"};elevated.Click+=delegate {RunStage(false,true);};context.Items.Add(elevated);context.Items.Add(close);
        context.Opened+=delegate {menuOpen=true;leaveTimer.Stop();};
        context.Closed+=delegate {menuOpen=false;if(!IsMouseOver)leaveTimer.Start();};
        ContextMenu=context;
        Loaded+=delegate {if(started)return;started=true;CreateTray();
            var marker=System.IO.Path.Combine(ownRoot,"show.marker");if(File.Exists(marker))lastShow=File.ReadAllText(marker);
            foreach(string command in new string[]{"exit","recover"}) {
                var file=System.IO.Path.Combine(ownRoot,command+".marker");
                try {if(File.Exists(file)){if(command=="exit")lastExit=File.ReadAllText(file);else lastRecovery=File.ReadAllText(file);}}catch{}
            }
            macDock=new MacBottomDock(stageRoot,ToggleTaskbar,Expand,false,ShowNotice);macDock.Show();
            if(showPanel)Expand();else Collapse();WriteDockStatus("ready",null);updateTimer.Start();edgeTimer.Start();healthTimer.Start();DockWatchdog.Start(ownRoot,Process.GetCurrentProcess().Id,RunToken);};
        updateTimer.Tick+=delegate {if(expanded)UpdatePanel();
            try {var exit=System.IO.Path.Combine(ownRoot,"exit.marker");if(File.Exists(exit) && File.ReadAllText(exit)!=lastExit){Exit("exit-request");return;}}
            catch{}
            var marker=System.IO.Path.Combine(ownRoot,"show.marker");try {if(File.Exists(marker)){string request=File.ReadAllText(marker);if(request!=lastShow){lastShow=request;Expand();}}}catch{};};
        edgeTimer.Tick+=delegate {CheckEdge();documentLauncher.Poll(stageRoot);};
        healthTimer.Tick+=delegate {CheckHealth();};
        linkHideTimer.Tick+=delegate {linkHideTimer.Stop();Collapse();};
        Closed+=delegate {healthTimer.Stop();if(macDock!=null)macDock.Close();WriteDockStatus("closed",null);webLauncher.Dispose();linkHideTimer.Stop();if(inputSource!=null)inputSource.RemoveHook(NonActivatingMessage);updateTimer.Stop();leaveTimer.Stop();edgeTimer.Stop();if(tray!=null){tray.Visible=false;tray.Dispose();}if(trayIcon!=null)trayIcon.Dispose();};
    }
    internal void Exit(string reason){exitReason=reason;DockWatchdog.MarkIntentionalExit(ownRoot,RunToken,reason);Close();}
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e){if(String.IsNullOrEmpty(exitReason))exitReason="native-close";WriteDockStatus("closing",null);base.OnClosing(e);}
    protected override void OnClosed(EventArgs e){try{base.OnClosed(e);}finally{var app=Application.Current;if(app!=null&&app.MainWindow==this)app.Shutdown();}}
    internal void ShowNotice(string detail) {
        if(Dispatcher.HasShutdownStarted)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(new Action(delegate{ShowNotice(detail);}));return;}
        if(tray!=null){tray.BalloonTipTitle="MacDesk";tray.BalloonTipText=detail;tray.ShowBalloonTip(4500);}
    }
    internal void RecoverWidgets() {
        leaveTimer.Stop();hoverSince=DateTime.MinValue;suppressUntil=DateTime.MinValue;
        if(ContextMenu==null||!ContextMenu.IsOpen)menuOpen=false;
        if(!edgeTimer.IsEnabled)edgeTimer.Start();if(!updateTimer.IsEnabled)updateTimer.Start();
        if(macDock!=null)macDock.RepairWidgets();
        try{DockRuntime.AtomicText(System.IO.Path.Combine(stageRoot,"sidebar-recovery.request"),Guid.NewGuid().ToString("N"));}catch(Exception error){DockRuntime.Record(ownRoot,"sidebar-rearm",error);}
        Expand();
    }
    void CheckHealth() {
        if(ContextMenu==null||!ContextMenu.IsOpen)menuOpen=false;
        if(!edgeTimer.IsEnabled)edgeTimer.Start();if(!updateTimer.IsEnabled)updateTimer.Start();
        if(macDock==null||macDock.WasClosed){macDock=new MacBottomDock(stageRoot,ToggleTaskbar,Expand,false,ShowNotice);macDock.Show();}
        macDock.RepairWidgets();
        try {
            var request=System.IO.Path.Combine(ownRoot,"recover.marker");
            if(File.Exists(request)){var token=File.ReadAllText(request);if(token!=lastRecovery){lastRecovery=token;RecoverWidgets();}}
            DockRuntime.AtomicText(System.IO.Path.Combine(ownRoot,"dock-heartbeat.ini"),"Version=4.7.7.0\nPid="+Process.GetCurrentProcess().Id+"\nRunToken="+RunToken+"\nState=ready\nExpanded="+expanded+"\nMenuOpen="+menuOpen+"\nExitReason="+exitReason+"\nUiUtc="+DateTime.UtcNow.ToString("o")+"\n");
        }catch(Exception error){DockRuntime.Record(ownRoot,"heartbeat",error);}
    }
    void SetTheme(bool value){light=value;File.WriteAllText(themePath,light?"Light":"Dark");if(expanded)BuildPanel();}
    static string EdgeName(DockEdge value){return value==DockEdge.Left?"左边缘":value==DockEdge.Right?"右边缘":value==DockEdge.Top?"上边缘":"下边缘";}
    Forms.Screen CurrentScreen() {foreach(var screen in Forms.Screen.AllScreens)if(screen.DeviceName==monitorDevice)return screen;return Forms.Screen.PrimaryScreen;}
    void LoadPlacement(){try {foreach(string line in File.ReadAllLines(placementPath)){int at=line.IndexOf('=');if(at<1)continue;string key=line.Substring(0,at),value=line.Substring(at+1);
        DockEdge parsed;double number;if(key=="Edge" && Enum.TryParse(value,out parsed) && Enum.IsDefined(typeof(DockEdge),parsed))edge=parsed;
        if(key=="Offset" && double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out number) && !double.IsNaN(number))offset=Math.Max(.06,Math.Min(.94,number));
        if(key=="Monitor")monitorDevice=value;}}catch{}}
    void SavePlacement(){monitorDevice=CurrentScreen().DeviceName;File.WriteAllText(placementPath,"Edge="+edge+"\nOffset="+offset.ToString("R",CultureInfo.InvariantCulture)+"\nMonitor="+monitorDevice+"\n",new UTF8Encoding(false));}
    void Position() {
        var screen=CurrentScreen();double scale=DockNative.Scale(screen);
        double fit=Math.Min(1,Math.Min((screen.WorkingArea.Width/scale-16)/PanelWidth,(screen.WorkingArea.Height/scale-16)/PanelHeight));
        Width=PanelWidth*Math.Max(.4,fit);Height=PanelHeight*Math.Max(.4,fit);
        var r=EdgeGeometry.Panel(edge,offset,DockNative.Bounds(screen.WorkingArea),Width*scale,Height*scale,scale);
        var hwnd=new WindowInteropHelper(this).EnsureHandle();
        DockNative.SetWindowPos(hwnd,IntPtr.Zero,(int)Math.Round(r.X),(int)Math.Round(r.Y),(int)Math.Round(r.Width),(int)Math.Round(r.Height),0x0014);
    }
    bool PointerNearPanel(){if(!expanded || !IsVisible)return false;DockNative.PixelPoint p;DockNative.PixelRect r;
        if(!DockNative.GetCursorPos(out p) || !DockNative.GetWindowRect(new WindowInteropHelper(this).Handle,out r))return false;
        var screen=CurrentScreen();if(EdgeGeometry.Hit(edge,offset,DockNative.Bounds(screen.Bounds),new Point(p.X,p.Y),DockNative.Scale(screen),true))return true;
        double gap=12*DockNative.Scale(CurrentScreen());return p.X>=r.Left-gap && p.X<=r.Right+gap && p.Y>=r.Top-gap && p.Y<=r.Bottom+gap;}
    void CheckEdge(){edgeTicks++;DockNative.PixelPoint diagnosticPoint;bool cursorOk=DockNative.GetCursorPos(out diagnosticPoint);var diagnosticScreen=CurrentScreen();
        if(expanded){hoverSince=DateTime.MinValue;if(!dragging && !menuOpen){if(IsMouseOver || PointerNearPanel())leaveTimer.Stop();else if(!leaveTimer.IsEnabled && DateTime.UtcNow>=suppressUntil)leaveTimer.Start();}return;}
        if(dragging || menuOpen || DateTime.UtcNow<suppressUntil){hoverSince=DateTime.MinValue;return;}
        DockNative.PixelPoint p;if(!DockNative.GetCursorPos(out p))return;var screen=CurrentScreen();
        if(!EdgeGeometry.Hit(edge,offset,DockNative.Bounds(screen.Bounds),new Point(p.X,p.Y),DockNative.Scale(screen),true)){hoverSince=DateTime.MinValue;return;}
        edgeHits++;
        if(hoverSince==DateTime.MinValue)hoverSince=DateTime.UtcNow;else if((DateTime.UtcNow-hoverSince).TotalMilliseconds>=220)Expand();}
    void SnapAfterDrag(){DockNative.PixelPoint p;if(!DockNative.GetCursorPos(out p))return;var screen=Forms.Screen.FromPoint(new System.Drawing.Point(p.X,p.Y));
        monitorDevice=screen.DeviceName;var bounds=DockNative.Bounds(screen.Bounds);edge=EdgeGeometry.Nearest(bounds,new Point(p.X,p.Y));
        offset=(edge==DockEdge.Left || edge==DockEdge.Right)?(p.Y-bounds.Top)/bounds.Height:(p.X-bounds.Left)/bounds.Width;
        offset=Math.Max(.06,Math.Min(.94,offset));SavePlacement();Position();UpdateDragHint();if(!IsMouseOver)leaveTimer.Start();}
    void UpdateDragHint(){if(dragHint!=null)dragHint.Text="拖动到任意边缘  ·  当前"+EdgeName(edge);}
    void CreateTray(){using(var bitmap=new System.Drawing.Bitmap(32,32)){using(var g=System.Drawing.Graphics.FromImage(bitmap)){g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);using(var brush=new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(102,8,116)))g.FillEllipse(brush,1,1,30,30);
        using(var font=new System.Drawing.Font("Segoe UI",18,System.Drawing.FontStyle.Bold,System.Drawing.GraphicsUnit.Pixel))g.DrawString("M",font,System.Drawing.Brushes.White,6,4);}
        IntPtr icon=bitmap.GetHicon();trayIcon=(System.Drawing.Icon)System.Drawing.Icon.FromHandle(icon).Clone();DockNative.DestroyIcon(icon);}
        tray=new Forms.NotifyIcon {Icon=trayIcon,Text="MacDesk · 双击展开快捷中心",Visible=true};var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("展开快捷中心",null,delegate {Expand();});menu.Items.Add("恢复到主屏幕上边缘",null,delegate {monitorDevice=Forms.Screen.PrimaryScreen.DeviceName;edge=DockEdge.Top;offset=.42;SavePlacement();Expand();});
        menu.Items.Add("恢复桌面入口",null,delegate {RecoverWidgets();});menu.Items.Add("退出 MacDesk",null,delegate {Exit("user-menu");});tray.ContextMenuStrip=menu;tray.DoubleClick+=delegate {Expand();};}
    void PaintFrame() {
        frame.Background=new LinearGradientBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(light?"#FFFDFC":"#2D0935"),(System.Windows.Media.Color)ColorConverter.ConvertFromString(light?"#F7F0F9":"#1C1023"),90);
        frame.BorderBrush=Color(light?"#D7BDDF":"#743B81");frame.BorderThickness=new Thickness(1);
        frame.CornerRadius=new CornerRadius(24);
    }
    void Expand(){leaveTimer.Stop();hoverSince=DateTime.MinValue;suppressUntil=DateTime.UtcNow.AddMilliseconds(500);if(WindowState!=WindowState.Normal)WindowState=WindowState.Normal;if(expanded){Show();Position();return;}expands++;expanded=true;Width=PanelWidth;Height=PanelHeight;BuildPanel();Show();Position();body.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(180)));}
    void Collapse(){leaveTimer.Stop();linkHideTimer.Stop();expanded=false;hoverSince=DateTime.MinValue;suppressUntil=DateTime.UtcNow.AddMilliseconds(650);Hide();}
    TextBlock Text(string value,double size,Brush color) {return new TextBlock {Text=value,FontSize=size,Foreground=color,TextTrimming=TextTrimming.CharacterEllipsis};}
    UIElement CenterGlyph(double width,double height) {
        return new Border {Width=width,Height=height,CornerRadius=new CornerRadius(width*.25),Background=Color("#7740A6"),
            Child=new TextBlock {Text="M",FontFamily=new FontFamily("Segoe UI Semibold"),FontSize=height*.63,Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};
    }
    UIElement DrawIcon(int kind,Brush brush) {
        string[] paths={
            "M4,3 L18,3 L18,21 L4,21 Z M7,3 L7,21 M10,7 L15,7 M10,11 L15,11",
            "M2,5 L22,5 L22,19 L2,19 Z M2,5 L12,13 L22,5",
            "M7,19 C0,19 0,9 7,9 C8,1 19,2 19,10 C25,10 25,19 19,19 Z",
            "M2,11 L12,2 L22,11 M5,9 L5,22 L10,22 L10,15 L14,15 L14,22 L19,22 L19,9",
            "M3,21 L5,15 L17,3 C20,0 24,4 21,7 L9,19 Z M5,15 L9,19 M15,5 L19,9",
            "M3,2 L18,2 L22,6 L22,22 L3,22 Z M18,2 L18,6 L22,6 M7,10 L18,10 M7,14 L18,14 M7,18 L14,18",
            "M2,4 L22,4 L22,18 L9,18 L4,23 L4,18 L2,18 Z M6,9 L18,9 M6,13 L15,13",
            "M2,5 L17,5 L17,19 L2,19 Z M17,10 L23,6 L23,18 L17,14 M6,8 L13,12 L6,16 Z"
        };
        var shape=new System.Windows.Shapes.Path {Data=Geometry.Parse(paths[kind]),Stroke=brush,StrokeThickness=1.55,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Stretch=Stretch.Uniform,Width=25,Height=25};
        return shape;
    }
    Button Card(string name,string hint,string url,int icon) {
        var button=new Button {Margin=new Thickness(0,0,10,8),Padding=new Thickness(12,10,10,10),BorderThickness=new Thickness(0),Background=Color(light?"#F0E5F4":"#3A1745"),HorizontalContentAlignment=HorizontalAlignment.Stretch,Height=56,Cursor=System.Windows.Input.Cursors.Hand,ToolTip="打开"+name};
        AutomationProperties.SetName(button,name);
        var template=new ControlTemplate(typeof(Button));
        var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(15));border.SetValue(Border.BorderThicknessProperty,new Thickness(1));border.SetValue(Border.BorderBrushProperty,Color(light?"#E6DAEC":"#493052"));border.SetBinding(Border.BackgroundProperty,new System.Windows.Data.Binding("Background"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)});
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(ContentPresenter.MarginProperty,button.Padding);border.AppendChild(presenter);template.VisualTree=border;
        var hover=new Trigger {Property=Button.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Button.BackgroundProperty,Color(light?"#E4CEEC":"#660874")));template.Triggers.Add(hover);button.Template=template;
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(40)});row.ColumnDefinitions.Add(new ColumnDefinition());
        string[] hues=light?new string[]{"#77448E","#8E4E91","#4E778E","#846946","#56836B","#73669D","#4D847E","#9A6277"}:new string[]{"#D7A5ED","#E7B1DE","#A5D0E8","#E5C998","#A5D4B8","#CBBCEE","#9AD6C9","#EBB4CB"};
        var iconBox=new Border {Width=36,Height=36,CornerRadius=new CornerRadius(11),Background=Color(light?"#FFFAFE":"#392B43"),Child=DrawIcon(icon,Color(hues[icon]))};row.Children.Add(iconBox);
        var lines=new StackPanel {Margin=new Thickness(4,0,0,0)};Grid.SetColumn(lines,1);
        lines.Children.Add(Text(name,14,ForegroundColor));lines.Children.Add(Text(hint,11,Muted));row.Children.Add(lines);

        button.Content=row;
        button.Click+=delegate {OpenWeb(name,url);};
        return button;
    }
    void BuildPanel() {
        PaintFrame();body=new StackPanel {Margin=new Thickness(23,17,13,17)};frame.Child=body;
        var drag=new Grid {Height=24,Margin=new Thickness(0,0,10,8),Background=Brushes.Transparent,Cursor=System.Windows.Input.Cursors.SizeAll};
        dragHint=Text("",10.5,Muted);dragHint.VerticalAlignment=VerticalAlignment.Center;drag.Children.Add(dragHint);UpdateDragHint();
        var dots=Text("⋮⋮",15,Accent);dots.HorizontalAlignment=HorizontalAlignment.Right;drag.Children.Add(dots);
        drag.MouseLeftButtonDown+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs e){dragging=true;leaveTimer.Stop();try {DragMove();}catch{}finally{dragging=false;SnapAfterDrag();}e.Handled=true;};body.Children.Add(drag);
        var header=new Grid {Height=70,Margin=new Thickness(0,0,10,10)};header.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(70)});header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(115)});
        header.Children.Add(CenterGlyph(59,59));
        var title=new StackPanel {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)};var name=Text("MacDesk",24,ForegroundColor);name.FontWeight=FontWeights.SemiBold;title.Children.Add(name);title.Children.Add(Text("macOS 风格桌面",11,Muted));Grid.SetColumn(title,1);header.Children.Add(title);
        var times=new StackPanel {HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};clock=Text("",26,ForegroundColor);clock.FontFamily=new FontFamily("Segoe UI Semibold");clock.TextAlignment=TextAlignment.Right;date=Text("",10.5,Muted);date.TextAlignment=TextAlignment.Right;times.Children.Add(clock);times.Children.Add(date);Grid.SetColumn(times,2);header.Children.Add(times);body.Children.Add(header);
        body.Children.Add(new TextBlock {Text="把常用软件、文件夹和网页放在一起。\n在程序坞或下方点“添加入口”，用你自己的选择定制。",Foreground=Muted,FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(2,0,10,14)});
        var webPins=new DockPinStore(ownRoot).Items.Where(p=>p.Kind=="web").Take(8).ToArray();
        var linkArea=new Border {Height=263,Margin=new Thickness(0,0,0,5)};
        if(webPins.Length==0) {
            linkArea.Child=new TextBlock {Text="还没有固定网页\n\n点击“添加入口” → “添加网页…”。\n网页也会显示在底部程序坞中。",FontSize=15,Foreground=Muted,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        }else {
            var cards=new UniformGrid {Columns=2,Rows=4,VerticalAlignment=VerticalAlignment.Top};
            for(int i=0;i<webPins.Length;i++)cards.Children.Add(Card(webPins[i].Name,"打开网页",webPins[i].Target,i));linkArea.Child=cards;
        }body.Children.Add(linkArea);
        var actions=new Grid {Height=43,Margin=new Thickness(0,0,10,9)};actions.ColumnDefinitions.Add(new ColumnDefinition());actions.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(85)});
        var add=new Button {Content="+ 添加入口",Padding=new Thickness(13,7,13,7),Foreground=Accent,Background=Brushes.Transparent,BorderBrush=Accent,HorizontalAlignment=HorizontalAlignment.Left,Cursor=System.Windows.Input.Cursors.Hand};
        add.Click+=delegate {if(macDock==null)return;menuOpen=true;leaveTimer.Stop();try {macDock.ShowPinPicker();BuildPanel();}finally {menuOpen=false;if(!IsMouseOver)leaveTimer.Start();}};actions.Children.Add(add);
        var hide=new Button {Content="收起",Background=Brushes.Transparent,BorderThickness=new Thickness(0),Foreground=Muted,FontSize=12,Cursor=System.Windows.Input.Cursors.Hand};hide.Click+=delegate {Collapse();};Grid.SetColumn(hide,1);actions.Children.Add(hide);body.Children.Add(actions);
        body.Children.Add(new Border {Height=1,Background=Color(light?"#DFD0E6":"#50315A"),Margin=new Thickness(0,0,10,10)});
        var stage=new Grid {Height=34,Margin=new Thickness(0,0,10,8)};
        var stageTitle=Text("台前调度",12,Muted);stageTitle.VerticalAlignment=VerticalAlignment.Center;stage.Children.Add(stageTitle);
        var stageStart=new Button {Content="开启",Padding=new Thickness(15,3,15,3),Foreground=Accent,Background=Brushes.Transparent,BorderBrush=Accent,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,0,80,0),Cursor=System.Windows.Input.Cursors.Hand};
        stageStart.Click+=delegate {RunStage(false);};stage.Children.Add(stageStart);
        var stageStop=new Button {Content="恢复桌面",Padding=new Thickness(9,3,9,3),Foreground=Muted,Background=Brushes.Transparent,BorderThickness=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Right,Cursor=System.Windows.Input.Cursors.Hand};
        stageStop.Click+=delegate {RunStage(true);};stage.Children.Add(stageStop);body.Children.Add(stage);
        body.Children.Add(Text("多窗口同时显示 · 右侧缩略图 · 原生应用图标",11,Muted));
        var apps=new UniformGrid {Columns=2,Margin=new Thickness(0,7,10,0)};
        string[] labels={"任务栏 / 托盘","全部应用"};
        for(int i=0;i<labels.Length;i++) {
            int action=i;var entry=new Button {Content=labels[i],Padding=new Thickness(5,7,5,7),Margin=new Thickness(2),Foreground=Accent,Background=Brushes.Transparent,BorderBrush=Accent,Cursor=System.Windows.Input.Cursors.Hand};
            entry.Click+=delegate {if(action==0)ToggleTaskbar();else {Collapse();DockAppLauncher.Open(false,delegate(string result) {if(result.StartsWith("没有找到")||result.StartsWith("应用入口未能"))Dispatcher.BeginInvoke(new Action(delegate {MessageBox.Show(result,"应用入口");}));});}};apps.Children.Add(entry);
        }body.Children.Add(apps);UpdatePanel();
    }
    void ToggleTaskbar() {
        if(macDock!=null)macDock.MarkManualTaskbarIntent();
        try {
            var statusFile=System.IO.Path.Combine(stageRoot,"taskbar-status.ini");
            if(!File.Exists(statusFile)||!(File.ReadAllText(statusFile).StartsWith("State=hidden")||File.ReadAllText(statusFile).StartsWith("State=peek"))) {
                MessageBox.Show("先开启台前调度即可使用显示 / 隐藏切换。退出台前调度后，Windows 任务栏正常显示。","任务栏 / 托盘");return;
            }
            TaskbarToggleQueue.Request(stageRoot);
        }catch(Exception ex){MessageBox.Show(ex.Message,"任务栏 / 托盘");}
    }

    void RunStage(bool stop,bool elevated=false) {
        if(macDock!=null)macDock.CancelPendingRecovery();
        try {
            string root=stageRoot,exe=System.IO.Path.Combine(root,"StageManager.exe");
            if(!File.Exists(exe)){MessageBox.Show("台前调度程序不存在。请使用完整的 MacDesk 发布包。","MacDesk");return;}
            if(stop){File.WriteAllText(System.IO.Path.Combine(root,"stop.request"),"1");return;}
            Collapse();var start=new ProcessStartInfo(exe){UseShellExecute=true,WorkingDirectory=root};if(elevated)start.Verb="runas";Process.Start(start);
        }catch(System.ComponentModel.Win32Exception ex){if(ex.NativeErrorCode!=1223)MessageBox.Show("台前调度暂未能启动："+ex.Message,"MacDesk");}
        catch(Exception ex){MessageBox.Show("台前调度暂未能启动："+ex.Message,"MacDesk");}
    }
    static IntPtr NonActivatingMessage(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled) {
        if(message==0x21){handled=true;return new IntPtr(3);}return IntPtr.Zero;
    }
    void WriteDockStatus(string operation,Exception error) {
        try {DockRuntime.AtomicText(System.IO.Path.Combine(ownRoot,"dock-runtime.ini"),
            "Version="+((System.Reflection.AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(System.Reflection.Assembly.GetExecutingAssembly(),typeof(System.Reflection.AssemblyFileVersionAttribute))).Version+"\nPid="+Process.GetCurrentProcess().Id+"\nRunToken="+RunToken+"\nState="+operation+"\nExpanded="+expanded+"\nExitReason="+exitReason+"\nNonActivating=True\nLinkWorker=STA\nErrorType="+(error==null?"None":error.GetType().Name)+"\nUtc="+DateTime.UtcNow.ToString("o"));}catch{}
    }
    void OpenWeb(string name,string url) {
        Uri address;if(!Uri.TryCreate(url,UriKind.Absolute,out address) || (address.Scheme!="https" && address.Scheme!="http"))return;
        if(macDock!=null)macDock.CancelPendingRecovery();
        bool accepted=linkClicks.Accept(url,DateTime.UtcNow.Ticks);
        // Keep the click surface briefly so a double-click cannot land on the desktop
        // and undo the launch. Repeated clicks refresh restoration intent, not the URL.
        leaveTimer.Stop();linkHideTimer.Stop();linkHideTimer.Start();
        // Notify stage immediately so an older switch does not steal this click's focus.
        string marker=StageManager.Services.BrowserActivationIntent.CreateMarker(webLauncher.DefaultBrowser);
        try {StageManager.Services.BrowserActivationIntent.Write(stageRoot,marker);}catch{}
        if(!accepted)return;
        WriteDockStatus("link-queued",null);
        webLauncher.Open(url,stageRoot,marker,delegate(Exception error) {
            if(Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)return;
            Dispatcher.BeginInvoke(new Action(delegate {
                WriteDockStatus(error==null?"link-started":"link-error",error);
                if(error==null)return;
                if(tray!=null){tray.BalloonTipTitle="网页未能打开";tray.BalloonTipText=name+"打开失败，请重新点击或检查默认浏览器。";tray.ShowBalloonTip(4000);}
            }));
        });
    }
    void UpdatePanel() {
        if(clock==null)return;clock.Text=DateTime.Now.ToString("HH:mm");date.Text=DateTime.Now.ToString("MM月dd日 dddd",CultureInfo.GetCultureInfo("zh-CN"));
    }
    public void RenderPreview(string path) {
        expanded=true;Width=PanelWidth;Height=PanelHeight;BuildPanel();
        frame.Measure(new Size(PanelWidth,PanelHeight));frame.Arrange(new Rect(0,0,PanelWidth,PanelHeight));frame.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)(PanelWidth*2),(int)(PanelHeight*2),192,192,PixelFormats.Pbgra32);bitmap.Render(frame);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using(var stream=File.Create(path))encoder.Save(stream);
    }
}
class PersonalDock {
    static void SelfTest(string output) {
        DockDocumentLauncher.CheckProtocol();
        MacDockProtocol.Check();
        foreach(double scale in new[]{1.0,1.5,2.0}) {
            var monitor=new System.Drawing.Rectangle(-1920,-200,1920,1080);
            var bar=MacBottomDock.Placement(monitor,30,scale);
            if(!new Rect(monitor.X,monitor.Y,monitor.Width,monitor.Height).Contains(bar))throw new Exception("Mac Dock overflows screen at mixed DPI");
        }
        if(!DockAppLauncher.ValidAppId("OpenAI.Codex_abcd!App")||DockAppLauncher.ValidAppId("Codex")
            ||DockAppLauncher.ValidAppId("abc!App /command")||DockAppLauncher.ValidAppId("abc!App\""))
            throw new Exception("Unsafe application identity accepted");
        var screen=new Rect(-1920,0,1920,1080);var area=new Rect(-1920,0,1920,1040);
        foreach(DockEdge edge in Enum.GetValues(typeof(DockEdge))) {
            Point p=edge==DockEdge.Left?new Point(-1919,540):edge==DockEdge.Right?new Point(-1,540):edge==DockEdge.Top?new Point(-960,1):new Point(-960,1079);
            if(!EdgeGeometry.Hit(edge,.5,screen,p,1))throw new Exception("Missed edge on negative-coordinate monitor");
            if(EdgeGeometry.Hit(edge,.5,screen,new Point(-960,540),1))throw new Exception("Screen center triggers edge");
            if(EdgeGeometry.Hit(edge,.5,screen,new Point(2,540),1))throw new Exception("Other monitor triggers edge");
            if(EdgeGeometry.Nearest(screen,p)!=edge)throw new Exception("Wrong drag docking edge");
            foreach(double offset in new double[]{.06,.5,.94}) {
                var panel=EdgeGeometry.Panel(edge,offset,area,750,972,1.5);
                if(!area.Contains(panel))throw new Exception("Panel overflows taskbar or display");
            }
        }
        if(EdgeGeometry.Hit(DockEdge.Right,.5,screen,new Point(-1,300),1))throw new Exception("Unrelated edge segment triggers panel");
        if(!EdgeGeometry.Hit(DockEdge.Right,.5,screen,new Point(-1,300),1,true))throw new Exception("Whole-edge trigger misses position");
        if(!EdgeGeometry.Hit(DockEdge.Right,.5,screen,new Point(-7,540),2))throw new Exception("DPI-scaled hot zone failed");
        var clicks=new LinkClickPolicy();
        if(!clicks.Accept("https://example.org",10000000) || clicks.Accept("https://example.org",11000000)
            || !clicks.Accept("https://example.net",12000000) || !clicks.Accept("https://example.org",20000000))
            throw new Exception("Web click debounce blocked a distinct link or duplicated a click");
        File.WriteAllText(output,"PASS: Mac Dock fresh app list, duplicate identity, stale snapshot refusal and bounded high-DPI placement; document launch request token validation, expiration, deduplication and executable refusal; GUI application identity validation; web click debounce preserves different links; all four hidden trigger zones, whole-edge and restricted segment activation, snap selection, negative monitor coordinates, DPI-scaled hit area, panel clamping including taskbar. No UI input or account access.");
    }
    [STAThread] static void Main(string[] args) {
        if(DockWatchdog.TryRun(args))return;
        if(args.Length==2&&args[0]=="/check-dock-runtime") {try{DockRuntime.Check(args[1]);}catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e);Environment.ExitCode=1;}return;}
        if(args.Length==2&&args[0]=="/check-dock-watchdog") {try{if(!DockWatchdog.Check(args[1]))Environment.ExitCode=1;}catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e);Environment.ExitCode=1;}return;}
        if(args.Length==2&&args[0]=="/check-dock-recovery") {try{DockTrayRecovery.Check(args[1]);}catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e);Environment.ExitCode=1;}return;}
        if(args.Length==2&&args[0]=="/check-dock-presentation") {try{MacBottomDock.CheckRecoveryPresentation(args[1]);}catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e);Environment.ExitCode=1;}return;}
        if(args.Length==2&&args[0]=="/check-dock-pins") {try{DockPinChecks.Run(args[1]);}catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e);Environment.ExitCode=1;}return;}
        if(args.Length==2&&args[0]=="/check-mac-appearance-local") {try{MacBottomDock.CheckAppearance(args[1],true);}catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e.Message);Environment.ExitCode=1;}return;}
        if(args.Length==2&&args[0]=="/check-dock-hover") {try{MacBottomDock.CheckHover(args[1]);}catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e);Environment.ExitCode=1;}return;}
        if(args.Length==2&&args[0]=="/preview-mac-hover") {new MacBottomDock(AppDomain.CurrentDomain.BaseDirectory,delegate{},delegate{},true).RenderPreview(args[1],true);return;}
        if(args.Length==2&&args[0]=="/check-mac-appearance") {try{MacBottomDock.CheckAppearance(args[1]);}catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e.Message);Environment.ExitCode=1;}return;}
        if(System.IO.Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetExecutingAssembly().Location)=="CampusDockInputFixture") {
            MacBottomDock.RunInputFixture(AppDomain.CurrentDomain.BaseDirectory);return;
        }
        if(args.Length==2&&args[0]=="/preview-mac-dock"){new MacBottomDock("",delegate{},delegate{},true).RenderPreview(args[1]);return;}
        if(args.Length==2 && args[0]=="/preview") {new DockWindow(false,false).RenderPreview(args[1]);return;}
        if(args.Length==2 && args[0]=="/preview-light") {new DockWindow(false,true).RenderPreview(args[1]);return;}
        if(args.Length==2 && args[0]=="/selftest") {try {SelfTest(args[1]);}catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e.Message);Environment.ExitCode=1;}return;}
        bool created;using(var mutex=new Mutex(true,"Local\\MacDesk.Native.v1",out created)){
            if(!created){if(args.Length>0 && (args[0]=="/show" || args[0]=="/exit" || args[0]=="/recover")) {
                string marker=args[0]=="/show"?"show":args[0]=="/exit"?"exit":"recover";
                File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),marker+".marker"),Guid.NewGuid().ToString("N"));}return;}
            if(args.Length>0 && args[0]=="/exit")return;
            try {var app=new Application();var dock=new DockWindow(args.Length>0 && (args[0]=="/show"||args[0]=="/recover"));
                DockRuntime.Configure(app,dock,dock.RecoverWidgets,dock.ShowNotice,System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location));
                app.SessionEnding+=delegate{dock.Exit("session-ending");};
                if(args.Length>0&&args[0]=="/recover")dock.Loaded+=delegate{dock.RecoverWidgets();};
                app.Run(dock);}
            catch(Exception e){DockRuntime.Record(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),"main-loop",e);Environment.ExitCode=1;}
        }
    }
}
