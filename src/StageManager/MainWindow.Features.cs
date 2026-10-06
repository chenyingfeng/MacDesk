using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StageManager.Model;
using StageManager.Native.Window;
using StageManager.Services;
namespace StageManager;
public partial class MainWindow
{
    private Window? _groupsWindow;
    public bool MacMode {
        get=>PortablePreferences.Read("mac-mode");
        set {PortablePreferences.Write("mac-mode",value);RaisePropertyChanged();RaisePropertyChanged(nameof(KeepWindowsVisible));ApplyStageMode();}
    }
    private async void ApplyStageMode()
    {
        if(SceneManager==null)return;
        try {await SceneManager.SetStageModeAsync(MacMode,KeepWindowsVisible);
            foreach(var model in Scenes)model.UpdatePreviewSizes();
            SyncVisibilityByUpdatedTimeStamp();SortRecentScenes();HideSidebar();}
        catch(Exception ex){MessageBox.Show(ex.Message,"切换台前模式");}
    }
    public bool SidebarAlwaysVisible {
        get=>PortablePreferences.Read("sidebar-always-visible");
        set {PortablePreferences.Write("sidebar-always-visible",value);RaisePropertyChanged();QueueHoverUpdate();}
    }
    public bool RecentFirst {
        get=>PortablePreferences.Read("recent-first");
        set {PortablePreferences.Write("recent-first",value);RaisePropertyChanged();SortRecentScenes();}
    }
    public bool OneWindowPerApp {
        get=>PortablePreferences.Read("one-window-per-app");
        set {PortablePreferences.Write("one-window-per-app",value);RaisePropertyChanged();
            if(SceneManager!=null){SceneManager.OneWindowPerApp=value;ApplyStageMode();}}
    }
    public bool ClickWallpaperShowsDesktop {
        get=>!PortablePreferences.Read("disable-wallpaper-toggle");
        set {PortablePreferences.Write("disable-wallpaper-toggle",!value);RaisePropertyChanged();}
    }
    private void SortRecentScenes()
    {
        if((!RecentFirst && !MacMode) || IsSidebarDragging)return;
        var order=Scenes.OrderByDescending(s=>s.Updated).ToArray();
        for(int i=0;i<order.Length;i++)if(Scenes.IndexOf(order[i])!=i)Scenes.Move(Scenes.IndexOf(order[i]),i);
        AssignRowTilts();
    }
    private async void MenuItem_Desktop_Click(object sender,RoutedEventArgs e)
    {
        try {await SceneManager.ToggleDesktopAsync();HideSidebar();}
        catch(Exception ex){MessageBox.Show(ex.Message,"显示桌面");}
    }
    private void MenuItem_Groups_Click(object sender,RoutedEventArgs e)=>OpenTaskGroups();
    private void MenuItem_TaskbarToggle_Click(object sender,RoutedEventArgs e)=>
        TaskbarToggleQueue.Request(PortablePreferences.Root);
    internal void OpenTaskGroups()
    {
        MenuItem_Help_Click(this,new RoutedEventArgs());return;
    }
    private void OpenAdvancedTaskEditor()
    {
        if(_groupsWindow!=null){_groupsWindow.Activate();return;}
        HideSidebar();
        var window=new Window {Title="台前调度 · 任务分组",Width=680,Height=Math.Min(760,SystemParameters.WorkArea.Height-36),MinWidth=580,MinHeight=530,
            WindowStartupLocation=WindowStartupLocation.CenterScreen,FontFamily=new FontFamily("Microsoft YaHei UI"),
            Background=new SolidColorBrush(Color.FromRgb(247,243,250)),Foreground=new SolidColorBrush(Color.FromRgb(48,33,57))};
        _groupsWindow=window;
        window.Closed+=(_,_)=>{_groupsWindow=null;SceneManager.WindowsManager.SuppressNextDesktopClick();};
        var root=new DockPanel {Margin=new Thickness(24)};window.Content=root;
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        top.Children.Add(new TextBlock {Text="把一组窗口当作一个任务",FontSize=23,FontWeight=FontWeights.SemiBold});
        top.Children.Add(new TextBlock {Text="勾选窗口创建分组；选中已有分组可修改成员、命名、保存布局或拆分。",
            Margin=new Thickness(0,8,0,14),TextWrapping=TextWrapping.Wrap});
        var groups=new ComboBox {Margin=new Thickness(0,0,0,8),Height=30};top.Children.Add(groups);
        var name=new TextBox {Text="我的任务",Height=32,Padding=new Thickness(7)};top.Children.Add(name);
        var status=new TextBlock {Text="任务保存在本机。可关联文档或网页，重启应用后按标题核对恢复。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)};
        var bottom=new StackPanel();
        var bottomView=new ScrollViewer {Content=bottom,MaxHeight=window.Height*.6,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        DockPanel.SetDock(bottomView,Dock.Bottom);root.Children.Add(bottomView);
        bottom.Children.Add(status);
        var selected=new System.Collections.Generic.List<(CheckBox Box,IWindow Window)>();
        var list=new StackPanel();root.Children.Add(new ScrollViewer {Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,12,0,8)});
        bool busy=false;
        void Refresh(Scene? preferred=null) {
            groups.Items.Clear();groups.Items.Add(new ComboBoxItem {Content="新建任务分组"});
            foreach(var s in SceneManager.GetScenes())groups.Items.Add(new ComboBoxItem {Content=s.CustomName??s.Title.Replace(Environment.NewLine," · "),Tag=s});
            selected.Clear();list.Children.Clear();
            foreach(var w in SceneManager.FeatureWindows) {
                var box=new CheckBox {Content=w.ProcessName+"  ·  "+w.Title,Margin=new Thickness(0,6,0,6),IsChecked=preferred?.Windows.Any(x=>x.Handle==w.Handle)==true};
                selected.Add((box,w));list.Children.Add(box);
            }
            groups.SelectedItem=groups.Items.Cast<ComboBoxItem>().FirstOrDefault(i=>ReferenceEquals(i.Tag,preferred))??groups.Items[0];
        }
        groups.SelectionChanged+=(_,_)=> {
            if(groups.SelectedItem is not ComboBoxItem item)return;
            var scene=item.Tag as Scene;
            if(scene?.CustomName!=null)name.Text=scene.CustomName;
            foreach(var pair in selected)pair.Box.IsChecked=scene?.Windows.Any(w=>w.Handle==pair.Window.Handle)==true;
        };
        Button Action(string label,Func<Task> action,Panel container) {
            var button=new Button {Content=label,Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,5,8,5)};
            button.Click+=async(_,_)=> {if(busy)return;busy=true;window.IsEnabled=false;
                try {await action();}catch(Exception ex){status.Text=ex.Message;}
                finally {busy=false;window.IsEnabled=true;}};container.Children.Add(button);return button;
        }
        var presets=new ComboBox {Height=30,Margin=new Thickness(0,8,0,0)};
        var actions=new WrapPanel();bottom.Children.Add(actions);
        Action("创建 / 更新分组",async()=> {
            var members=selected.Where(x=>x.Box.IsChecked==true).Select(x=>x.Window).ToArray();
            if(string.IsNullOrWhiteSpace(name.Text)||name.Text.Trim().Length>60||members.Length==0)
                throw new InvalidOperationException("请填写分组名，并至少选择一个窗口。");
            if((groups.SelectedItem as ComboBoxItem)?.Tag is Scene old && old.CustomName!=null)await SceneManager.SplitTaskGroupAsync(old);
            var scene=await SceneManager.CreateTaskGroupAsync(name.Text,members);
            Refresh(scene);status.Text="分组已打开；关闭窗口会正常移除它的缩略图。";
        },actions);
        Action("保存布局",()=> {
            if((groups.SelectedItem as ComboBoxItem)?.Tag is not Scene scene)throw new InvalidOperationException("请先创建或选择一个分组。");
            SceneManager.RenameTaskGroup(scene,name.Text);var preset=SceneManager.CapturePreset(scene);
            WorkspacePresets.Save(WorkspacePresets.Load().Where(p=>p.Name!=preset.Name).Append(preset).ToArray());
            Refresh(scene);RefreshPresets();status.Text="已保存分组、位置、大小和最大化状态。";return Task.CompletedTask;
        },actions);
        Action("关联文档 / 网页",()=> {
            var picked=selected.Where(x=>x.Box.IsChecked==true).Select(x=>x.Window).ToArray();
            if(picked.Length!=1)throw new InvalidOperationException("请只勾选一个窗口，再关联它的文档或网页。");
            AssociateDocument(picked[0]);status.Text="关联已保存；请再保存任务布局以包含关联。";return Task.CompletedTask;
        },actions);
        Action("拆分为应用分组",async()=> {
            if((groups.SelectedItem as ComboBoxItem)?.Tag is not Scene scene)return;
            await SceneManager.SplitTaskGroupAsync(scene);Refresh();status.Text="已拆分，应用窗口保留当前状态。";
        },actions);
        Action("刷新窗口列表",()=>{Refresh();return Task.CompletedTask;},actions);
        Action("并排当前窗口",async()=> {
            var screen=SidebarScreen.WorkingArea;
            await SceneManager.ArrangeStageAsync(new Rect(screen.X,screen.Y,screen.Width,screen.Height));status.Text="当前台前窗口已并排。";
        },actions);
        Action("显示桌面 / 返回",async()=> {await SceneManager.ToggleDesktopAsync();window.Close();},actions);
        bottom.Children.Add(presets);
        void RefreshPresets() {presets.Items.Clear();foreach(var p in WorkspacePresets.Load())presets.Items.Add(new ComboBoxItem {Content=p.Name,Tag=p});if(presets.Items.Count>0)presets.SelectedIndex=0;}
        var presetActions=new WrapPanel();bottom.Children.Add(presetActions);
        Action("恢复保存的任务",async()=> {
            if((presets.SelectedItem as ComboBoxItem)?.Tag is not WorkspacePreset preset)return;
            int count=await ShowTaskRestoreAsync(preset);Refresh();status.Text="已恢复 "+count+" / "+preset.Members.Length+" 个核对后的窗口。";
        },presetActions);
        Action("删除保存的任务",()=> {
            if((presets.SelectedItem as ComboBoxItem)?.Tag is WorkspacePreset preset)WorkspacePresets.Save(WorkspacePresets.Load().Where(p=>p.Name!=preset.Name).ToArray());
            RefreshPresets();status.Text="已删除保存的布局，当前窗口不受影响。";return Task.CompletedTask;
        },presetActions);
        var preferences=new StackPanel();
        bottom.Children.Add(new Expander {Header="模式与偏好（Mac 模式、动画、多窗口）",Content=preferences,Margin=new Thickness(0,10,0,4)});
        foreach(var pair in new (string Label,string Property,bool Value)[] {
            ("Mac 模式：只显示当前任务组，拖入窗口自动加入此组",nameof(MacMode),MacMode),
            ("同一应用只显示一个窗口；Ctrl + Alt + Shift + W 轮换",nameof(OneWindowPerApp),OneWindowPerApp),
            ("窗口切换动效（尊重系统动画设置）",nameof(WindowMotion),WindowMotion),
            ("侧栏始终显示",nameof(SidebarAlwaysVisible),SidebarAlwaysVisible),
            ("最近使用的分组排在前面",nameof(RecentFirst),RecentFirst),
            ("保留其他台前窗口（关闭后专注当前组）",nameof(KeepWindowsVisible),KeepWindowsVisible),
            ("在任务中隐藏桌面图标",nameof(HideDesktopIcons),HideDesktopIcons),
            ("点击空白桌面显示桌面 / 返回任务",nameof(ClickWallpaperShowsDesktop),ClickWallpaperShowsDesktop)}) {
            var check=new CheckBox {Content=pair.Label,IsChecked=pair.Value,Margin=new Thickness(0,5,0,0)};
            check.SetBinding(CheckBox.IsCheckedProperty,new System.Windows.Data.Binding(pair.Property) {
                Source=this,Mode=System.Windows.Data.BindingMode.TwoWay});preferences.Children.Add(check);
        }
        bottom.Children.Add(new TextBlock {Text="快捷键：Ctrl + Alt + Shift + 1–9 切换分组；D 显示桌面；G 打开设置。",
            FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)});
        bottom.Children.Add(new TextBlock {Text="隐藏任务栏时，把鼠标移到屏幕下边缘可唤出；快捷中心也提供托盘和全部应用入口。",
            FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)});
        Refresh();RefreshPresets();window.Show();
    }
    internal void SelectGroupByNumber(int index) {
        if(_groupsWindow!=null || IsSidebarDragging || SceneManager.WindowsManager.SystemNavigationActive)return;
        var scene=Scenes.Where(s=>s.IsVisible).ElementAtOrDefault(index);
        if(scene!=null)SwitchSceneCommand.Execute(scene);
    }
    internal void ToggleDesktopFromKeyboard()=>MenuItem_Desktop_Click(this,new RoutedEventArgs());
    internal void CycleCurrentTaskWindows() {
        if(!OneWindowPerApp||_groupsWindow!=null||IsSidebarDragging||SceneManager.WindowsManager.SystemNavigationActive)return;
        var scene=Scenes.FirstOrDefault(s=>s.Scene.IsSelected&&SceneManager.InSidebarWorkspace(s.Scene,_sidebarMonitor));
        if(scene!=null)SwitchSceneCommand.Execute(scene);
    }
    internal void RenderGroupsPreview(string path)
    {
        // No WindowsManager.Start, native hooks, taskbar guard or external application operations.
        SceneManager=new SceneManager(new StageManager.Native.WindowsManager());
        OpenAdvancedTaskEditor();
        var window=_groupsWindow!;window.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth*2,(int)window.ActualHeight*2,192,192,PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using(var stream=System.IO.File.Create(path))encoder.Save(stream);
        window.Close();_overlapCheckTimer?.Dispose();SceneManager.Dispose();
    }
}
