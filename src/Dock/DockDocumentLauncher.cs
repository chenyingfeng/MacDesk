using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
internal sealed class DockDocumentLauncher
{
    int busy;long lastPoll;
    internal void Poll(string root) {
        if(DateTime.UtcNow.Ticks-lastPoll<TimeSpan.TicksPerSecond||Interlocked.CompareExchange(ref busy,1,0)!=0)return;
        lastPoll=DateTime.UtcNow.Ticks;
        var worker=new Thread(delegate() {
            string request=Path.Combine(root,"workspace-launch.request.json"),token="";
            try {
                if(!File.Exists(request))return;
                if(new FileInfo(request).Length>32768)throw new InvalidDataException();
                var validated=Decode(File.ReadAllText(request),DateTime.UtcNow,out token);
                // Consume before starting any application, preventing a repeated timer launch.
                File.Delete(request);int opened=0;
                foreach(var target in validated) {
                    Uri uri;bool web=Uri.TryCreate(target,UriKind.Absolute,out uri)&&(uri.Scheme=="http"||uri.Scheme=="https");
                    if(!web&&!File.Exists(target))continue;
                    Process.Start(new ProcessStartInfo(target){UseShellExecute=true});opened++;
                }
                WriteResponse(root,token,"已打开 "+opened+" / "+validated.Count+" 个已关联目标；请刷新并核对窗口。");
            }catch(Exception ex) {
                try {if(File.Exists(request))File.Delete(request);}catch{}
                WriteResponse(root,token,"文档打开未完成："+ex.GetType().Name+"。请通过系统打开后刷新窗口。");
            }finally {Interlocked.Exchange(ref busy,0);}
        });worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);worker.Start();
    }
    internal static List<string> Decode(string text,DateTime now,out string token) {
        var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(text);
        token=Convert.ToString(data["Token"]);Guid identity;
        if(!Guid.TryParseExact(token,"N",out identity))throw new InvalidDataException();
        DateTime created;
        if(!DateTime.TryParse(Convert.ToString(data["CreatedUtc"]),null,System.Globalization.DateTimeStyles.RoundtripKind,out created)
            ||Math.Abs((now-created.ToUniversalTime()).TotalSeconds)>30)throw new InvalidDataException();
        var targets=data["Targets"] as ArrayList;if(targets==null||targets.Count==0||targets.Count>20)throw new InvalidDataException();
        var validated=new List<string>();
        foreach(var raw in targets) {
            var target=raw as string;if(!DocumentTarget.Valid(target))throw new InvalidDataException();
            if(!validated.Contains(target))validated.Add(target);
        }
        return validated;
    }
    internal static void CheckProtocol() {
        var now=DateTime.UtcNow;var json=new JavaScriptSerializer();var token=Guid.NewGuid().ToString("N");string result;
        var body=json.Serialize(new {Token=token,CreatedUtc=now.ToString("o"),Targets=new[]{"https://example.org/","https://example.org/"}});
        if(Decode(body,now,out result).Count!=1||result!=token)throw new InvalidOperationException("Document request deduplication");
        bool refused=false;try {Decode(body,now.AddSeconds(31),out result);}catch(InvalidDataException){refused=true;}
        if(!refused)throw new InvalidOperationException("Stale document launch request accepted");
        refused=false;try {Decode(json.Serialize(new {Token=token,CreatedUtc=now.ToString("o"),Targets=new[]{@"C:\fixtures\unsafe.exe"}}),now,out result);}
        catch(InvalidDataException){refused=true;}
        if(!refused)throw new InvalidOperationException("Executable document request accepted");
    }
    static void WriteResponse(string root,string token,string message) {
        try {
            var path=Path.Combine(root,"workspace-launch.response.json");
            File.WriteAllText(path+".pending",new JavaScriptSerializer().Serialize(new {Token=token,Message=message}));
            if(File.Exists(path))File.Delete(path);File.Move(path+".pending",path);
        }catch{}
    }
}
