using System;
using System.Linq;
using System.Windows;
namespace StageManager.Services;
internal static class FeatureChecks
{
    internal static void Run()
    {
        var app=new AppWindowSelection();var a=new IntPtr(1);var b=new IntPtr(2);var c=new IntPtr(3);
        if(app.Pick(10,[a,b],IntPtr.Zero,false)!=a || app.Pick(10,[a,b],IntPtr.Zero,true)!=b
            || app.Pick(10,[a,b],IntPtr.Zero,true)!=a || app.Pick(11,[c],IntPtr.Zero,true)!=c
            || app.Pick(10,[b],IntPtr.Zero,true)!=b || app.Pick(10,[],IntPtr.Zero,true)!=IntPtr.Zero)
            throw new InvalidOperationException("App window cycle/closed member/process isolation");
        if(app.Pick(10,[a,b],a,true)!=a || app.Pick(10,[a,b],IntPtr.Zero,false)!=a)
            throw new InvalidOperationException("Explicit Task View member overridden by app cycle");
        var p=new WorkspacePreset("研究",[new WorkspaceMember("msedge.exe",0,-3,4,2,2,false,7)]);
        var valid=WorkspacePresets.Validate([p]);if(valid.Length!=1)throw new InvalidOperationException("Workspace validation");
        var work=new Rect(-1920,-200,1920,1040);var mapped=WorkspacePresets.Map(p.Members[0],work);
        if(!work.Contains(mapped) || mapped.Width!=1920 || mapped.Height!=1040)
            throw new InvalidOperationException("Saved layout outside negative/resized display");
        var invalid=p with {Members=[p.Members[0] with {Width=double.NaN},p.Members[0] with {Process="../QQ.exe"}]};
        if(WorkspacePresets.Validate([invalid]).Length!=0)throw new InvalidOperationException("Invalid saved geometry/process accepted");
        var encoded=System.Text.Json.JsonSerializer.Serialize(p);
        var roundtrip=System.Text.Json.JsonSerializer.Deserialize<WorkspacePreset>(encoded);
        if(roundtrip?.Name!=p.Name || roundtrip.Members[0]!=p.Members[0])throw new InvalidOperationException("Chinese workspace roundtrip");
        foreach(var size in new (double W,double H)[]{(800,600),(6000,400),(80,2000),(20,30)}) {
            var card=Model.SceneModel.MacCardSize(size.W,size.H);
            if(card.Width>196.0001||card.Height>128.0001||card.Width<=0||card.Height<=0
                ||Math.Abs(card.Width/card.Height-size.W/size.H)>.0001)
                throw new InvalidOperationException("Mac card bounds/aspect");
        }
        if(Model.SceneModel.MacCardSize(0,30)!=(0,0))throw new InvalidOperationException("Empty card sizing");
    }
}
