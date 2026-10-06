using System;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

internal static class DockRuntime {
    internal static bool Recoverable(Exception error) {
        return error is IOException||error is UnauthorizedAccessException||error is ArgumentException
            ||error is InvalidOperationException||error is Win32Exception||error is COMException;
    }
    internal static void Record(string root,string operation,Exception error) {
        try {
            string file=Path.Combine(root,"dock-error.log");
            if(File.Exists(file)&&new FileInfo(file).Length>131072)File.WriteAllText(file,"");
            File.AppendAllText(file,DateTime.UtcNow.ToString("o")+" "+operation+" "+error.GetType().FullName+"\n"+(error.StackTrace??"")+"\n");
        }catch{}
    }
    internal static void AtomicText(string file,string text) {
        string pending=file+".pending";File.WriteAllText(pending,text);
        if(File.Exists(file))File.Replace(pending,file,null);else File.Move(pending,file);
    }
    internal static bool FreshPeek(string text,DateTime now) {
        if(!text.StartsWith("State=peek",StringComparison.Ordinal))return false;
        foreach(string line in text.Split('\n'))if(line.StartsWith("Utc=",StringComparison.Ordinal)) {
            DateTime updated;return DateTime.TryParse(line.Substring(4).Trim(),null,System.Globalization.DateTimeStyles.RoundtripKind,out updated)
                &&Math.Abs((now-updated.ToUniversalTime()).TotalSeconds)<=4;
        }return false;
    }
    internal static void Configure(Application app,Window main,Action recover,Action<string> notice,string root) {
        // Secondary windows (picker, reminders, recovery notes) never own process lifetime.
        app.MainWindow=main;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        main.Closed+=delegate{app.Shutdown();};
        long recent=0;int failures=0;
        app.DispatcherUnhandledException+=delegate(object sender,DispatcherUnhandledExceptionEventArgs e) {
            Record(root,"dispatcher",e.Exception);
            if(!Recoverable(e.Exception))return;
            long now=DateTime.UtcNow.Ticks;
            if(now-recent>30*TimeSpan.TicksPerSecond){recent=now;failures=0;}
            if(++failures>3)return;
            e.Handled=true;
            app.Dispatcher.BeginInvoke(new Action(delegate {try{recover();notice("桌面入口已重新就绪。");}catch(Exception error){Record(root,"repair",error);}}));
        };
        AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e) {
            var error=e.ExceptionObject as Exception;if(error!=null)Record(root,"unhandled",error);
        };
    }
    internal static void Check(string report) {
        var now=DateTime.UtcNow;
        if(!FreshPeek("State=peek\nUtc="+now.ToString("o"),now)||FreshPeek("State=peek\nUtc="+now.AddSeconds(-5).ToString("o"),now)
            ||FreshPeek("State=peek",now)||FreshPeek("State=hidden\nUtc="+now.ToString("o"),now))throw new InvalidOperationException("Stale taskbar peek retained");
        if(!Recoverable(new IOException())||!Recoverable(new COMException())||Recoverable(new OutOfMemoryException())||Recoverable(new AccessViolationException()))throw new InvalidOperationException("Fatal error recovery policy");
        var app=new Application();var main=new Window();Configure(app,main,delegate{},delegate{},Path.GetDirectoryName(report));
        var secondary=new Window();secondary.Close();
        if(app.MainWindow!=main||app.ShutdownMode!=ShutdownMode.OnExplicitShutdown||app.Dispatcher.HasShutdownStarted)throw new InvalidOperationException("Secondary window owned application shutdown");
        File.WriteAllText(report,"PASS: own unshown main/secondary WPF lifetime remains explicit; stale/missing taskbar peek expires; known recoverable exception boundaries exclude fatal memory/access errors. No existing application was controlled.");
    }
}
