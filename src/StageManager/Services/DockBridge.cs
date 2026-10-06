using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using StageManager.Native.Window;
namespace StageManager.Services;
internal static class DockBridge
{
    private static long lastExport;
    internal static bool ValidRequest(string token,DateTime created,long handle,int pid,DateTime now)=>
        Guid.TryParseExact(token,"N",out _)&&handle!=0&&pid>0&&Math.Abs((now-created.ToUniversalTime()).TotalSeconds)<=5;
    internal static void Poll(MainWindow main) {
        if(main.SceneManager==null)return;
        var request=Path.Combine(PortablePreferences.Root,"dock-activation.request.json");
        try {
            if(File.Exists(request)) {
                if(new FileInfo(request).Length>2048){File.Delete(request);return;}
                using var json=JsonDocument.Parse(File.ReadAllText(request));File.Delete(request);
                var r=json.RootElement;var token=r.GetProperty("Token").GetString()??"";
                var created=r.GetProperty("CreatedUtc").GetDateTime();long handle=r.GetProperty("Handle").GetInt64();int pid=r.GetProperty("Pid").GetInt32();
                if(ValidRequest(token,created,handle,pid,DateTime.UtcNow)) {
                    WorkspaceEnvironment.Refresh(main.Handle);
                    _=main.SceneManager.ActivateDockWindowAsync(new IntPtr(handle),pid);
                }
            }
        }catch(Exception ex) when(ex is IOException||ex is UnauthorizedAccessException||ex is JsonException||ex is InvalidOperationException||ex is FormatException||ex is System.Collections.Generic.KeyNotFoundException){}
        if(Environment.TickCount64-lastExport<1000)return;lastExport=Environment.TickCount64;
        try {
            var windows=main.SceneManager.FeatureWindows.Where(w=>WorkspaceEnvironment.IsCurrent(w.Handle)).ToArray();
            var screen=System.Windows.Forms.Screen.PrimaryScreen!;
            var payload=new {UpdatedUtc=DateTime.UtcNow,StagePid=Environment.ProcessId,
                Fullscreen=WorkspaceEnvironment.ForegroundFullscreen(screen.DeviceName),
                Apps=windows.Select(w=>new {Handle=w.Handle.ToInt64(),Pid=w.ProcessId,Exe=w.ProcessFileName,
                    Active=w.IsFocused,OnStage=main.SceneManager.IsCurrentScene(main.SceneManager.FindSceneForWindow(w))}).ToArray()};
            var path=Path.Combine(PortablePreferences.Root,"stage-apps.json");
            File.WriteAllText(path+".pending",JsonSerializer.Serialize(payload));File.Move(path+".pending",path,true);
        }catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
