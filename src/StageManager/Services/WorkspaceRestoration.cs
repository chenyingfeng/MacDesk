using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StageManager.Native.Window;
namespace StageManager.Services;
internal sealed record RestoreChoice(WorkspaceMember Member,IWindow[] Candidates,IWindow? Suggested);
internal static class WorkspaceRestoration
{
    internal static RestoreChoice[] Plan(WorkspacePreset preset,IWindow[] available)=>preset.Members.Select(member=> {
        var candidates=available.Where(w=>string.Equals(Path.GetFileName(w.ProcessFileName),member.Process,StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact=candidates.Where(w=>!string.IsNullOrWhiteSpace(member.TitleHint)&&w.Title.Trim()==member.TitleHint.Trim()).ToArray();
        // Never trust an ordinal after a restart; ambiguity is resolved by the user's chooser.
        return new RestoreChoice(member,candidates,exact.Length==1?exact[0]:null);
    }).ToArray();
    private sealed record Bookmark(string Process,string Title,string Target);
    private static string BookmarkPath=>Path.Combine(PortablePreferences.Root,"window-bookmarks.json");
    private static Bookmark[] Bookmarks() {
        try {return (JsonSerializer.Deserialize<Bookmark[]>(File.ReadAllText(BookmarkPath))??[])
            .Where(x=>x!=null&&x.Process.Length<=128&&x.Title.Length<=512&&DocumentTarget.Valid(x.Target)).Take(100).ToArray();}
        catch{return [];}
    }
    internal static string? Target(IWindow w)=>Bookmarks().FirstOrDefault(x=>x.Process==Path.GetFileName(w.ProcessFileName)&&x.Title==w.Title.Trim())?.Target;
    internal static void Associate(IWindow w,string target) {
        if(!DocumentTarget.Valid(target))throw new ArgumentException("请选择普通文档文件，或填写不含账号密码的 http / https 网页地址。");
        var value=new Bookmark(Path.GetFileName(w.ProcessFileName),w.Title.Trim(),target);
        var bookmarks=Bookmarks().Where(x=>x.Process!=value.Process||x.Title!=value.Title).Append(value).TakeLast(100).ToArray();
        File.WriteAllText(BookmarkPath+".pending",JsonSerializer.Serialize(bookmarks,new JsonSerializerOptions {WriteIndented=true}));
        File.Move(BookmarkPath+".pending",BookmarkPath,true);
    }
    internal static async Task<string> OpenTargetsAsync(string[] targets) {
        targets=targets.Where(DocumentTarget.Valid).Distinct().Take(20).ToArray();
        if(targets.Length==0)return "没有需要打开的已关联文档或网页。";
        var processes=System.Diagnostics.Process.GetProcessesByName("PersonalDock-v4");
        bool running=processes.Length>0;foreach(var process in processes)process.Dispose();
        if(!running)
            throw new InvalidOperationException("请先打开MacDesk 快捷中心，再从普通权限的应用入口打开文档。");
        var request=Path.Combine(PortablePreferences.Root,"workspace-launch.request.json");
        if(File.Exists(request))throw new InvalidOperationException("上一次打开请求尚未完成，请稍后重试。");
        var token=Guid.NewGuid().ToString("N");var response=Path.Combine(PortablePreferences.Root,"workspace-launch.response.json");
        File.WriteAllText(request+".pending",JsonSerializer.Serialize(new {Token=token,Targets=targets,CreatedUtc=DateTime.UtcNow.ToString("O")}));
        File.Move(request+".pending",request);
        for(int i=0;i<100;i++) {
            await Task.Delay(150);
            try {
                if(!File.Exists(response))continue;
                using var json=JsonDocument.Parse(File.ReadAllText(response));
                if(json.RootElement.GetProperty("Token").GetString()!=token)continue;
                return json.RootElement.GetProperty("Message").GetString()??"已发送打开请求。";
            }catch(IOException){}catch(JsonException){}
        }
        return "打开请求已发送。请等待应用打开后刷新窗口列表。";
    }
}
