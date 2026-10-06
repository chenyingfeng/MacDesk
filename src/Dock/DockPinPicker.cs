using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms=System.Windows.Forms;

internal sealed class DockPinCandidate {
    internal string Name,Path;
    public override string ToString(){return Name;}
}
internal sealed class DockPinPicker : Window {
    readonly DockPinStore store;readonly TextBox search=new TextBox();readonly ListBox list=new ListBox();readonly TextBlock status=new TextBlock();
    readonly List<DockPinCandidate> candidates=new List<DockPinCandidate>();
    internal DockPinPicker(DockPinStore pins,MacDockApp[] running) {
        store=pins;Title="添加到程序坞";Width=610;Height=540;MinWidth=530;MinHeight=430;ShowInTaskbar=true;ResizeMode=ResizeMode.CanResize;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=13;Background=new SolidColorBrush(Color.FromRgb(245,246,248));
        var root=new Grid {Margin=new Thickness(22)};root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});Content=root;
        var heading=new TextBlock {Text="把常用入口留在程序坞",FontSize=22,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,15)};root.Children.Add(heading);
        search.Margin=new Thickness(0,0,0,12);search.Padding=new Thickness(9);search.ToolTip="搜索软件名称";root.Children.Add(search);Grid.SetRow(search,1);
        list.SelectionMode=SelectionMode.Extended;list.Margin=new Thickness(0,0,0,10);root.Children.Add(list);Grid.SetRow(list,2);
        status.Text="正在查找开始菜单和桌面的快捷方式…";status.TextWrapping=TextWrapping.Wrap;status.Margin=new Thickness(0,0,0,10);root.Children.Add(status);Grid.SetRow(status,3);
        var actions=new WrapPanel();root.Children.Add(actions);Grid.SetRow(actions,4);
        AddButton(actions,"添加选中",delegate{AddSelected();});AddButton(actions,"选择文件…",delegate{ChooseFiles();});AddButton(actions,"添加文件夹…",delegate{ChooseFolder();});AddButton(actions,"添加网页…",delegate{ChooseWeb();});
        search.TextChanged+=delegate{Filter();};list.MouseDoubleClick+=delegate{AddSelected();};
        foreach(var app in running.GroupBy(a=>a.Exe).Select(g=>g.First()))if(File.Exists(app.Exe))candidates.Add(new DockPinCandidate {Name=DockPinStore.Key(app.Exe)+" · 正在运行",Path=app.Exe});
        Filter();Loaded+=delegate{search.Focus();ScanShortcuts();};
    }
    static void AddButton(Panel panel,string text,Action action) {var button=new Button {Content=text,Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,7,5)};button.Click+=delegate{action();};panel.Children.Add(button);}
    void Filter(){list.ItemsSource=candidates.Where(c=>c.Name.IndexOf(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)>=0).OrderBy(c=>c.Name).ToArray();}
    void ScanShortcuts() {
        var worker=new Thread(delegate(){var found=new List<DockPinCandidate>();
            foreach(var root in new[]{Environment.GetFolderPath(Environment.SpecialFolder.Programs),Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)}) {
                var folders=new Stack<string>();if(Directory.Exists(root))folders.Push(root);int visited=0;
                while(folders.Count>0&&visited++<500&&found.Count<3000) {string folder=folders.Pop();try {
                    foreach(string file in Directory.GetFiles(folder,"*.lnk"))found.Add(new DockPinCandidate {Name=Path.GetFileNameWithoutExtension(file),Path=file});
                    foreach(string child in Directory.GetDirectories(folder))if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0)folders.Push(child);
                }catch{}}
            }
            if(Dispatcher.HasShutdownStarted)return;Dispatcher.BeginInvoke(new Action(delegate {if(!IsVisible)return;
                foreach(var item in found)if(!candidates.Any(c=>String.Equals(c.Path,item.Path,StringComparison.OrdinalIgnoreCase)))candidates.Add(item);
                Filter();status.Text="选中后点“添加选中”；按住 Ctrl 可多选。也可以选择桌面快捷方式或软件文件。";
            }));
        });worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);worker.Start();
    }
    void AddSelected(){var selected=list.SelectedItems.Cast<DockPinCandidate>().ToArray();if(selected.Length==0){status.Text="先选择一个软件，或点“选择文件…”。";return;}AddFiles(selected.Select(c=>new KeyValuePair<string,string>(c.Path,c.Name.Replace(" · 正在运行",""))));}
    void AddFiles(IEnumerable<KeyValuePair<string,string>> files) {int added=0,duplicate=0;var errors=new List<string>();foreach(var file in files)try {if(store.Add(DockPinStore.FromFile(file.Key,file.Value)))added++;else duplicate++;}catch(Exception ex){errors.Add(ex.Message);}status.Text="已添加 "+added+" 个入口"+(duplicate>0?"，已有 "+duplicate+" 个入口保留":"")+(errors.Count>0?"。"+errors[0]:"。关闭这个窗口即可在程序坞看到。");}
    void ChooseFiles(){var dialog=new Microsoft.Win32.OpenFileDialog {Title="选择要加入程序坞的软件或快捷方式",Filter="软件和快捷方式 (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url",Multiselect=true};if(dialog.ShowDialog(this)==true)AddFiles(dialog.FileNames.Select(path=>new KeyValuePair<string,string>(path,Path.GetFileNameWithoutExtension(path))));}
    void ChooseFolder(){using(var dialog=new Forms.FolderBrowserDialog {Description="选择要固定到程序坞的文件夹",ShowNewFolderButton=false})if(dialog.ShowDialog()==Forms.DialogResult.OK)AddFiles(new[]{new KeyValuePair<string,string>(dialog.SelectedPath,new DirectoryInfo(dialog.SelectedPath).Name)});}
    void ChooseWeb(){var dialog=new Window {Title="添加网页",Width=440,Height=250,ResizeMode=ResizeMode.NoResize,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=FontFamily};
        var body=new StackPanel {Margin=new Thickness(20)};dialog.Content=body;body.Children.Add(new TextBlock {Text="名称"});var name=new TextBox {Margin=new Thickness(0,5,0,12),Padding=new Thickness(5)};body.Children.Add(name);body.Children.Add(new TextBlock {Text="网页地址"});var address=new TextBox {Margin=new Thickness(0,5,0,12),Padding=new Thickness(5)};body.Children.Add(address);
        var button=new Button {Content="加入程序坞",Padding=new Thickness(10,6,10,6),HorizontalAlignment=HorizontalAlignment.Right};body.Children.Add(button);button.Click+=delegate {try{bool added=store.Add(DockPinStore.FromWeb(name.Text,address.Text));status.Text=added?"网页已加入。关闭窗口即可看到。":"这个网页已在程序坞中。";dialog.Close();}catch(Exception ex){MessageBox.Show(dialog,ex.Message,"添加网页");}};dialog.ShowDialog();}
}
