using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Diagnostics;
using System.Xml;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Read documented Windows shell/application icons. Never borrow ownership of a window HICON.
internal static class NativeDockIcons {
    internal static string LastError="";
    internal static bool LiveWindow(long handle,int pid) {
        uint actual;IntPtr window=new IntPtr(handle);
        return handle!=0&&pid>0&&IsWindow(window)&&GetWindowThreadProcessId(window,out actual)!=0&&actual==(uint)pid&&IsWindowVisible(window);
    }
    internal const string Codex="shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App";
    internal static string ProcessPath(int pid,string fallback) {
        IntPtr process=OpenProcess(0x1000,false,pid);
        if(process==IntPtr.Zero)return fallback;
        try {var text=new StringBuilder(32768);int size=text.Capacity;
            return QueryFullProcessImageName(process,0,text,ref size)?text.ToString():fallback;
        }finally{CloseHandle(process);}
    }
    internal static ImageSource Read(string parsingName) {
        if(String.IsNullOrWhiteSpace(parsingName))return null;
        if(parsingName==Codex) {var packaged=CodexPackageIcon();if(packaged!=null)return packaged;}
        if(parsingName.StartsWith("shell:AppsFolder",StringComparison.OrdinalIgnoreCase))parsingName="::{4234d49b-0245-4df3-b780-3893943456e1}"+parsingName.Substring(16);
        if(parsingName=="shell:RecycleBinFolder")parsingName="::{645ff040-5081-101b-9f08-00aa002f954e}";
        if(parsingName=="shell:Downloads") {
            IntPtr known=IntPtr.Zero;try{Guid folder=new Guid("374de290-123f-4565-9164-39c4925e467b");
                if(SHGetKnownFolderPath(ref folder,0,IntPtr.Zero,out known)!=0)return null;parsingName=Marshal.PtrToStringUni(known);
            }finally{if(known!=IntPtr.Zero)Marshal.FreeCoTaskMem(known);}
        }
        object item=null;IntPtr bitmap=IntPtr.Zero;
        try {Guid iid=new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            int hr=SHCreateItemFromParsingName(parsingName,IntPtr.Zero,ref iid,out item);if(hr!=0){LastError="Shell item HRESULT="+hr.ToString("x");return null;}
            hr=((IShellItemImageFactory)item).GetImage(new IconSize(128,128),0x4|0x1,out bitmap);if(hr!=0||bitmap==IntPtr.Zero){LastError="Shell icon HRESULT="+hr.ToString("x");return null;}
            var image=Imaging.CreateBitmapSourceFromHBitmap(bitmap,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();return image;
        }catch(Exception ex){LastError=ex.GetType().Name;return null;}finally {if(bitmap!=IntPtr.Zero)DeleteObject(bitmap);if(item!=null&&Marshal.IsComObject(item))Marshal.ReleaseComObject(item);}
    }
    internal static ImageSource PackageIcon(string exe) {
        try {string folder=Path.GetDirectoryName(exe);
            for(int i=0;i<4&&!String.IsNullOrEmpty(folder);i++,folder=Path.GetDirectoryName(folder)) {
                string manifest=Path.Combine(folder,"AppxManifest.xml");if(!File.Exists(manifest))continue;
                var xml=new XmlDocument {XmlResolver=null};xml.Load(manifest);
                var element=xml.SelectSingleNode("//*[local-name()='Application' and @Id='App']/*[local-name()='VisualElements']");
                if(element==null)return null;string relative=element.Attributes["Square44x44Logo"].Value.Replace('/',Path.DirectorySeparatorChar);
                string original=Path.GetFullPath(Path.Combine(folder,relative));
                if(!original.StartsWith(Path.GetFullPath(folder)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return null;
                string stem=Path.Combine(Path.GetDirectoryName(original),Path.GetFileNameWithoutExtension(original));
                string[] candidates={stem+".targetsize-256_altform-unplated.png",stem+".targetsize-96_altform-unplated.png",stem+".scale-200.png",original};
                string path=candidates.FirstOrDefault(File.Exists);if(path==null)return null;
                var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(path);image.EndInit();image.Freeze();
                var color=element.Attributes["BackgroundColor"];
                if(color!=null&&!String.Equals(color.Value,"transparent",StringComparison.OrdinalIgnoreCase)) {
                    var background=(Color)ColorConverter.ConvertFromString(color.Value);
                    var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()) {
                        drawing.DrawRoundedRectangle(new SolidColorBrush(background),null,new Rect(0,0,256,256),52,52);
                        drawing.DrawImage(image,new Rect(30,30,196,196));
                    }
                    var composed=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);composed.Render(visual);composed.Freeze();return composed;
                }return image;
            }
        }catch{}return null;
    }
    static ImageSource CodexPackageIcon() {
        try {foreach(var process in Process.GetProcessesByName("ChatGPT")) {using(process) {
            string path=ProcessPath(process.Id,"");if(path.IndexOf("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0){var image=PackageIcon(path);if(image!=null)return image;}
        }}
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps");
            foreach(string folder in Directory.GetDirectories(root,"OpenAI.Codex_*",SearchOption.TopDirectoryOnly).OrderByDescending(f=>f)) {
                var image=PackageIcon(Path.Combine(folder,"app","ChatGPT.exe"));if(image!=null)return image;
            }
        }catch{}return null;
    }
    internal static string WindowTitle(long handle,int pid) {
        uint actual;GetWindowThreadProcessId(new IntPtr(handle),out actual);if(actual!=(uint)pid)return "";
        var text=new StringBuilder(256);GetWindowText(new IntPtr(handle),text,text.Capacity);return text.ToString();
    }
    internal static ImageSource Executable(string path) {
        IntPtr large=IntPtr.Zero,small=IntPtr.Zero;
        try {if(!String.IsNullOrWhiteSpace(path)&&File.Exists(path)&&SHDefExtractIcon(path,0,0,out large,out small,128u|(16u<<16))==0&&large!=IntPtr.Zero) {
            var image=Imaging.CreateBitmapSourceFromHIcon(large,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());image.Freeze();return image;
        }}catch{}finally {if(large!=IntPtr.Zero)DestroyIcon(large);if(small!=IntPtr.Zero&&small!=large)DestroyIcon(small);}return null;
    }
    internal static ImageSource WindowIcon(long handle,int pid) {
        uint actual;GetWindowThreadProcessId(new IntPtr(handle),out actual);if(actual!=(uint)pid)return null;
        foreach(int size in new[]{1,2,0}) {IntPtr result;
            if(SendMessageTimeout(new IntPtr(handle),0x7f,new IntPtr(size),IntPtr.Zero,0x2,40,out result)==IntPtr.Zero||result==IntPtr.Zero)continue;
            try {var image=Imaging.CreateBitmapSourceFromHIcon(result,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());image.Freeze();return image;}catch{}
        }return null;
    }
    [StructLayout(LayoutKind.Sequential)] struct IconSize {internal int Width,Height;internal IconSize(int w,int h){Width=w;Height=h;}}
    [ComImport,Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory {[PreserveSig]int GetImage(IconSize size,uint flags,out IntPtr bitmap);}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=true)]static extern int SHCreateItemFromParsingName(string name,IntPtr bind,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object item);
    [DllImport("shell32.dll")]static extern int SHGetKnownFolderPath(ref Guid folder,uint flags,IntPtr token,out IntPtr path);
    [DllImport("shell32.dll",EntryPoint="SHDefExtractIconW",CharSet=CharSet.Unicode)]static extern int SHDefExtractIcon(string path,int index,uint flags,out IntPtr large,out IntPtr small,uint size);
    [DllImport("kernel32.dll")]static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder text,ref int size);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr handle,out uint pid);
    [DllImport("user32.dll")]static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr handle,StringBuilder text,int size);
    [DllImport("user32.dll")]static extern IntPtr SendMessageTimeout(IntPtr handle,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
}
// One background STA owns shell extraction; frozen images alone cross back to WPF.
internal sealed class DockIconWorker {
    readonly BlockingCollection<Action> work=new BlockingCollection<Action>();
    internal DockIconWorker() {var thread=new Thread(delegate(){foreach(var action in work.GetConsumingEnumerable()){try{action();}catch{}}});thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();}
    internal void Queue(Action action){if(!work.IsAddingCompleted)try{work.Add(action);}catch(InvalidOperationException){}}
    internal void Stop(){work.CompleteAdding();}
}
