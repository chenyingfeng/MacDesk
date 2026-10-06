using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;

internal static class DockPinChecks {
    internal static void Run(string report) {
        string root=Path.Combine(Path.GetDirectoryName(report),"pin-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string exe=Path.Combine(root,"fixture app.exe");File.WriteAllText(exe,"Not an executable; never launched.");
        var store=new DockPinStore(root);var pin=DockPinStore.FromFile(exe,"测试应用");
        if(!store.Add(pin)||store.Add(DockPinStore.FromFile(exe,"重复")))throw new InvalidOperationException("Duplicate pin");
        if(!DockPinStore.Matches(pin,new MacDockApp {Handle=1,Pid=1,Exe=exe})||DockPinStore.Matches(pin,new MacDockApp {Handle=1,Pid=1,Exe=Path.Combine(root,"other","fixture app.exe")}))throw new InvalidOperationException("Pinned window identity ambiguity");
        var folder=DockPinStore.FromFile(root,"资料文件夹");var web=DockPinStore.FromWeb("网页","https://example.org/path");store.Add(folder);store.Add(web);
        store.Move(web.Id,-1);var reloaded=new DockPinStore(root);
        if(reloaded.Items.Count!=3||reloaded.Items[1].Id!=web.Id||reloaded.Items[0].Name!="测试应用")throw new InvalidOperationException("Pin save/reload/order/Chinese data");
        reloaded.Remove(pin.Id);if(!File.Exists(exe)||new DockPinStore(root).Items.Count!=2)throw new InvalidOperationException("Removing pin changed original app");
        bool denied=false;try{DockPinStore.FromWeb("bad","javascript:alert(1)");}catch(InvalidDataException){denied=true;}if(!denied)throw new InvalidOperationException("Non-web protocol accepted");
        denied=false;string script=Path.Combine(root,"fixture.ps1");File.WriteAllText(script,"never executed");try{DockPinStore.FromFile(script);}catch(InvalidDataException){denied=true;}if(!denied)throw new InvalidOperationException("Unsupported picker target accepted");
        string url=Path.Combine(root,"网页.url");File.WriteAllText(url,"[InternetShortcut]\nURL=https://example.org/\n");if(DockPinStore.FromFile(url).Kind!="web")throw new InvalidOperationException("Internet shortcut import");
        object shell=null,link=null;try {
            var type=Type.GetTypeFromProgID("WScript.Shell");shell=Activator.CreateInstance(type);string path=Path.Combine(root,"快捷方式.lnk");link=type.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});
            link.GetType().InvokeMember("TargetPath",BindingFlags.SetProperty,null,link,new object[]{exe});link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);
            if(!String.Equals(DockPinStore.FromFile(path).Identity,exe,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Shortcut identity not read");
        }finally{if(link!=null&&Marshal.IsComObject(link))Marshal.ReleaseComObject(link);if(shell!=null&&Marshal.IsComObject(shell))Marshal.ReleaseComObject(shell);}
        File.WriteAllText(Path.Combine(root,"dock-pins.json"),"broken");if(String.IsNullOrEmpty(new DockPinStore(root).LoadError))throw new InvalidOperationException("Corrupt pin config overwritten");
        File.WriteAllText(report,"PASS: fixture-only add, deduplication, Chinese persistence, order/reload, remove without deleting original file; exact executable identity matching; shell shortcut identity; URL shortcut import; unsupported scheme/file rejection; corrupt-config preservation. No app launched, no user pin changed.");
    }
}
