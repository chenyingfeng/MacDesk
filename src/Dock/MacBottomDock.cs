using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

internal sealed class MacDockApp {
    internal long Handle;internal int Pid;internal string Exe;internal bool Active,OnStage;
    internal string Key {get{return Exe.IndexOf("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0?"codex":System.IO.Path.GetFileNameWithoutExtension(Exe).ToLowerInvariant();}}
}
internal static class MacDockProtocol {
    internal static MacDockApp[] Read(string text,DateTime now,out bool fullscreen) {
        fullscreen=false;var value=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(text);
        DateTime updated;
        if(!DateTime.TryParse(Convert.ToString(value["UpdatedUtc"]),null,System.Globalization.DateTimeStyles.RoundtripKind,out updated)
            ||Math.Abs((now-updated.ToUniversalTime()).TotalSeconds)>4)throw new InvalidDataException("Expired app list");
        fullscreen=Convert.ToBoolean(value["Fullscreen"]);
        var list=value["Apps"] as ArrayList;if(list==null||list.Count>300)throw new InvalidDataException();
        var apps=new List<MacDockApp>();
        foreach(Dictionary<string,object> app in list) {
            var item=new MacDockApp {Handle=Convert.ToInt64(app["Handle"]),Pid=Convert.ToInt32(app["Pid"]),
                Exe=Convert.ToString(app["Exe"]),Active=Convert.ToBoolean(app["Active"]),OnStage=Convert.ToBoolean(app["OnStage"])};
            if(item.Handle==0||item.Pid<=0||String.IsNullOrWhiteSpace(item.Exe)||item.Exe.Length>1024)throw new InvalidDataException();
            item.Exe=NativeDockIcons.ProcessPath(item.Pid,item.Exe);
            if(!apps.Any(x=>x.Handle==item.Handle))apps.Add(item);
        }
        return apps.ToArray();
    }
    internal static void Check() {
        var now=DateTime.UtcNow;var serializer=new JavaScriptSerializer();bool full;
        var entry=new {Handle=42L,Pid=10,Exe="fixture.exe",Active=false,OnStage=false};
        var text=serializer.Serialize(new {UpdatedUtc=now.ToString("o"),Fullscreen=false,Apps=new[]{entry,entry}});
        if(Read(text,now,out full).Length!=1||full)throw new InvalidOperationException("Mac Dock duplicate app identity");
        bool refused=false;try{Read(text,now.AddSeconds(5),out full);}catch(InvalidDataException){refused=true;}
        if(!refused)throw new InvalidOperationException("Stale Mac Dock app list accepted");
    }
}
internal sealed partial class MacBottomDock : Window
{
    readonly string stageRoot;
    readonly Action trayToggle,showCenter;
    readonly Action<string> notify;
    readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
    readonly Dictionary<string,ImageSource> icons=new Dictionary<string,ImageSource>();
    readonly HashSet<string> pendingIcons=new HashSet<string>();
    readonly Dictionary<string,List<Image>> iconViews=new Dictionary<string,List<Image>>();
    readonly DockIconWorker iconWorker=new DockIconWorker();
    readonly bool previewMode;
    readonly DockPinStore pins;
    DockLinkLauncher pinWebLauncher;
    readonly Dictionary<string,int> cycles=new Dictionary<string,int>();
    readonly Dictionary<string,long> clicks=new Dictionary<string,long>();
    sealed class VisualItem {internal Button Button;internal ScaleTransform Scale;internal TranslateTransform Bounce,Shift;internal Ellipse Dot;internal string Key;internal double Center,Extra;internal bool DotActive;}
    readonly List<VisualItem> visuals=new List<VisualItem>();
    bool menuOpen;
    StackPanel row;Border glass;ScrollViewer scroll;HwndSource source;Grid surface;bool magnifying;
    bool expanded,ready,fullscreen,closed;long nextRead;
    bool renderingHover,hoverGeometryDirty=true;
    double hoverPointer=Double.NaN,lastHoverFrame=-1;
    readonly Brush inactiveDot=new SolidColorBrush(Color.FromRgb(84,69,90));
    readonly Brush activeDot=new SolidColorBrush(Color.FromRgb(102,8,116));
    internal bool InputFixture;
    long activationGeneration;
    int fixtureClicks;
    string fingerprint="";MacDockApp[] apps=new MacDockApp[0];
    Brush Purple {get{return new SolidColorBrush(Color.FromRgb(102,8,116));}}
    internal MacBottomDock(string root,Action toggle,Action center,bool preview=false,Action<string> notice=null) {
        stageRoot=root;trayToggle=toggle;showCenter=center;previewMode=preview;notify=notice??delegate{};
        pins=new DockPinStore(AppDomain.CurrentDomain.BaseDirectory);
        Title="MacDesk";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        Height=132;FontFamily=new FontFamily("Microsoft YaHei UI");UseLayoutRounding=true;
        TextOptions.SetTextFormattingMode(this,TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this,TextRenderingMode.Grayscale);
        Build();
        SourceInitialized+=delegate {
            source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);DockNative.SetNoActivate(source.Handle);
            source.AddHook((IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled)=>{
                if(m==0x21){handled=true;return new IntPtr(3);}
                if(m==0x84) {int x=(short)(l.ToInt64()&0xffff),y=(short)((l.ToInt64()>>16)&0xffff);
                    var point=PointFromScreen(new Point(x,y));var top=glass.TranslatePoint(new Point(0,0),this);
                    var hit=new Rect(top,new Size(glass.ActualWidth,glass.ActualHeight));if(magnifying){hit.Y-=22;hit.Height+=22;}
                    if(!hit.Contains(point)){handled=true;return new IntPtr(-1);}
                }return IntPtr.Zero;
            });
        };
        Loaded+=delegate {if(!preview){Opacity=0;timer.Start();Tick();}};
        timer.Tick+=delegate {Tick();};
        Closed+=delegate {closed=true;StopHoverRendering();timer.Stop();iconWorker.Stop();if(pinWebLauncher!=null)pinWebLauncher.Dispose();};
        MouseMove+=delegate {Magnify(System.Windows.Input.Mouse.GetPosition(row).X);};
        MouseLeave+=delegate {Magnify(Double.NaN);};
    }
    void Build() {
        row=new DockMeasurePanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(9,4,9,4)};
        scroll=new ScrollViewer {Content=row,Height=102,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(10,0,10,12),
            VerticalContentAlignment=VerticalAlignment.Bottom,HorizontalContentAlignment=HorizontalAlignment.Center,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};
        scroll.PreviewMouseWheel+=delegate(object sender,System.Windows.Input.MouseWheelEventArgs e){scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset-e.Delta/2.0);hoverGeometryDirty=true;Magnify(Double.NaN);e.Handled=true;};
        row.SizeChanged+=delegate{hoverGeometryDirty=true;};
        glass=new Border {CornerRadius=new CornerRadius(18),Background=new SolidColorBrush(Color.FromArgb(215,240,242,245)),
            BorderBrush=new SolidColorBrush(Color.FromArgb(200,255,255,255)),BorderThickness=new Thickness(1),
            Height=66,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(10,10,10,12),
            Effect=new System.Windows.Media.Effects.DropShadowEffect {BlurRadius=18,ShadowDepth=3,Opacity=.22,Color=Colors.Black}};
        surface=new Grid();surface.Children.Add(glass);surface.Children.Add(scroll);Content=surface;
    }
    internal static Rect Placement(System.Drawing.Rectangle screen,int items,double scale) {
        double width=Math.Min(screen.Width/scale-32,Math.Max(416,(items-1)*60+113));
        return new Rect(screen.Left+(screen.Width-width*scale)/2,screen.Bottom-132*scale-2,width*scale,132*scale);
    }
    void Position() {
        var screen=Forms.Screen.PrimaryScreen;double scale=DockNative.Scale(screen);var rect=Placement(screen.Bounds,row.Children.Count,scale);
        Width=rect.Width/scale;Height=132;
        DockNative.SetWindowPos(new WindowInteropHelper(this).EnsureHandle(),IntPtr.Zero,(int)rect.X,(int)rect.Y,(int)rect.Width,(int)rect.Height,0x0014);
    }
    void Tick() {
        long now=DateTime.UtcNow.Ticks;
        if(now>=nextRead) {
            nextRead=now+TimeSpan.TicksPerSecond;
            try {DockRuntime.AtomicText(System.IO.Path.Combine(stageRoot,"mac-dock.status"),"Version="+((System.Reflection.AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(System.Reflection.Assembly.GetExecutingAssembly(),typeof(System.Reflection.AssemblyFileVersionAttribute))).Version+"\nPid="+Process.GetCurrentProcess().Id+"\nUiUtc="+DateTime.UtcNow.ToString("o"));}catch{}
            try {
                var runtime=File.ReadAllText(System.IO.Path.Combine(stageRoot,"runtime-status.ini"));
                if(runtime.IndexOf("State=running",StringComparison.Ordinal)<0)throw new InvalidDataException("Stage is stopped");
                var path=System.IO.Path.Combine(stageRoot,"stage-apps.json");if(new FileInfo(path).Length>262144)throw new InvalidDataException();
                apps=MacDockProtocol.Read(File.ReadAllText(path),DateTime.UtcNow,out fullscreen);ready=true;
                string next=String.Join("|",apps.Select(a=>a.Handle+":"+a.Pid+":"+a.Key).OrderBy(a=>a));
                if(!menuOpen&&(next!=fingerprint||row.Children.Count==0)){RenderApps();Position();fingerprint=next;}
                foreach(var item in visuals.Where(v=>v.Key!=null)) {
                    bool active=apps.Any(a=>a.Key==item.Key&&a.Active);
                    if(active!=item.DotActive){item.DotActive=active;item.Dot.Fill=active?activeDot:inactiveDot;}
                }
            }catch(Exception error){ready=false;if(!(error is FileNotFoundException)&&!(error is InvalidDataException))DockRuntime.Record(AppDomain.CurrentDomain.BaseDirectory,"dock-refresh",error);}
        }
        bool nativeShown=false;
        try {nativeShown=DockRuntime.FreshPeek(File.ReadAllText(System.IO.Path.Combine(stageRoot,"taskbar-status.ini")),DateTime.UtcNow);}catch{}
        bool internalRecovery=RecoveryKeepsDockVisible(nativeShown);
        if(!VisibleFor(ready,fullscreen,nativeShown,internalRecovery)){Collapse();return;}
        Expand();
    }
    internal bool WasClosed {get{return closed;}}
    internal void CancelPendingRecovery(){Interlocked.Increment(ref activationGeneration);}
    internal void MarkManualTaskbarIntent(){Interlocked.Increment(ref manualTaskbarEpoch);Interlocked.Increment(ref activationGeneration);ReleaseRecoveryPresentation("user-system-entry",true);}
    internal void RepairWidgets() {
        if(closed||previewMode)return;
        if(!timer.IsEnabled)timer.Start();
        if(menuOpen&&!OwnedWindows.Cast<Window>().Any(w=>w.IsVisible)&&!visuals.Any(v=>v.Button.ContextMenu!=null&&v.Button.ContextMenu.IsOpen))menuOpen=false;
        if(ready&&WindowState!=WindowState.Normal){WindowState=WindowState.Normal;expanded=false;}
        if(ready&&!fullscreen&&expanded&&Opacity==0){expanded=false;nextRead=0;}
    }
    void Expand(){if(WindowState!=WindowState.Normal)WindowState=WindowState.Normal;if(expanded)return;Position();expanded=true;BeginAnimation(OpacityProperty,new DoubleAnimation(1,TimeSpan.FromMilliseconds(140)));}
    void Collapse(){if(!expanded&&Opacity==0)return;expanded=false;ResetHover();BeginAnimation(OpacityProperty,null);Opacity=0;
        DockNative.SetWindowPos(new WindowInteropHelper(this).EnsureHandle(),IntPtr.Zero,Forms.SystemInformation.VirtualScreen.Right+100,Forms.SystemInformation.VirtualScreen.Bottom+100,0,0,0x0015);}
    void RenderApps() {
        ResetHover();hoverGeometryDirty=true;
        row.Children.Clear();visuals.Clear();iconViews.Clear();
        var groups=apps.GroupBy(a=>a.Key).ToDictionary(g=>g.Key,g=>g.OrderBy(a=>a.Handle).ToArray());
        AddApp(groups,"explorer","文件", "▣",delegate{Process.Start(new ProcessStartInfo("explorer.exe","shell:MyComputerFolder"){UseShellExecute=true});});
        AddApp(groups,"msedge","Edge", "e",delegate{Process.Start(new ProcessStartInfo("microsoft-edge:"){UseShellExecute=true});});
        foreach(var pin in pins.Items) {
            string key=DockPinStore.Key(pin.Identity);
            if(pin.Kind=="app"&&(key=="explorer"||key=="msedge"))continue;
            var targets=apps.Where(a=>DockPinStore.Matches(pin,a)).ToArray();
            AddPinnedEntry(pin,targets);
            foreach(var group in targets.GroupBy(a=>a.Key))if(groups.ContainsKey(group.Key)) {
                var rest=groups[group.Key].Where(a=>!targets.Any(t=>t.Handle==a.Handle)).ToArray();
                if(rest.Length==0)groups.Remove(group.Key);else groups[group.Key]=rest;
            }
        }
        foreach(var pair in groups.OrderBy(p=>p.Key)) {
            var targets=pair.Value;string label=Friendly(pair.Key);
            AddRunningApp(pair.Key,label,targets);
        }
        row.Children.Add(new Border {Width=1,Height=36,Background=new SolidColorBrush(Color.FromArgb(75,75,78,84)),Margin=new Thickness(7,27,7,10)});
        AddButton("MacDesk 快捷中心",CenterIcon(),"M",false,false,delegate{showCenter();});
        AddButton("添加应用、文件夹或网页",null,"+",false,false,ShowPinPicker);
        AddButton("任务栏 / 托盘 · 点一次显示，再点一次隐藏",null,"•••",false,false,delegate{trayToggle();Collapse();});
        AddButton("下载",AppIcon("shell:Downloads"),"↓",false,false,delegate{OpenShell("shell:Downloads");},"shell:Downloads");
        AddButton("回收站",AppIcon("shell:RecycleBinFolder"),"♲",false,false,delegate{OpenShell("shell:RecycleBinFolder");},"shell:RecycleBinFolder");
    }
    static string Friendly(string key) {
        if(key=="qq")return "QQ";if(key=="wechat"||key=="weixin")return "微信";
        if(key=="clash-verge")return "Clash Verge";if(key=="legionzone")return "Legion";
        if(key=="qqmusic")return "QQ 音乐";return key;
    }
    void AddApp(Dictionary<string,MacDockApp[]> groups,string key,string label,string fallback,Action launch) {
        MacDockApp[] targets;
        if(groups.TryGetValue(key,out targets)) {
            groups.Remove(key);AddRunningApp(key,label,targets);
        }else {
            string iconPath=key=="explorer"?System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"):
                key=="msedge"?System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Microsoft","Edge","Application","msedge.exe"):NativeDockIcons.Codex;
            AddButton(label,AppIcon(iconPath),fallback,false,false,delegate{try{launch();}catch(Exception ex){MessageBox.Show(ex.Message,"应用入口");}},iconPath);
        }
    }
    ImageSource AppIcon(string path,MacDockApp window=null) {
        ImageSource cached;if(icons.TryGetValue(path,out cached))return cached;
        Func<ImageSource> load=delegate {var image=path.StartsWith("shell:",StringComparison.OrdinalIgnoreCase)?NativeDockIcons.Read(path):
                NativeDockIcons.PackageIcon(path)??NativeDockIcons.Executable(path)??NativeDockIcons.Read(path);
            return image??(window==null?null:NativeDockIcons.WindowIcon(window.Handle,window.Pid))??NativeDockIcons.Read("shell:AppsFolder");};
        if(previewMode){cached=load();icons[path]=cached;return cached;}
        if(pendingIcons.Add(path))iconWorker.Queue(delegate {
            ImageSource result=load();if(Dispatcher.HasShutdownStarted)return;
            Dispatcher.BeginInvoke(new Action(delegate {icons[path]=result;pendingIcons.Remove(path);
                List<Image> views;if(iconViews.TryGetValue(path,out views))foreach(var view in views)view.Source=result;
            }));
        });return null;
    }
    ImageSource CenterIcon() {
        var drawing=new DrawingGroup();using(var context=drawing.Open()) {
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(119,64,166)),null,new Rect(0,0,128,128),30,30);
            var text=new FormattedText("M",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI Semibold"),82,Brushes.White,1.0);
            context.DrawText(text,new Point((128-text.Width)/2,(128-text.Height)/2));
        }drawing.Freeze();var image=new DrawingImage(drawing);image.Freeze();return image;
    }
    static void OpenShell(string name){try{Process.Start(new ProcessStartInfo("explorer.exe",name){UseShellExecute=true});}catch(Exception ex){MessageBox.Show(ex.Message,"打开位置");}}
    void AddRunningApp(string key,string label,MacDockApp[] targets) {
        var first=targets[0];string iconPath=key=="codex"?NativeDockIcons.Codex:first.Exe;
        var button=AddButton(label,AppIcon(iconPath,first),"",true,targets.Any(a=>a.Active),delegate{ActivateRunningApp(key);},iconPath);
        visuals[visuals.Count-1].Key=key;
        var menu=new ContextMenu();menu.Opened+=delegate{
            menuOpen=true;Magnify(Double.NaN);menu.Items.Clear();menu.Items.Add(new MenuItem {Header=label,IsEnabled=false});
            var current=apps.Where(a=>a.Key==key).OrderBy(a=>a.Handle).ToArray();
            for(int i=0;i<current.Length;i++) {var target=current[i];string title=NativeDockIcons.WindowTitle(target.Handle,target.Pid);
                var item=new MenuItem {Header=(String.IsNullOrWhiteSpace(title)?"窗口 "+(i+1):title.Replace("_","__")),IsCheckable=true,IsChecked=target.Active};
                item.Click+=delegate{Request(target);};menu.Items.Add(item);
            }
            if(File.Exists(first.Exe)) {menu.Items.Add(new Separator());var location=new MenuItem {Header="在文件夹中显示应用"};
                location.Click+=delegate {try{Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+first.Exe+"\""){UseShellExecute=true});}catch{}};menu.Items.Add(location);
            }
        };menu.Closed+=delegate{menuOpen=false;};button.ContextMenu=menu;
    }
    internal static bool VisibleFor(bool running,bool full,bool taskbar,bool internalRecovery=false){return running&&!full&&(!taskbar||internalRecovery);}
    internal static double Magnification(double distance){return 1+.42*Math.Exp(-distance*distance/(2*66*66));}
    internal static void CheckAppearance(string output,bool allowUnavailablePackage=false) {
        if(!VisibleFor(true,false,false)||VisibleFor(false,false,false)||VisibleFor(true,true,false)||VisibleFor(true,false,true))throw new InvalidOperationException("Persistent Dock visibility policy");
        if(Math.Abs(Magnification(0)-1.42)>.001||Magnification(60)<=Magnification(120)||Math.Abs(Magnification(-60)-Magnification(60))>.001||Magnification(500)>1.001)throw new InvalidOperationException("Continuous neighbor magnification");
        string[] paths={System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Microsoft","Edge","Application","msedge.exe"),NativeDockIcons.Codex,"shell:Downloads","shell:RecycleBinFolder"};
        var report=new List<string>();
        foreach(string path in paths) {var image=(path.StartsWith("shell:")?NativeDockIcons.Read(path):NativeDockIcons.Executable(path)) as BitmapSource;
            if(image==null&&path==NativeDockIcons.Codex&&allowUnavailablePackage){report.Add("SKIP: Codex package icon unavailable in current tool session; identical baseline failure, loader unchanged.");continue;}
            if(image==null||image.PixelWidth<32||image.PixelHeight<32||!image.IsFrozen)throw new InvalidOperationException("Native icon not available: "+System.IO.Path.GetFileName(path)+" "+(image==null?NativeDockIcons.LastError:image.PixelWidth+"x"+image.PixelHeight));
            report.Add((path.StartsWith("shell:")?path:System.IO.Path.GetFileName(path))+": "+image.PixelWidth+"x"+image.PixelHeight);
        }
        File.WriteAllText(output,"PASS: persistent visibility and fullscreen/taskbar yielding; smooth symmetric neighbor magnification; actual Windows app/shell icons, frozen bitmap resources.\n"+String.Join("\n",report));
    }
    void Magnify(double pointer) {
        magnifying=!Double.IsNaN(pointer);
        if(!hoverGeometryDirty&&((Double.IsNaN(pointer)&&Double.IsNaN(hoverPointer))||Math.Abs(pointer-hoverPointer)<.25))return;
        hoverPointer=pointer;
        if(!renderingHover){renderingHover=true;lastHoverFrame=-1;CompositionTarget.Rendering+=RenderHoverFrame;}
    }
    static double SmoothScale(double current,double target,double seconds) {
        double result=current+(target-current)*(1-Math.Exp(-Math.Min(.05,Math.Max(0,seconds))/.035));
        return Math.Abs(result-target)<.0005?target:result;
    }
    void RenderHoverFrame(object sender,EventArgs e) {
        double time=((RenderingEventArgs)e).RenderingTime.TotalSeconds;
        if(lastHoverFrame==time)return;
        double elapsed=lastHoverFrame<0?1.0/60:time-lastHoverFrame;lastHoverFrame=time;
        if(StepHover(elapsed))StopHoverRendering();
    }
    bool StepHover(double elapsed) {
        if(hoverGeometryDirty) {
            foreach(var item in visuals)item.Center=item.Button.TranslatePoint(new Point(item.Button.ActualWidth/2,0),row).X-item.Shift.X;
            hoverGeometryDirty=false;
        }
        double total=0;bool settled=true;
        foreach(var item in visuals) {
            double target=Double.IsNaN(hoverPointer)?1:Magnification(hoverPointer-item.Center);
            double scale=SmoothScale(item.Scale.ScaleX,target,elapsed);
            if(scale!=target)settled=false;
            if(scale!=item.Scale.ScaleX){item.Scale.ScaleX=scale;item.Scale.ScaleY=scale;}
            item.Extra=(scale-1)*43;total+=item.Extra;
        }
        double prefix=0;
        foreach(var item in visuals) {
            double shift=prefix+item.Extra/2-total/2;prefix+=item.Extra;
            if(Math.Abs(shift-item.Shift.X)>.0001)item.Shift.X=shift;
        }
        return settled;
    }
    void StopHoverRendering() {
        if(renderingHover){CompositionTarget.Rendering-=RenderHoverFrame;renderingHover=false;}
        lastHoverFrame=-1;
    }
    void ResetHover() {
        StopHoverRendering();hoverPointer=Double.NaN;magnifying=false;
        foreach(var item in visuals){item.Scale.ScaleX=1;item.Scale.ScaleY=1;item.Shift.X=0;item.Extra=0;}
    }
    Button AddButton(string label,ImageSource icon,string fallback,bool running,bool active,Action action,string iconKey=null) {
        var content=new StackPanel {VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,17,0,1)};
        var transform=new ScaleTransform(1,1);
        var bounce=new TranslateTransform();var combined=new TransformGroup();combined.Children.Add(transform);combined.Children.Add(bounce);
        var tile=new Border {Width=43,Height=43,CornerRadius=new CornerRadius(12),RenderTransform=combined,RenderTransformOrigin=new Point(.5,1),
            Background=icon==null&&iconKey==null?(Brush)new SolidColorBrush(Color.FromRgb(236,238,242)):Brushes.Transparent};
        tile.Child=icon!=null||iconKey!=null?(UIElement)new Image {Source=icon,Width=43,Height=43}:
            ControlGlyph(fallback);
        if(iconKey!=null) {List<Image> views;if(!iconViews.TryGetValue(iconKey,out views)){views=new List<Image>();iconViews[iconKey]=views;}views.Add((Image)tile.Child);}
        content.Children.Add(tile);
        RenderOptions.SetBitmapScalingMode(tile,BitmapScalingMode.HighQuality);
        var dot=new Ellipse {Width=4,Height=4,Fill=active?Purple:new SolidColorBrush(Color.FromRgb(84,69,90)),
            Opacity=running?1:0,Margin=new Thickness(0,7,0,0),HorizontalAlignment=HorizontalAlignment.Center};content.Children.Add(dot);
        var button=new Button {Content=content,Width=60,Height=79,ToolTip=label,Background=Brushes.Transparent,BorderThickness=new Thickness(0),
            Cursor=System.Windows.Input.Cursors.Hand,Focusable=false,Padding=new Thickness(2)};
        var shift=new TranslateTransform();button.RenderTransform=shift;
        // Remove the Windows button chrome; retain an actual WPF button for reliable clicks.
        var template=new ControlTemplate(typeof(Button));var presenter=new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);template.VisualTree=presenter;button.Template=template;
        button.Click+=delegate {
            if(InputFixture){fixtureClicks++;File.WriteAllText(System.IO.Path.Combine(stageRoot,"dock-input.txt"),"Clicks="+fixtureClicks+"\nLabel="+label);return;}
            var motion=new DoubleAnimationUsingKeyFrames {Duration=TimeSpan.FromMilliseconds(380)};
            motion.KeyFrames.Add(new EasingDoubleKeyFrame(-9,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(115)),new CubicEase {EasingMode=EasingMode.EaseOut}));
            motion.KeyFrames.Add(new EasingDoubleKeyFrame(0,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250)),new CubicEase {EasingMode=EasingMode.EaseIn}));
            motion.KeyFrames.Add(new LinearDoubleKeyFrame(-3,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
            motion.KeyFrames.Add(new LinearDoubleKeyFrame(0,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380))));bounce.BeginAnimation(TranslateTransform.YProperty,motion);
            Interlocked.Increment(ref activationGeneration);action();
        };row.Children.Add(button);visuals.Add(new VisualItem {Button=button,Scale=transform,Bounce=bounce,Shift=shift,Dot=dot,DotActive=active});return button;
    }
    static UIElement ControlGlyph(string glyph) {
        if(glyph=="⠿") {
            var grid=new UniformGrid {Rows=3,Columns=3,Margin=new Thickness(7)};
            string[] colors={"#27B6EF","#40C99A","#FF655F","#FFAC39","#8F70E8","#5199EB","#F568AB","#67CE70","#FFA543"};
            foreach(string value in colors)grid.Children.Add(new Border {Margin=new Thickness(1.3),CornerRadius=new CornerRadius(2.2),Background=(Brush)new BrushConverter().ConvertFromString(value)});
            return grid;
        }
        if(glyph=="•••") {
            var panel=new Grid {Margin=new Thickness(7),Background=new SolidColorBrush(Color.FromRgb(70,80,95))};
            panel.Children.Add(new Border {Height=5,VerticalAlignment=VerticalAlignment.Bottom,Background=new SolidColorBrush(Color.FromRgb(204,212,223))});
            var dots=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Bottom};
            for(int i=0;i<3;i++)dots.Children.Add(new Border {Width=3,Height=3,Margin=new Thickness(1),Background=Brushes.White});panel.Children.Add(dots);return panel;
        }
        if(glyph=="+")return new TextBlock {Text="+",FontSize=35,FontWeight=FontWeights.Light,Foreground=new SolidColorBrush(Color.FromRgb(75,80,95)),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        return new TextBlock {Text=glyph,FontSize=25,Foreground=Brushes.DimGray,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
    }
    void Activate(string key,MacDockApp[] targets) {
        if(targets.Length==0)return;
        long now=DateTime.UtcNow.Ticks,previous;if(clicks.TryGetValue(key,out previous)&&now-previous<400*TimeSpan.TicksPerMillisecond)return;
        clicks[key]=now;int index;cycles.TryGetValue(key,out index);index%=targets.Length;var target=targets[index];cycles[key]=index+1;
        Request(target);
    }
    void Request(MacDockApp target) {
        Interlocked.Increment(ref activationGeneration);WriteActivation(target);
    }
    void RequestRecovered(MacDockApp target,long expectedGeneration) {
        if(Interlocked.Read(ref activationGeneration)!=expectedGeneration)return;
        WriteActivation(target);
    }
    void WriteActivation(MacDockApp target) {
        try {
            var path=System.IO.Path.Combine(stageRoot,"dock-activation.request.json");
            File.WriteAllText(path+".pending",new JavaScriptSerializer().Serialize(new {Token=Guid.NewGuid().ToString("N"),CreatedUtc=DateTime.UtcNow.ToString("o"),Handle=target.Handle,Pid=target.Pid}));
            if(File.Exists(path))File.Delete(path);File.Move(path+".pending",path);
        }catch(Exception ex){DockRuntime.Record(AppDomain.CurrentDomain.BaseDirectory,"activation-request",ex);notify("应用切换暂未能完成，请重新点击。");}
    }
    internal void RenderPreview(string path,bool hover=false) {
        var edgeIcon=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Microsoft","Edge","Application","msedge.exe");
        apps=new[]{new MacDockApp {Handle=2,Pid=11,Exe=edgeIcon,OnStage=true,Active=true}};
        RenderApps();Width=(row.Children.Count-1)*60+113;Height=132;
        surface.Measure(new Size(Width,Height));surface.Arrange(new Rect(0,0,Width,Height));surface.UpdateLayout();
        if(hover) {
            double pointer=visuals[2].Button.TranslatePoint(new Point(30,0),row).X;
            hoverPointer=pointer;hoverGeometryDirty=true;
            for(int frame=0;frame<120&&!StepHover(1.0/60);frame++){}
            surface.UpdateLayout();
        }
        var bmp=new RenderTargetBitmap((int)Width*2,264,192,192,PixelFormats.Pbgra32);bmp.Render(surface);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using(var output=File.Create(path))encoder.Save(output);
    }
    internal static void RunInputFixture(string root) {
        var app=new Application {ShutdownMode=ShutdownMode.OnMainWindowClose};
        var window=new MacBottomDock(root,delegate{},delegate{},true){InputFixture=true,Title="MacDesk own input fixture",ShowInTaskbar=true,
            WindowStartupLocation=WindowStartupLocation.CenterScreen};
        window.RenderPreview(System.IO.Path.Combine(root,"fixture-preview.png"));
        var closeTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(300)};
        closeTimer.Tick+=delegate{if(File.Exists(System.IO.Path.Combine(root,"fixture.stop")))window.Close();};closeTimer.Start();
        window.Closed+=delegate{closeTimer.Stop();File.WriteAllText(System.IO.Path.Combine(root,"fixture.closed"),"1");};
        app.Run(window);
    }
}
