using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
namespace StageManager.Services;
internal sealed record WorkspaceMember(string Process,int Ordinal,double X,double Y,double Width,double Height,bool Maximized,int ZOrder=0,
    string? TitleHint=null,string? OpenTarget=null,string? Monitor=null);
internal sealed record WorkspacePreset(string Name,WorkspaceMember[] Members);
internal static class WorkspacePresets
{
    internal static string FilePath=>Path.Combine(PortablePreferences.Root,"task-groups.json");
    internal static WorkspacePreset[] Load()
    {
        try {return Validate(JsonSerializer.Deserialize<WorkspacePreset[]>(File.ReadAllText(FilePath))??[]);}
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) {return [];}
    }
    internal static WorkspacePreset[] Validate(WorkspacePreset[] entries)=>entries.Take(40)
        .Where(p=>p!=null && !string.IsNullOrWhiteSpace(p.Name) && p.Name.Length<=60 && p.Members!=null)
        .Select(p=>p with {Members=p.Members.Take(50).Where(m=>m!=null && !string.IsNullOrWhiteSpace(m.Process)
            && m.Process==Path.GetFileName(m.Process) && m.Ordinal>=0 && m.Ordinal<50
            && double.IsFinite(m.X) && double.IsFinite(m.Y) && double.IsFinite(m.Width) && double.IsFinite(m.Height)
            && m.Width>0 && m.Height>0 && m.Width<=4 && m.Height<=4
            && (m.TitleHint==null||m.TitleHint.Length<=512) && (m.Monitor==null||m.Monitor.Length<=128)
            && (m.OpenTarget==null||DocumentTarget.Valid(m.OpenTarget))).ToArray()})
        .Where(p=>p.Members.Length>0).ToArray();
    internal static void Save(WorkspacePreset[] entries)
    {
        var pending=FilePath+".pending";
        File.WriteAllText(pending,JsonSerializer.Serialize(Validate(entries),new JsonSerializerOptions {WriteIndented=true}));
        File.Move(pending,FilePath,true);
    }
    internal static Rect Map(WorkspaceMember m,Rect work)
    {
        double w=Math.Clamp(m.Width*work.Width,Math.Min(240,work.Width),work.Width);
        double h=Math.Clamp(m.Height*work.Height,Math.Min(160,work.Height),work.Height);
        return new Rect(Math.Clamp(work.X+m.X*work.Width,work.Left,work.Right-w),
            Math.Clamp(work.Y+m.Y*work.Height,work.Top,work.Bottom-h),w,h);
    }
}
