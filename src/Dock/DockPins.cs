using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;

internal sealed class DockPin {
    public string Id,Name,Kind,Target,Identity;
    public override string ToString(){return Name;}
}
internal sealed class DockPinStore {
    readonly string path;
    internal readonly List<DockPin> Items=new List<DockPin>();
    internal string LoadError="";
    internal DockPinStore(string root) {
        path=Path.Combine(root,"dock-pins.json");
        try {if(File.Exists(path)) {
            if(new FileInfo(path).Length>524288)throw new InvalidDataException();
            var values=new JavaScriptSerializer().Deserialize<List<DockPin>>(File.ReadAllText(path));
            if(values==null||values.Count>80)throw new InvalidDataException();
            foreach(var pin in values)if(Valid(pin)&&!Items.Any(p=>SameTarget(p,pin)))Items.Add(pin);
        }}catch{LoadError="原有固定入口配置无法读取。请先保留原文件，再重试。";}
    }
    internal static bool Web(string value) {Uri url;return Uri.TryCreate(value,UriKind.Absolute,out url)&&(url.Scheme=="https"||url.Scheme=="http");}
    internal static bool Valid(DockPin pin) {
        Guid id;if(pin==null||!Guid.TryParseExact(pin.Id,"N",out id)||String.IsNullOrWhiteSpace(pin.Name)||pin.Name.Length>100||String.IsNullOrWhiteSpace(pin.Target)||pin.Target.Length>32768)return false;
        if(pin.Kind=="web")return Web(pin.Target);
        if((pin.Kind!="app"&&pin.Kind!="folder")||!Path.IsPathRooted(pin.Target))return false;
        string ext=Path.GetExtension(pin.Target).ToLowerInvariant();return pin.Kind=="folder"||ext==".exe"||ext==".lnk";
    }
    internal static bool SameTarget(DockPin a,DockPin b) {
        return a.Kind==b.Kind&&String.Equals(a.Target,b.Target,a.Kind=="web"?StringComparison.Ordinal:StringComparison.OrdinalIgnoreCase);
    }
    internal bool Add(DockPin pin) {
        if(!Valid(pin))throw new InvalidDataException("这个入口暂时无法添加。");
        if(Items.Any(p=>SameTarget(p,pin)))return false;
        if(Items.Count>=80)throw new InvalidOperationException("最多可以固定 80 个入口。");
        Items.Add(pin);try{Save();}catch{Items.Remove(pin);throw;}return true;
    }
    internal void Remove(string id) {var pin=Items.FirstOrDefault(p=>p.Id==id);if(pin==null)return;int index=Items.IndexOf(pin);Items.Remove(pin);try{Save();}catch{Items.Insert(index,pin);throw;}}
    internal void Move(string id,int direction) {
        int index=Items.FindIndex(p=>p.Id==id),next=index+direction;if(index<0||next<0||next>=Items.Count)return;
        var pin=Items[index];Items.RemoveAt(index);Items.Insert(next,pin);try{Save();}catch{Items.RemoveAt(next);Items.Insert(index,pin);throw;}
    }
    void Save() {
        if(!String.IsNullOrEmpty(LoadError))throw new InvalidDataException(LoadError);
        string pending=path+".pending";File.WriteAllText(pending,new JavaScriptSerializer().Serialize(Items));
        if(File.Exists(path))File.Replace(pending,path,path+".bak");else File.Move(pending,path);
    }
    internal static DockPin FromFile(string value,string name=null) {
        string target=Path.GetFullPath(value);if(!File.Exists(target)&&!Directory.Exists(target))throw new FileNotFoundException("入口已不存在，请重新选择。");
        string ext=Path.GetExtension(target).ToLowerInvariant();string kind=Directory.Exists(target)?"folder":"app";
        string identity=ext==".exe"?target:ext==".lnk"?ShortcutTarget(target):"";
        if(ext==".url") {
            string address=File.ReadAllLines(target).FirstOrDefault(line=>line.StartsWith("URL=",StringComparison.OrdinalIgnoreCase));
            if(address==null||!Web(address.Substring(4).Trim()))throw new InvalidDataException("这个网页快捷方式没有有效的 http / https 地址。");
            return FromWeb(name??Path.GetFileNameWithoutExtension(target),address.Substring(4).Trim());
        }
        var pin=new DockPin {Id=Guid.NewGuid().ToString("N"),Name=name??(kind=="folder"?new DirectoryInfo(target).Name:Path.GetFileNameWithoutExtension(target)),Kind=kind,Target=target,Identity=identity};
        if(!Valid(pin))throw new InvalidDataException("请选择软件（exe）、快捷方式（lnk / url）或文件夹。");return pin;
    }
    internal static DockPin FromWeb(string name,string target) {
        var pin=new DockPin {Id=Guid.NewGuid().ToString("N"),Name=name.Trim(),Kind="web",Target=target.Trim(),Identity=""};
        if(!Valid(pin))throw new InvalidDataException("请填写名称和有效的 http / https 网页地址。");return pin;
    }
    // Read only the shortcut's target; do not resolve, rewrite or launch it here.
    internal static string ShortcutTarget(string path) {
        try {
            string value=DockShellLink.ReadTarget(path);
            return !String.IsNullOrWhiteSpace(value)&&Path.IsPathRooted(value)&&Path.GetExtension(value).Equals(".exe",StringComparison.OrdinalIgnoreCase)?Path.GetFullPath(value):"";
        }catch{return "";}
    }
    internal static string Key(string exe) {return (exe??"").IndexOf("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0?"codex":Path.GetFileNameWithoutExtension(exe??"").ToLowerInvariant();}
    internal static bool Matches(DockPin pin,MacDockApp app) {
        if(pin.Kind!="app"||String.IsNullOrWhiteSpace(pin.Identity))return false;
        return String.Equals(pin.Identity,app.Exe,StringComparison.OrdinalIgnoreCase)
            ||!Path.IsPathRooted(app.Exe)&&Key(pin.Identity)==app.Key;
    }
}
