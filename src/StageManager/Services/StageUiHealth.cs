using System;
using System.IO;
using System.Threading;
namespace StageManager.Services;
// A background metadata heartbeat diagnoses the UI loop without querying or moving apps.
internal sealed class StageUiHealth : IDisposable
{
    internal static string PathName=>Path.Combine(AppContext.BaseDirectory,"stage-health.ini");
    private readonly Timer timer;
    private long uiTicks=DateTime.UtcNow.Ticks;
    private int writing,disposed,maintenanceErrors;
    private string lastFault="";
    internal StageUiHealth(){timer=new Timer(_=>Write(),null,0,1000);}
    internal void Pulse()=>Interlocked.Exchange(ref uiTicks,DateTime.UtcNow.Ticks);
    internal void MaintenanceFault(string component,Exception error) {
        Interlocked.Increment(ref maintenanceErrors);
        Volatile.Write(ref lastFault,component+":"+error.GetType().Name);
    }
    private void Write() {
        if(Interlocked.Exchange(ref writing,1)!=0)return;
        try {
            var ui=new DateTime(Interlocked.Read(ref uiTicks),DateTimeKind.Utc);
            var now=DateTime.UtcNow;var state=Volatile.Read(ref disposed)!=0?"stopped":now-ui>TimeSpan.FromSeconds(12)?"ui-delayed":"responsive";
            var text=$"Version={UpdateService.GetCurrentVersion()}\nPid={Environment.ProcessId}\nState={state}\nUiUtc={ui:O}\nMaintenanceErrors={Volatile.Read(ref maintenanceErrors)}\nLastFault={Volatile.Read(ref lastFault)}\nUtc={now:O}\n";
            File.WriteAllText(PathName+".pending",text);File.Move(PathName+".pending",PathName,true);
        }catch(IOException){}catch(UnauthorizedAccessException){}
        finally{Volatile.Write(ref writing,0);}
    }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;timer.Dispose();Write();}
}
