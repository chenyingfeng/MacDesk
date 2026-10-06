using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StageManager.Native.Window;
using StageManager.Services;
namespace StageManager;
public partial class MainWindow
{
    private void AssociateDocument(IWindow window)
    {
        var dialog=new Window {Title="关联文档或网页",Width=600,Height=250,ResizeMode=ResizeMode.NoResize,
            Owner=_groupsWindow,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=new FontFamily("Microsoft YaHei UI")};
        var body=new StackPanel {Margin=new Thickness(20)};dialog.Content=body;
        body.Children.Add(new TextBlock {Text="为此窗口关联以后要重新打开的文档或网页：",TextWrapping=TextWrapping.Wrap});
        body.Children.Add(new TextBlock {Text=window.Title,Margin=new Thickness(0,6,0,10),TextTrimming=TextTrimming.CharacterEllipsis});
        var input=new TextBox {Text=WorkspaceRestoration.Target(window)??"",Padding=new Thickness(8),Margin=new Thickness(0,0,0,10)};body.Children.Add(input);
        var actions=new WrapPanel();body.Children.Add(actions);
        var browse=new Button {Content="选择文档…",Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,8,0)};
        browse.Click+=(_,_)=> {
            var picker=new Microsoft.Win32.OpenFileDialog {Title="选择要关联的文档",CheckFileExists=true,Multiselect=false,
                Filter="文档与图片|*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.txt;*.md;*.tex;*.csv;*.png;*.jpg;*.jpeg"};
            if(picker.ShowDialog(dialog)==true)input.Text=picker.FileName;
        };actions.Children.Add(browse);
        var save=new Button {Content="保存关联",Padding=new Thickness(12,7,12,7)};
        save.Click+=(_,_)=> {try {WorkspaceRestoration.Associate(window,input.Text.Trim());dialog.DialogResult=true;}
            catch(Exception ex){MessageBox.Show(dialog,ex.Message,"关联未保存");}};actions.Children.Add(save);
        dialog.ShowDialog();
    }
    private Task<int> ShowTaskRestoreAsync(WorkspacePreset preset)
    {
        var completion=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog=new Window {Title="核对并恢复 · "+preset.Name,Width=720,Height=Math.Min(700,SystemParameters.WorkArea.Height-40),
            Owner=_groupsWindow,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=new FontFamily("Microsoft YaHei UI"),
            Background=new SolidColorBrush(Color.FromRgb(247,243,250))};
        var root=new DockPanel {Margin=new Thickness(22)};dialog.Content=root;
        var heading=new TextBlock {Text="按窗口标题核对任务",FontSize=22,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,14)};
        DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var message=new TextBlock {Text="同名或标题变化的窗口请手动选择；未找到的窗口可以先打开关联文档。全屏窗口请先退出全屏，再刷新核对。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,8)};footer.Children.Add(message);
        var buttons=new WrapPanel();footer.Children.Add(buttons);
        var list=new StackPanel();root.Children.Add(new ScrollViewer {Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var selectors=new List<(WorkspaceMember Member,ComboBox Box)>();bool restoring=false;int restored=0;
        void Refresh() {
            list.Children.Clear();selectors.Clear();
            var available=SceneManager.FeatureWindows.Where(w=>WorkspaceEnvironment.IsCurrent(w.Handle)&&!WorkspaceEnvironment.IsFullscreen(w.Handle)).ToArray();
            foreach(var choice in WorkspaceRestoration.Plan(preset,available)) {
                list.Children.Add(new TextBlock {Text=choice.Member.Process+"  ·  "+(choice.Member.TitleHint??"旧版任务，请手动选择窗口"),
                    TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,6)});
                var box=new ComboBox {MinHeight=30,MaxDropDownHeight=250};
                box.Items.Add(new ComboBoxItem {Content="暂不恢复此项"});
                foreach(var candidate in choice.Candidates)box.Items.Add(new ComboBoxItem {Content=candidate.Title+"  ["+WorkspaceEnvironment.MonitorOf(candidate)+"]",Tag=candidate});
                box.SelectedItem=box.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>ReferenceEquals(x.Tag,choice.Suggested))??box.Items[0];
                list.Children.Add(box);selectors.Add((choice.Member,box));
                if(choice.Member.OpenTarget!=null)list.Children.Add(new TextBlock {Text="关联："+choice.Member.OpenTarget,FontSize=11,
                    TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DimGray,Margin=new Thickness(0,4,0,0)});
            }
        }
        Button Action(string name,Func<Task> action) {
            var button=new Button {Content=name,Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,0,8,8)};
            button.Click+=async(_,_)=> {if(restoring)return;restoring=true;buttons.IsEnabled=false;
                try {await action();}catch(Exception ex){message.Text=ex.Message;}
                finally {restoring=false;buttons.IsEnabled=true;}};buttons.Children.Add(button);return button;
        }
        Action("打开未找到项的关联文档",async()=> {
            var targets=selectors.Where(x=>(x.Box.SelectedItem as ComboBoxItem)?.Tag is not IWindow)
                .Select(x=>x.Member.OpenTarget).OfType<string>().ToArray();
            message.Text=await WorkspaceRestoration.OpenTargetsAsync(targets);
            await Task.Delay(1500);Refresh();
        });
        Action("刷新窗口",()=>{Refresh();return Task.CompletedTask;});
        Action("恢复所选窗口",async()=> {
            var chosen=selectors.Select(x=>(Member:x.Member,Window:(x.Box.SelectedItem as ComboBoxItem)?.Tag as IWindow))
                .Where(x=>x.Window!=null).Select(x=>(x.Member,Window:x.Window!)).ToArray();
            if(chosen.Length==0)throw new InvalidOperationException("请先打开应用，并选择至少一个对应窗口。");
            if(chosen.Select(x=>x.Window.Handle).Distinct().Count()!=chosen.Length)
                throw new InvalidOperationException("同一窗口不能对应多个任务成员，请重新核对。");
            restored=await SceneManager.ApplyPresetAsync(preset,chosen);dialog.Close();
        });
        dialog.Closed+=(_,_)=>completion.TrySetResult(restored);
        Refresh();dialog.Show();return completion.Task;
    }
    internal void RenderRestorePreview(string path)
    {
        SceneManager=new SceneManager(new StageManager.Native.WindowsManager());
        _=ShowTaskRestoreAsync(new WorkspacePreset("论文写作",[
            new WorkspaceMember("msedge.exe",0,.1,.1,.5,.7,false,TitleHint:"论文项目 · 浏览器",OpenTarget:"https://example.org/project"),
            new WorkspaceMember("WINWORD.EXE",0,.5,.1,.45,.7,false,TitleHint:"论文草稿.docx")]));
        var dialog=Application.Current.Windows.OfType<Window>().First(w=>w.Title.StartsWith("核对并恢复"));
        dialog.UpdateLayout();var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth*2,(int)dialog.ActualHeight*2,192,192,PixelFormats.Pbgra32);
        bitmap.Render(dialog);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using(var stream=System.IO.File.Create(path))encoder.Save(stream);
        dialog.Close();_overlapCheckTimer?.Dispose();SceneManager.Dispose();
    }
}
