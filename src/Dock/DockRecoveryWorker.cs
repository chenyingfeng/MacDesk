using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

// Each explicit recovery owns one ordinary-privilege child. A private pipe is
// connected to that exact PID before any command is sent. Closing the job kills
// a stuck provider without leaving a blocked thread in the Dock process.
internal static class DockRecoveryWorker {
    const string Switch="/tray-recovery-worker";
    const int BudgetMilliseconds=4400;
    sealed class Request {
        public string Identity,StageRoot,Fixture;
    }
    static JavaScriptSerializer Serializer(){return new JavaScriptSerializer {MaxJsonLength=32768};}
    internal static DockTrayRecovery.Result Restore(DockPin pin,string stageRoot,Func<bool> wanted) {
        if(pin==null||pin.Kind!="app"||!DockTrayRecovery.Supported(pin.Identity)||!Path.IsPathRooted(pin.Identity))
            return new DockTrayRecovery.Result();
        return Run(new Request {Identity=pin.Identity,StageRoot=stageRoot},wanted,BudgetMilliseconds,null);
    }
    static DockTrayRecovery.Result Failure(string phase,bool submitted) {
        return new DockTrayRecovery.Result {Phase=phase,LastPhase=phase,Route="isolated-tray-worker",ActionUncertain=submitted};
    }
    static async Task<DockTrayRecovery.Result> Exchange(NamedPipeServerStream pipe,Process child,Request request,Action sent,Action accepted) {
        await Task.Factory.FromAsync(pipe.BeginWaitForConnection,pipe.EndWaitForConnection,null).ConfigureAwait(false);
        uint pid;if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out pid)||pid!=(uint)child.Id)throw new InvalidDataException("Unexpected recovery peer");
        var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true);
        var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true);
        sent(); // After this point a timeout cannot establish whether Invoke ran.
        await writer.WriteLineAsync(Serializer().Serialize(request)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        if(await reader.ReadLineAsync().ConfigureAwait(false)!="accepted")throw new InvalidDataException("Missing worker acknowledgement");
        if(accepted!=null)accepted();
        string json=await reader.ReadLineAsync().ConfigureAwait(false);
        if(String.IsNullOrEmpty(json)||json.Length>32768)throw new InvalidDataException("Invalid recovery reply");
        var reply=Serializer().Deserialize<DockTrayRecovery.Result>(json);
        if(reply==null)throw new InvalidDataException("Empty recovery reply");
        return reply;
    }
    static DockTrayRecovery.Result Run(Request request,Func<bool> wanted,int budget,Action<int> started,Action accepted=null) {
        var clock=Stopwatch.StartNew();int submitted=0;
        if(!DockTrayRecovery.Wanted(wanted))return Failure("worker-cancelled",false);
        string name="MacDesk.Recovery."+Guid.NewGuid().ToString("N");
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
        Process child=null;OwnedJob job=null;NamedPipeServerStream pipe=null;
        try {
            pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security);
            job=new OwnedJob();
            string exe=Assembly.GetExecutingAssembly().Location;
            child=Process.Start(new ProcessStartInfo(exe,Switch+" "+name+" "+Process.GetCurrentProcess().Id) {
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(exe)
            });
            if(child==null)throw new InvalidOperationException("Recovery child did not start");
            job.Assign(child);
            if(started!=null)started(child.Id);
            Task<DockTrayRecovery.Result> exchange=Exchange(pipe,child,request,delegate {Interlocked.Exchange(ref submitted,1);},accepted);
            // Observe late pipe disposal failures even after the caller returns.
            exchange.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            while(!exchange.IsCompleted) {
                if(!DockTrayRecovery.Wanted(wanted))return Failure("worker-cancelled",Interlocked.CompareExchange(ref submitted,0,0)!=0);
                if(clock.ElapsedMilliseconds>=budget)return Failure("worker-timeout",Interlocked.CompareExchange(ref submitted,0,0)!=0);
                // A child can exit after flushing a valid reply but before the
                // asynchronous pipe reader resumes. Consume the reply/EOF first.
                Thread.Sleep(25);
            }
            if(!DockTrayRecovery.Wanted(wanted))return Failure("worker-cancelled",Interlocked.CompareExchange(ref submitted,0,0)!=0);
            if(clock.ElapsedMilliseconds>=budget)return Failure("worker-timeout",Interlocked.CompareExchange(ref submitted,0,0)!=0);
            var result=exchange.GetAwaiter().GetResult();result.ElapsedMilliseconds=clock.ElapsedMilliseconds;
            if(result.Shown&&(!DockTrayRecovery.SameWindow(new IntPtr(result.Handle),result.Pid)))return Failure("worker-stale-window",true);
            return result;
        }catch(Exception error){var result=Failure("worker-error",Interlocked.CompareExchange(ref submitted,0,0)!=0);result.ErrorType=error.GetType().Name;return result;}
        finally {
            // Job first: provider calls cannot outlive this request or the parent.
            if(job!=null)job.Dispose();
            if(pipe!=null)pipe.Dispose();
            if(child!=null){try{if(!child.HasExited)child.Kill();}catch{}try{child.WaitForExit(500);}catch{}child.Dispose();}
        }
    }
    internal static bool TryRun(string[] args) {
        if(args.Length>0&&args[0]=="/check-worker-parent-exit") {
            if(args.Length==2)Run(new Request {Fixture="hang"},()=>true,5000,p=>File.WriteAllText(args[1],p.ToString()),()=>Environment.Exit(0));
            return true;
        }
        if(args.Length==0||args[0]!=Switch)return false;
        Environment.ExitCode=2;
        int parent;
        if(args.Length!=3||!args[1].StartsWith("MacDesk.Recovery.",StringComparison.Ordinal)
            ||args[1].Length!=49||!Int32.TryParse(args[2],out parent)||parent<=0)return true;
        Guid token;if(!Guid.TryParseExact(args[1].Substring(17),"N",out token))return true;
        try {
            using(var pipe=new NamedPipeClientStream(".",args[1],PipeDirection.InOut,PipeOptions.Asynchronous)) {
                pipe.Connect(2000);uint server;
                if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out server)||server!=(uint)parent)return true;
                var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true);
                var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true};
                string line=reader.ReadLine();if(line==null||line.Length>32768)return true;
                var request=Serializer().Deserialize<Request>(line);if(request==null)return true;
                writer.WriteLine("accepted");
                DockTrayRecovery.Result result;
                // Controlled process-boundary fixtures never call a desktop API.
                if(request.Fixture=="hang") {Thread.Sleep(Timeout.Infinite);return true;}
                if(request.Fixture=="exit")return true;
                if(request.Fixture=="reply")result=new DockTrayRecovery.Result {Route="owned-fixture",Phase="fixture-complete"};
                else if(request.Fixture!=null)return true;
                else {
                    if(!Path.IsPathRooted(request.Identity)||!DockTrayRecovery.Supported(request.Identity))return true;
                    result=DockTrayRecovery.Restore(new DockPin {Kind="app",Identity=request.Identity},request.StageRoot);
                }
                writer.WriteLine(Serializer().Serialize(result));Environment.ExitCode=0;
            }
        }catch { }
        return true;
    }
    internal static void Check(string output) {
        int childPid=0,entered=0;var watch=Stopwatch.StartNew();
        var timed=Run(new Request {Fixture="hang"},()=>true,1800,p=>childPid=p,()=>Interlocked.Exchange(ref entered,1));
        if(entered!=1||!timed.ActionUncertain||timed.Phase!="worker-timeout"||watch.ElapsedMilliseconds>4500||!Gone(childPid))throw new InvalidOperationException("Blocked provider was not bounded: "+timed.Phase+"/"+timed.ErrorType);
        watch.Restart();
        long acceptedAt=-1;
        var cancelled=Run(new Request {Fixture="hang"},()=>Interlocked.Read(ref acceptedAt)<0||watch.ElapsedMilliseconds<Interlocked.Read(ref acceptedAt)+150,2500,p=>childPid=p,()=>Interlocked.Exchange(ref acceptedAt,watch.ElapsedMilliseconds));
        if(cancelled.Phase!="worker-cancelled"||watch.ElapsedMilliseconds>2500||!Gone(childPid))throw new InvalidOperationException("Cancellation left a worker alive");
        var exited=Run(new Request {Fixture="exit"},()=>true,2500,p=>childPid=p);
        if(exited.Shown||!Gone(childPid))throw new InvalidOperationException("Exited provider accepted or leaked");
        for(int i=0;i<3;i++) {
            var recovered=Run(new Request {Fixture="reply"},()=>true,3000,p=>childPid=p);
            if(recovered.Route!="owned-fixture"||recovered.Phase!="fixture-complete"||!Gone(childPid))throw new InvalidOperationException("A prior failure blocked a fresh request: "+recovered.Phase+"/"+recovered.ErrorType);
        }
        var refused=Run(new Request {Fixture="reply"},()=>false,1000,p=>{throw new InvalidOperationException("Canceled work started");});
        if(refused.Phase!="worker-cancelled")throw new InvalidOperationException("Early cancellation failed");
        string orphan=output+".owned-child";
        using(var parent=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"/check-worker-parent-exit \""+orphan+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})) {
            if(!parent.WaitForExit(6000)){parent.Kill();throw new InvalidOperationException("Owned parent fixture failed to exit");}
        }
        int orphanPid=Int32.Parse(File.ReadAllText(orphan));watch.Restart();while(!Gone(orphanPid)&&watch.ElapsedMilliseconds<1500)Thread.Sleep(25);
        if(!Gone(orphanPid))throw new InvalidOperationException("Parent exit orphaned provider");File.Delete(orphan);
        File.WriteAllText(output,"PASS: actual owned child-process and private-pipe exchanges; acknowledged blocked provider times out with uncertain action; cancellation and early exit clean children; fresh requests succeed after failure; pre-cancelled request never launches; abrupt parent exit kills provider through its job. No existing application or taskbar invoked.");
    }
    static bool Gone(int pid){try{using(var p=Process.GetProcessById(pid))return p.HasExited;}catch(ArgumentException){return true;}}
    sealed class OwnedJob:IDisposable {
        SafeJob handle;
        internal OwnedJob(){
            handle=CreateJobObject(IntPtr.Zero,null);if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception();
            var info=new JobLimits();info.Basic.Flags=0x2000;
            if(!SetInformationJobObject(handle,9,ref info,(uint)Marshal.SizeOf(typeof(JobLimits)))){handle.Dispose();throw new System.ComponentModel.Win32Exception();}
        }
        internal void Assign(Process process){if(!AssignProcessToJobObject(handle,process.Handle))throw new System.ComponentModel.Win32Exception();}
        public void Dispose(){if(handle!=null)handle.Dispose();}
    }
    sealed class SafeJob:SafeHandleZeroOrMinusOneIsInvalid {
        private SafeJob():base(true){}
        protected override bool ReleaseHandle(){return CloseHandle(handle);}
    }
    [StructLayout(LayoutKind.Sequential)]struct BasicLimits {internal long ProcessTime,JobTime;internal uint Flags;internal UIntPtr Minimum,Maximum;internal uint ActiveProcesses;internal UIntPtr Affinity;internal uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]struct IoCounters {internal ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]struct JobLimits {internal BasicLimits Basic;internal IoCounters Io;internal UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]static extern SafeJob CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(SafeJob job,int kind,ref JobLimits limits,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(SafeJob job,IntPtr process);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
}
