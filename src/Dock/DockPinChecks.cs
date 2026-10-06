using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;

internal static class DockPinChecks {
    internal static void Run(string report) {
        string phase="UTF-8 source encoding";
        try {
        // Escapes contain only ASCII in the source. This detects a wrong source
        // code page before damaged literals become invalid filenames such as
        // question marks on a clean English Windows runner.
        if(!String.Equals("测试应用","\u6D4B\u8BD5\u5E94\u7528",StringComparison.Ordinal)
            ||!String.Equals("快捷方式.lnk","\u5FEB\u6377\u65B9\u5F0F.lnk",StringComparison.Ordinal)
            ||!String.Equals("网页.url","\u7F51\u9875.url",StringComparison.Ordinal))
            throw new InvalidOperationException("Chinese source literals differ from Unicode escapes; compile legacy sources with /codepage:65001.");
        phase="fixture directory";
        string root=Path.Combine(Path.GetDirectoryName(report),"pin-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        phase="add and duplicate";
        string exe=Path.Combine(root,"fixture app.exe");File.WriteAllText(exe,"Not an executable; never launched.");
        var store=new DockPinStore(root);var pin=DockPinStore.FromFile(exe,"测试应用");
        if(!store.Add(pin)||store.Add(DockPinStore.FromFile(exe,"重复")))throw new InvalidOperationException("Duplicate pin");
        phase="window identity";
        if(!DockPinStore.Matches(pin,new MacDockApp {Handle=1,Pid=1,Exe=exe})||DockPinStore.Matches(pin,new MacDockApp {Handle=1,Pid=1,Exe=Path.Combine(root,"other","fixture app.exe")}))throw new InvalidOperationException("Pinned window identity ambiguity");
        phase="persist and reorder";
        var folder=DockPinStore.FromFile(root,"资料文件夹");var web=DockPinStore.FromWeb("网页","https://example.org/path");store.Add(folder);store.Add(web);
        store.Move(web.Id,-1);var reloaded=new DockPinStore(root);
        if(reloaded.Items.Count!=3||reloaded.Items[1].Id!=web.Id||reloaded.Items[0].Name!="测试应用")throw new InvalidOperationException("Pin save/reload/order/Chinese data");
        phase="remove without deleting target";
        reloaded.Remove(pin.Id);if(!File.Exists(exe)||new DockPinStore(root).Items.Count!=2)throw new InvalidOperationException("Removing pin changed original app");
        phase="unsupported targets";
        bool denied=false;try{DockPinStore.FromWeb("bad","javascript:alert(1)");}catch(InvalidDataException){denied=true;}if(!denied)throw new InvalidOperationException("Non-web protocol accepted");
        denied=false;string script=Path.Combine(root,"fixture.ps1");File.WriteAllText(script,"never executed");try{DockPinStore.FromFile(script);}catch(InvalidDataException){denied=true;}if(!denied)throw new InvalidOperationException("Unsupported picker target accepted");
        phase="URL shortcut";
        string url=Path.Combine(root,"网页.url");File.WriteAllText(url,"[InternetShortcut]\nURL=https://example.org/\n");if(DockPinStore.FromFile(url).Kind!="web")throw new InvalidOperationException("Internet shortcut import");
        string shortcutPath=Path.Combine(root,"快捷方式.lnk");
        object shell=null,link=null;try {
            phase="WScript.Shell availability";
            var type=Type.GetTypeFromProgID("WScript.Shell");if(type==null)throw new InvalidOperationException("WScript.Shell COM class is not registered");shell=Activator.CreateInstance(type);
            phase="create Shell shortcut";
            link=type.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{shortcutPath});
            link.GetType().InvokeMember("TargetPath",BindingFlags.SetProperty,null,link,new object[]{exe});link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);
            phase="read persisted Shell shortcut identity";
            if(!String.Equals(DockPinStore.FromFile(shortcutPath).Identity,exe,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Shortcut identity not read");
        }finally{if(link!=null&&Marshal.IsComObject(link))Marshal.ReleaseComObject(link);if(shell!=null&&Marshal.IsComObject(shell))Marshal.ReleaseComObject(shell);}
        phase="corrupt config preservation";
        File.WriteAllText(Path.Combine(root,"dock-pins.json"),"broken");if(String.IsNullOrEmpty(new DockPinStore(root).LoadError))throw new InvalidOperationException("Corrupt pin config overwritten");
        File.WriteAllText(report,"PASS: UTF-8 Chinese literals match Unicode escapes; fixture-only add, deduplication, Chinese persistence, order/reload, remove without deleting original file; exact executable identity matching; Chinese shell shortcut identity; Chinese URL shortcut import; unsupported scheme/file rejection; corrupt-config preservation. No app launched, no user pin changed.");
        }catch(Exception error){throw new InvalidOperationException("Pin fixture phase: "+phase+"; runtime="+Environment.Version+"; OS="+Environment.OSVersion.Version+"; process="+(IntPtr.Size==8?"x64":"x86"),error);}
    }
}
