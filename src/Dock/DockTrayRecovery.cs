using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Automation;
using Accessibility;
using Forms=System.Windows.Forms;

// Used only by an explicit messenger pin click. The notification icon's existing
// default action owns the app's wake-up, focus and modal-window bookkeeping.
// No hidden messenger HWND is shown, enabled, styled, moved or relaunched here.
internal static class DockTrayRecovery {
    internal sealed class Result {
        internal bool Shown,Foreground,WakeRequested,ActionUncertain;
        internal long Handle,ElapsedMilliseconds;
        internal int Pid,CandidateCount;
        internal string Route="none",Phase="none",LastPhase="none",ErrorType="None",ErrorHResult="0x00000000",ErrorOperation="none",RejectReason="none";
    }
    internal static bool Supported(string identity){string key=DockPinStore.Key(identity);return key=="wechat"||key=="weixin"||key=="qq";}
    internal static bool MainShape(string cls,string caption,double width,double height) {
        return (cls=="WeChatMainWndForPC"||Regex.IsMatch(cls??"",@"\AQt[0-9]+QWindowIcon\z"))
            && (caption=="微信"||caption=="WeChat"||caption=="Weixin")
            && width>=480&&height>=300&&width>=height*.95;
    }
    internal static bool QqMainShape(string cls,string caption,double width,double height) {
        bool login=width>=300&&width<=360&&height>=420&&height<=520;
        return cls=="Chrome_WidgetWin_1"&&caption=="QQ"&&width>=300&&height>=200&&!login;
    }
    internal static bool SameWindow(IntPtr h,int pid){uint actual;return pid>0&&IsWindow(h)&&GetWindowThreadProcessId(h,out actual)!=0&&actual==(uint)pid;}
    internal static Result Restore(DockPin pin){return Restore(pin,null,null);}
    internal static Result Restore(DockPin pin,string stageRoot){return Restore(pin,stageRoot,null);}
    internal static Result Restore(DockPin pin,string stageRoot,Func<bool> stillWanted) {
        var result=new Result();
        if(pin.Kind!="app"||!Supported(pin.Identity)||!Path.IsPathRooted(pin.Identity)||!Wanted(stillWanted))return result;
        string app=DockPinStore.Key(pin.Identity)=="qq"?"qq":"wechat";
        var request=new RequestScope(stillWanted,app,result);
        result.Route=app+"-tray-unavailable";
        try {
            Phase(request,"process-check");
            request.Operation="process-path-check";
            if(!ExactProcessPresent(pin.Identity)||!RequestCurrent(request))return result;
            if(!String.IsNullOrEmpty(stageRoot)) {
                Phase(request,"taskbar-wait");
                request.Operation="taskbar-request-write";
                File.WriteAllText(Path.Combine(stageRoot,"taskbar-peek.request"),"1");
                // Wait for the guardian to expose the real notification area,
                // without spending the entire request budget polling UIA.
                var visibleDeadline=request.Clock.ElapsedMilliseconds+500;
                request.Operation="shell-visibility-check";
                while(RequestCurrent(request)&&request.Clock.ElapsedMilliseconds<visibleDeadline&&!VisibleShellTray())Thread.Sleep(40);
            }
            if(!RequestCurrent(request))return result;
            var choices=FindButtons(app,false,request);
            if(!RequestCurrent(request)||choices==null)return result;
            if(choices.Count==0) {
                var chevrons=FindButtons(app,true,request);
                if(chevrons==null||!RequestCurrent(request))return result;
                if(chevrons.Count==1&&Act(chevrons[0],app,true,pin.Identity,request)) {
                    Phase(request,"overflow-wait");
                    // Hidden overflow icons do not exist in the accessibility
                    // tree until the shell flyout has actually been opened.
                    var overflowDeadline=request.Clock.ElapsedMilliseconds+800;
                    do {
                        Thread.Sleep(60);
                        choices=FindButtons(app,false,request);
                        if(choices==null||choices.Count>0)break;
                    }while(RequestCurrent(request)&&request.Clock.ElapsedMilliseconds<overflowDeadline);
                }
            }
            if(choices==null||!RequestCurrent(request))return result;
            result.CandidateCount=choices.Count;
            if(choices.Count!=1){Phase(request,choices.Count==0?"icon-not-found":"ambiguous-icon");return result;}
            if(!Act(choices[0],app,false,pin.Identity,request))return result;
            Phase(request,"main-wait");
            var mainDeadline=request.Clock.ElapsedMilliseconds+1200;
            do {
                var main=new List<KeyValuePair<IntPtr,int>>();
                request.Operation="native-main-enumerate";
                EnumWindows(delegate(IntPtr h,IntPtr ignored){
                    if(!RequestCurrent(request))return false;uint pid;GetWindowThreadProcessId(h,out pid);
                    if(pid>0&&EligibleShown(h,(int)pid,pin.Identity,app))main.Add(new KeyValuePair<IntPtr,int>(h,(int)pid));return true;
                },IntPtr.Zero);
                if(main.Count==1&&RequestCurrent(request)) {
                    var target=main[0];
                    if(EligibleShown(target.Key,target.Value,pin.Identity,app)&&RequestCurrent(request)) {
                        result.Shown=true;result.Handle=target.Key.ToInt64();result.Pid=target.Value;
                        result.Foreground=GetForegroundWindow()==target.Key;
                        Phase(request,"shown");return result;
                    }
                }
                Thread.Sleep(60);
            }while(RequestCurrent(request)&&request.Clock.ElapsedMilliseconds<mainDeadline);
            Phase(request,"wake-submitted");
        }catch(Exception error){Error(request,error);}
        finally {
            result.ElapsedMilliseconds=request.Clock.ElapsedMilliseconds;
            if(!Wanted(stillWanted))result.Phase="cancelled";
            else if(!RequestAgeCurrent(result.ElapsedMilliseconds))result.Phase="expired";
            WriteTrace(request);
            foreach(object com in request.ComObjects)try{if(Marshal.IsComObject(com))Marshal.ReleaseComObject(com);}catch{}
        }
        return result;
    }
    internal static bool QqTrayLabel(string name){return TokenLabel(name,"QQ")||TokenLabel(name,"腾讯QQ");}
    internal static bool WechatTrayLabel(string name){return TokenLabel(name,"微信")||TokenLabel(name,"WeChat")||TokenLabel(name,"Weixin");}
    internal static bool TokenLabel(string name,string token) {
        name=(name??"").Trim();if(name==token)return true;
        if(!name.StartsWith(token,StringComparison.Ordinal)||name.Length<=token.Length)return false;
        char boundary=name[token.Length];
        return boundary==':'||boundary=='：'||boundary=='\r'||boundary=='\n'||boundary=='('||boundary=='（'||boundary=='['||boundary=='【';
    }
    internal static bool ChevronLabel(string name) {
        return name=="显示隐藏的图标"||name=="显示隐藏图标"||name=="Show hidden icons"||name=="Notification Chevron"||name=="NotificationChevron";
    }
    static bool LabelMatches(string name,string app,bool chevron){return chevron?ChevronLabel(name):(app=="qq"?QqTrayLabel(name):WechatTrayLabel(name));}
    static bool ExactProcessPresent(string identity) {
        if(!Path.IsPathRooted(identity)||!Supported(identity))return false;
        var processes=Process.GetProcessesByName(Path.GetFileNameWithoutExtension(identity));bool found=false;
        foreach(var process in processes) {
            try{if(String.Equals(NativeDockIcons.ProcessPath(process.Id,""),identity,StringComparison.OrdinalIgnoreCase))found=true;}
            catch{}finally{process.Dispose();}
        }
        return found;
    }
    internal static bool RequestAgeCurrent(long ageMilliseconds){return ageMilliseconds>=0&&ageMilliseconds<5000;}
    internal static bool Wanted(Func<bool> stillWanted){try{return stillWanted==null||stillWanted();}catch{return false;}}
    sealed class RequestScope {
        internal readonly Stopwatch Clock=Stopwatch.StartNew();internal readonly Func<bool> StillWanted;
        internal readonly string App,Id=Guid.NewGuid().ToString("N");internal readonly Result Result;
        internal readonly HashSet<object> ComObjects=new HashSet<object>();
        internal int NativeSurfaces,MsaaNodes,MsaaEmptyRoots,MsaaUnavailable;
        internal int UiaNodes,UiaSurfaces,UiaLegacyScopes,UiaShellScopes,UiaXamlScopes,UiaOverflowScopes;
        internal int UiaRootUnknownPid,UiaRootForeignPid,UiaForeignBranches,UiaEmptyRoots,UiaChildren,UiaRawBridges;
        internal int UiaContainers,UiaButtons,UiaLabelMatches,UiaCandidateWrongPid,UiaHiddenMatches,UiaNoInvokeMatches,UiaTaskBranches;
        internal int UiaDuplicateNodes,UiaDepthPrunes,UiaUnresolvedDepth,UiaBudgetExhausted;
        internal int InitialIconPasses,OverflowIconPasses,ChevronPasses,UiaPasses,MsaaPasses,ChevronAttempted,ChevronSubmitted,OverflowObservations,ErrorCount;
        internal readonly HashSet<long> OverflowWindows=new HashSet<long>();
        internal readonly List<string> Events=new List<string>();
        internal string Operation="none",LastChevronApi="none",LastScanApi="none",LastScanTarget="none",LastScanStage="none";
        internal int LastScanCandidates;internal bool LastScanComplete;
        internal bool LiveSnapshotRead,LiveSurfaceValid,LiveProcessValid,LivePidMatches,LiveLabelMatches,LiveEnabled,LiveOffscreen,LiveBoundsValid;
        internal bool LiveRuntimeMatches,LiveInvokeDeclared,LivePatternAvailable,LiveOnScreen,LiveBoundsChanged;
        internal int LiveRefreshes,LayoutChanges;
        internal RequestScope(Func<bool> wanted,string app,Result result){StillWanted=wanted;App=app;Result=result;}
    }
    static bool RequestCurrent(RequestScope request){return RequestAgeCurrent(request.Clock.ElapsedMilliseconds)&&Wanted(request.StillWanted);}
    static void Phase(RequestScope request,string phase){
        request.Result.Phase=phase;request.Result.LastPhase=phase;
        request.Events.Add(request.Clock.ElapsedMilliseconds+":"+phase);if(request.Events.Count>24)request.Events.RemoveAt(0);WriteTrace(request);
    }
    static void Error(RequestScope request,Exception error){
        request.ErrorCount++;
        request.Result.ErrorType=error.GetType().Name;
        request.Result.ErrorHResult="0x"+error.HResult.ToString("X8",System.Globalization.CultureInfo.InvariantCulture);
        request.Result.ErrorOperation=request.Operation;
        request.Events.Add(request.Clock.ElapsedMilliseconds+":error:"+request.Operation+":"+request.Result.ErrorHResult);
        if(request.Events.Count>24)request.Events.RemoveAt(0);WriteTrace(request);
    }
    static void ClearCurrentError(RequestScope request) {
        request.Result.ErrorType="None";request.Result.ErrorHResult="0x00000000";request.Result.ErrorOperation="none";
    }
    static void ClearLiveState(RequestScope request) {
        request.Result.RejectReason="none";
        request.LiveSnapshotRead=request.LiveSurfaceValid=request.LiveProcessValid=request.LivePidMatches=request.LiveLabelMatches=false;
        request.LiveEnabled=request.LiveOffscreen=request.LiveBoundsValid=request.LiveRuntimeMatches=request.LiveInvokeDeclared=false;
        request.LivePatternAvailable=request.LiveOnScreen=request.LiveBoundsChanged=false;
    }
    static bool Reject(RequestScope request,string reason) {
        request.Result.RejectReason=reason;request.Events.Add(request.Clock.ElapsedMilliseconds+":reject:"+reason);
        if(request.Events.Count>24)request.Events.RemoveAt(0);WriteTrace(request);return false;
    }
    static string RequestRejectReason(RequestScope request){return Wanted(request.StillWanted)?"request-expired":"request-cancelled";}
    static void WriteTrace(RequestScope request) {
        try {
            // Deliberately excludes names, tooltip/account text, titles, paths,
            // raw exception messages and accessibility-tree dumps.
            string text="Discovery=notification-host-v4-live-identity\nApp="+request.App+"\nRequest="+request.Id+"\nPhase="+request.Result.Phase+"\nLastPhase="+request.Result.LastPhase+"\nRoute="+request.Result.Route
                +"\nElapsedMs="+request.Clock.ElapsedMilliseconds+"\nCandidates="+request.Result.CandidateCount
                +"\nNativeSurfaces="+request.NativeSurfaces+"\nUiaNodes="+request.UiaNodes+"\nWakeRequested="+request.Result.WakeRequested
                +"\nMsaaNodes="+request.MsaaNodes+"\nMsaaEmptyRoots="+request.MsaaEmptyRoots+"\nMsaaUnavailable="+request.MsaaUnavailable
                +"\nUiaSurfaces="+request.UiaSurfaces+"\nUiaLegacyScopes="+request.UiaLegacyScopes+"\nUiaShellScopes="+request.UiaShellScopes
                +"\nUiaXamlScopes="+request.UiaXamlScopes+"\nUiaOverflowScopes="+request.UiaOverflowScopes+"\nUiaRootUnknownPid="+request.UiaRootUnknownPid
                +"\nUiaRootForeignPid="+request.UiaRootForeignPid+"\nUiaForeignBranches="+request.UiaForeignBranches+"\nUiaEmptyRoots="+request.UiaEmptyRoots
                +"\nUiaChildren="+request.UiaChildren+"\nUiaRawBridges="+request.UiaRawBridges+"\nUiaContainers="+request.UiaContainers+"\nUiaButtons="+request.UiaButtons
                +"\nUiaLabelMatches="+request.UiaLabelMatches+"\nUiaCandidateWrongPid="+request.UiaCandidateWrongPid+"\nUiaHiddenMatches="+request.UiaHiddenMatches
                +"\nUiaNoInvokeMatches="+request.UiaNoInvokeMatches+"\nUiaTaskBranches="+request.UiaTaskBranches
                +"\nUiaDuplicateNodes="+request.UiaDuplicateNodes+"\nUiaDepthPrunes="+request.UiaDepthPrunes
                +"\nUiaUnresolvedDepth="+request.UiaUnresolvedDepth+"\nUiaBudgetExhausted="+request.UiaBudgetExhausted
                +"\nInitialIconPasses="+request.InitialIconPasses+"\nOverflowIconPasses="+request.OverflowIconPasses+"\nChevronPasses="+request.ChevronPasses
                +"\nUiaPasses="+request.UiaPasses+"\nMsaaPasses="+request.MsaaPasses+"\nChevronAttempted="+request.ChevronAttempted+"\nChevronSubmitted="+request.ChevronSubmitted
                +"\nLastChevronApi="+request.LastChevronApi+"\nOverflowObservations="+request.OverflowObservations+"\nOverflowWindows="+request.OverflowWindows.Count
                +"\nLastScanApi="+request.LastScanApi+"\nLastScanTarget="+request.LastScanTarget+"\nLastScanStage="+request.LastScanStage
                +"\nLastScanCandidates="+request.LastScanCandidates+"\nLastScanComplete="+request.LastScanComplete+"\nOperation="+request.Operation
                +"\nRejectReason="+request.Result.RejectReason+"\nLiveRefreshes="+request.LiveRefreshes+"\nLiveSnapshotRead="+request.LiveSnapshotRead
                +"\nLiveSurfaceValid="+request.LiveSurfaceValid+"\nLiveProcessValid="+request.LiveProcessValid+"\nLivePidMatches="+request.LivePidMatches
                +"\nLiveLabelMatches="+request.LiveLabelMatches+"\nLiveEnabled="+request.LiveEnabled+"\nLiveOffscreen="+request.LiveOffscreen
                +"\nLiveBoundsValid="+request.LiveBoundsValid+"\nLiveRuntimeMatches="+request.LiveRuntimeMatches+"\nLiveInvokeDeclared="+request.LiveInvokeDeclared
                +"\nLivePatternAvailable="+request.LivePatternAvailable+"\nLiveOnScreen="+request.LiveOnScreen
                +"\nLiveBoundsChanged="+request.LiveBoundsChanged+"\nLayoutChanges="+request.LayoutChanges
                +"\nActionUncertain="+request.Result.ActionUncertain+"\nShown="+request.Result.Shown+"\nErrorType="+request.Result.ErrorType
                +"\nErrorHResult="+request.Result.ErrorHResult+"\nErrorOperation="+request.Result.ErrorOperation+"\nErrorCount="+request.ErrorCount+"\nEvents="+String.Join(";",request.Events.ToArray())+"\nUtc="+DateTime.UtcNow.ToString("o");
            string file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dock-native-recovery.status"),pending=file+"."+request.Id+".pending";
            File.WriteAllText(pending,text);
            if(File.Exists(file))File.Replace(pending,file,null);else File.Move(pending,file);
        }catch{}
    }
    sealed class Surface {internal IntPtr Root,Handle;internal int Pid;internal bool Notification;internal string Kind;}
    sealed class TrayButton {
        internal Surface Surface;internal AutomationElement Element;internal IAccessible Accessible;
        internal object Child;internal string RuntimeKey,BoundsKey;internal bool Native;
    }
    internal static bool TrayButtonMatches(string name,bool sameShellProcess,bool enabled,bool offscreen,bool qq) {
        return sameShellProcess&&enabled&&!offscreen&&(qq?QqTrayLabel(name):ChevronLabel(name));
    }
    internal static bool TrayRootClass(string cls) {
        return cls=="Shell_TrayWnd"||cls=="Shell_SecondaryTrayWnd"||cls=="NotifyIconOverflowWindow"||cls=="TopLevelWindowForOverflowXamlIsland";
    }
    static string WindowClass(IntPtr h){var text=new StringBuilder(128);GetClassName(h,text,text.Capacity);return text.ToString();}
    static bool ShellTrayRoot(IntPtr h,int pid) {
        if(!SameWindow(h,pid)||!IsWindowVisible(h)||IsHungAppWindow(h)||!TrayRootClass(WindowClass(h)))return false;
        string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe");
        if(!String.Equals(NativeDockIcons.ProcessPath(pid,""),shell,StringComparison.OrdinalIgnoreCase))return false;
        int cloak;return DwmGetWindowAttribute(h,14,out cloak,4)==0&&cloak==0;
    }
    static bool VisibleShellTray() {
        bool visible=false;EnumWindows(delegate(IntPtr h,IntPtr ignored){uint pid;GetWindowThreadProcessId(h,out pid);
            string cls=WindowClass(h);if((cls=="Shell_TrayWnd"||cls=="Shell_SecondaryTrayWnd")&&ShellTrayRoot(h,(int)pid)){visible=true;return false;}return true;
        },IntPtr.Zero);return visible;
    }
    static List<Surface> Surfaces(RequestScope request,bool nativeOnly) {
        var found=new List<Surface>();var seen=new HashSet<long>();
        EnumWindows(delegate(IntPtr h,IntPtr ignored){
            if(!RequestCurrent(request))return false;uint pid;GetWindowThreadProcessId(h,out pid);
            if(pid==0||!ShellTrayRoot(h,(int)pid))return true;
            string cls=WindowClass(h);
            if(cls=="NotifyIconOverflowWindow"||cls=="TopLevelWindowForOverflowXamlIsland") {
                request.OverflowObservations++;request.OverflowWindows.Add(h.ToInt64());
                if(seen.Add(h.ToInt64()))found.Add(new Surface {Root=h,Handle=h,Pid=(int)pid,Notification=true,Kind="overflow"});
            }
            else {
                EnumChildWindows(h,delegate(IntPtr child,IntPtr unused){
                    if(!RequestCurrent(request))return false;
                    string childClass=WindowClass(child);
                    if(!SameWindow(child,(int)pid)||!IsWindowVisible(child))return true;
                    if(childClass=="TrayNotifyWnd"&&seen.Add(child.ToInt64()))found.Add(new Surface {Root=h,Handle=child,Pid=(int)pid,Notification=true,Kind="legacy"});
                    else if(!nativeOnly&&XamlHostClass(childClass)&&seen.Add(child.ToInt64()))found.Add(new Surface {Root=h,Handle=child,Pid=(int)pid,Notification=false,Kind="xaml"});
                    return true;
                },IntPtr.Zero);
                // Windows 11 can retain an empty legacy TrayNotifyWnd beside its
                // real XAML notification host. Existence of that HWND does not
                // make it the complete UIA notification tree. Always discover
                // the verified shell root independently, then require a known
                // notification container before accepting any icon below it.
                if(!nativeOnly&&seen.Add(h.ToInt64()))found.Add(new Surface {Root=h,Handle=h,Pid=(int)pid,Notification=false,Kind="shell"});
            }
            return found.Count<32;
        },IntPtr.Zero);
        return found;
    }
    internal static bool XamlHostClass(string cls){return cls=="Windows.UI.Composition.DesktopWindowContentBridge"||cls=="DesktopWindowXamlSource";}
    static bool SurfaceCurrent(Surface surface){return ShellTrayRoot(surface.Root,surface.Pid)&&SameWindow(surface.Handle,surface.Pid)&&IsWindowVisible(surface.Handle);}
    internal static int CompletedScanChoice(bool complete,int count) {
        // -1 means refusal; 0 means this complete representation has no icon;
        // 1 means its sole exact-owner icon may proceed to live revalidation.
        return !complete||count<0||count>1?-1:count;
    }
    static void BeginScan(RequestScope request,string api,bool chevron) {
        ClearCurrentError(request);request.Operation=api+"-scan-start";
        request.LastScanApi=api;request.LastScanTarget=chevron?"chevron":"icon";
        request.LastScanStage=request.ChevronSubmitted>0?"after-chevron":"initial";
        request.LastScanCandidates=0;request.LastScanComplete=false;
        if(api=="uia")request.UiaPasses++;else request.MsaaPasses++;
        Phase(request,(chevron?"chevron":request.ChevronSubmitted>0?"overflow-icon":"icon")+"-"+api+"-scan");
    }
    static void EndScan(RequestScope request,bool complete,int count) {
        request.LastScanComplete=complete;request.LastScanCandidates=count;request.Result.CandidateCount=count;
        request.Events.Add(request.Clock.ElapsedMilliseconds+":scan:"+request.LastScanApi+":"+request.LastScanTarget+":"+request.LastScanStage+":"+complete+":"+count);
        if(request.Events.Count>24)request.Events.RemoveAt(0);WriteTrace(request);
    }
    static List<TrayButton> FindButtons(string app,bool chevron,RequestScope request) {
        if(chevron)request.ChevronPasses++;else if(request.ChevronSubmitted>0)request.OverflowIconPasses++;else request.InitialIconPasses++;
        // Modern shell UIA is an independent representation. A complete, unique
        // UIA target must not be blocked by an unrelated legacy MSAA branch.
        // Incomplete or ambiguous data from either interface is never selected.
        BeginScan(request,"uia",chevron);
        bool complete;
        var uia=UiaButtons(app,chevron,request,out complete);
        EndScan(request,complete,uia.Count);
        if(!RequestCurrent(request))return null;if(!complete){Phase(request,"uia-incomplete");return null;}
        if(CompletedScanChoice(complete,uia.Count)!=0)return uia;
        BeginScan(request,"msaa",chevron);
        var native=NativeButtons(app,chevron,request,out complete);
        EndScan(request,complete,native.Count);
        if(!RequestCurrent(request))return null;if(!complete){Phase(request,"msaa-incomplete");return null;}
        return native;
    }
    static List<TrayButton> NativeButtons(string app,bool chevron,RequestScope request,out bool complete) {
        var found=new List<TrayButton>();var seen=new HashSet<string>(StringComparer.Ordinal);int budget=256;complete=true;bool unsupported=false;
        foreach(var surface in Surfaces(request,true)) {
            request.NativeSurfaces++;
            try {
                IAccessible accessible;Guid iid=new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                request.Operation="msaa-root-open";
                if(AccessibleObjectFromWindow(surface.Handle,unchecked((uint)-4),ref iid,out accessible)<0||accessible==null){request.MsaaUnavailable++;continue;}
                request.ComObjects.Add(accessible);
                if(!WalkAccessible(accessible,0,0,surface,app,chevron,request,found,seen,ref budget,ref unsupported)){complete=false;break;}
            }catch(Exception error){Error(request,error);if(UnsupportedMsaa(error))unsupported=true;else{complete=false;break;}}
        }
        // An unavailable interface with no matches may use the independent UIA
        // representation. A partially represented tree with any match remains
        // incomplete: it cannot establish that this is the unique target.
        complete=complete&&NativeScanComplete(found.Count,unsupported);
        return found;
    }
    internal static bool UnsupportedMsaa(Exception error) {
        return error is COMException&&(error.HResult==unchecked((int)0x80004001)||error.HResult==unchecked((int)0x80004002)||error.HResult==unchecked((int)0x80020003));
    }
    internal static bool NativeScanComplete(int candidates,bool unsupported){return candidates>=0&&(!unsupported||candidates==0);}
    static string AccessibleIdentity(IAccessible accessible,object child) {
        IntPtr unknown=Marshal.GetIUnknownForObject(accessible);
        try{return unknown.ToInt64()+":"+Convert.ToInt32(child);}finally{Marshal.Release(unknown);}
    }
    static bool WalkAccessible(IAccessible accessible,object child,int depth,Surface surface,string app,bool chevron,RequestScope request,List<TrayButton> found,HashSet<string> seen,ref int budget,ref bool unsupported) {
        if(!RequestCurrent(request)||--budget<0||depth>8)return false;
        request.MsaaNodes++;
        try {
            request.Operation="msaa-name-read";
            string name=accessible.get_accName(child);
            request.Operation="msaa-state-read";int state=Convert.ToInt32(accessible.get_accState(child));
            request.Operation="msaa-role-read";int role=Convert.ToInt32(accessible.get_accRole(child));
            if(LegacyButtonMatches(name,role,state,app,chevron)) {
                request.Operation="msaa-default-action-read";
                string action=accessible.get_accDefaultAction(child);
                request.Operation="msaa-location-read";
                int x,y,width,height;accessible.accLocation(out x,out y,out width,out height,child);
                if(!String.IsNullOrWhiteSpace(action)&&width>0&&height>0) {
                    string bounds=x+","+y+","+width+","+height;
                    request.Operation="msaa-identity-read";
                    if(seen.Add(AccessibleIdentity(accessible,child)))found.Add(new TrayButton {Surface=surface,Accessible=accessible,Child=child,BoundsKey=bounds,Native=true});
                }
            }
            if(Convert.ToInt32(child)!=0)return true;
            request.Operation="msaa-child-count-read";
            int count=accessible.accChildCount;if(count<0||count>128)return false;
            if(count==0){if(depth==0)request.MsaaEmptyRoots++;return true;}var children=new object[count];int obtained;
            request.Operation="msaa-children-read";
            if(AccessibleChildren(accessible,0,count,children,out obtained)<0)return false;
            for(int i=0;i<obtained;i++) {
                if(!RequestCurrent(request))return false;
                var nested=children[i] as IAccessible;
                if(nested!=null){request.ComObjects.Add(nested);if(!WalkAccessible(nested,0,depth+1,surface,app,chevron,request,found,seen,ref budget,ref unsupported))return false;}
                else if(children[i] is int&&!WalkAccessible(accessible,children[i],depth+1,surface,app,chevron,request,found,seen,ref budget,ref unsupported))return false;
            }
            return true;
        }catch(Exception error){Error(request,error);if(UnsupportedMsaa(error)){unsupported=true;return true;}return false;}
    }
    internal static bool LegacyButtonMatches(string name,int role,int state,string app,bool chevron) {
        const int Unavailable=1,Invisible=0x8000,Offscreen=0x10000;
        return role==0x2b&&(state&(Unavailable|Invisible|Offscreen))==0&&LabelMatches(name,app,chevron);
    }
    sealed class UiaNode {internal AutomationElement Element;internal int Depth;internal bool Notification;}
    static CacheRequest TrayCache() {
        // The default ControlView filter can omit intermediate HWND/XAML
        // bridges. Keep the raw representation while selecting notification
        // controls explicitly below; this is a read-only discovery change.
        var cache=new CacheRequest {TreeScope=TreeScope.Element,TreeFilter=Automation.RawViewCondition,AutomationElementMode=AutomationElementMode.Full};
        cache.Add(AutomationElement.NameProperty);cache.Add(AutomationElement.ProcessIdProperty);cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.ClassNameProperty);cache.Add(AutomationElement.ControlTypeProperty);cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);cache.Add(AutomationElement.IsInvokePatternAvailableProperty);cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.RuntimeIdProperty);
        return cache;
    }
    internal static bool NotificationContainer(string id,string cls,string name) {
        return id=="SystemTrayFrame"||id=="NotifyIconStack"||id=="SystemTrayIconStack"||id.StartsWith("SystemTray.",StringComparison.Ordinal)
            ||cls=="TrayNotifyWnd"||cls.StartsWith("SystemTray.",StringComparison.Ordinal)
            ||name=="User Promoted Notification Area"||name=="System Promoted Notification Area"||name=="Overflow Notification Area"||name=="Notification Area"
            ||name=="用户提升的通知区域"||name=="系统提升的通知区域"||name=="通知区域";
    }
    internal static bool UiaTraversalPidAllowed(int shellPid,int providerPid,bool verifiedNativeRoot) {
        // ProcessId's documented default is zero. An intermediate read-only
        // proxy must not hide the exact-owner descendants we are looking for.
        // The seeded root already has a separately verified Explorer HWND.
        return shellPid>0&&(providerPid==shellPid||providerPid==0||verifiedNativeRoot);
    }
    internal static bool UiaTargetPidAllowed(int shellPid,int providerPid){return shellPid>0&&providerPid==shellPid;}
    internal static bool UiaTaskBranch(string id,string cls){return cls=="MSTaskListWClass"||(id??"").StartsWith("Taskbar.TaskList",StringComparison.Ordinal);}
    internal static bool UiaStopBranch(string id,string cls,bool button,bool knownContainer){return UiaTaskBranch(id,cls)||(button&&!knownContainer);}
    static string RuntimeKey(AutomationElement element) {
        var values=element.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[];
        if(values==null)values=element.GetRuntimeId();
        return values==null?"":String.Join(",",values.Select(v=>v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
    }
    internal static bool VisitUiaNode(Dictionary<string,int> visited,string runtime,bool notification,int depth) {
        if(String.IsNullOrEmpty(runtime))return true;
        string key=runtime+"|"+notification;int previous;
        if(visited.TryGetValue(key,out previous)&&previous<=depth)return false;
        visited[key]=depth;return true;
    }
    static List<TrayButton> UiaButtons(string app,bool chevron,RequestScope request,out bool complete) {
        var found=new List<TrayButton>();var seen=new HashSet<string>(StringComparer.Ordinal);int budget=320;complete=true;
        var visited=new Dictionary<string,int>(StringComparer.Ordinal);var unresolvedDepth=new HashSet<string>(StringComparer.Ordinal);
        foreach(var surface in Surfaces(request,false)) {
            request.UiaSurfaces++;
            if(surface.Kind=="legacy")request.UiaLegacyScopes++;else if(surface.Kind=="shell")request.UiaShellScopes++;
            else if(surface.Kind=="xaml")request.UiaXamlScopes++;else if(surface.Kind=="overflow")request.UiaOverflowScopes++;
            try {
                if(!SurfaceCurrent(surface))continue;
                var cache=TrayCache();var queue=new Queue<UiaNode>();
                request.Operation="uia-root-cache-read";
                queue.Enqueue(new UiaNode {Element=AutomationElement.FromHandle(surface.Handle).GetUpdatedCache(cache),Notification=surface.Notification});
                while(queue.Count>0) {
                    if(!RequestCurrent(request)){complete=false;break;}
                    var node=queue.Dequeue();
                    request.Operation="uia-node-cache-read";
                    var current=node.Element.Cached;
                    bool nativeRoot=node.Depth==0;
                    if(nativeRoot&&current.ProcessId==0)request.UiaRootUnknownPid++;
                    else if(nativeRoot&&current.ProcessId!=surface.Pid)request.UiaRootForeignPid++;
                    if(!UiaTraversalPidAllowed(surface.Pid,current.ProcessId,nativeRoot)){request.UiaForeignBranches++;continue;}
                    if(XamlHostClass(current.ClassName)||(!nativeRoot&&current.ProcessId==0))request.UiaRawBridges++;
                    // Whole-shell/XAML roots cannot gain notification scope
                    // from a localized name alone. Legacy/overflow scopes were
                    // already proved by their native notification HWND class.
                    bool knownContainer=NotificationContainer(current.AutomationId??"",current.ClassName??"",surface.Notification?current.Name??"":"");
                    if(knownContainer)request.UiaContainers++;
                    bool notification=node.Notification||knownContainer;
                    request.Operation="uia-runtime-read";
                    string runtime=RuntimeKey(node.Element),visitKey=runtime+"|"+notification;
                    if(!VisitUiaNode(visited,runtime,notification,node.Depth)){request.UiaDuplicateNodes++;continue;}
                    if(--budget<0){request.UiaBudgetExhausted++;complete=false;break;}
                    request.UiaNodes++;unresolvedDepth.Remove(visitKey);
                    request.Operation="uia-pattern-availability-read";
                    bool invokable=(bool)node.Element.GetCachedPropertyValue(AutomationElement.IsInvokePatternAvailableProperty);
                    if(current.ControlType==ControlType.Button)request.UiaButtons++;
                    bool matching=notification&&LabelMatches(current.Name,app,chevron);
                    if(matching) {
                        request.UiaLabelMatches++;
                        if(!UiaTargetPidAllowed(surface.Pid,current.ProcessId))request.UiaCandidateWrongPid++;
                        if(!current.IsEnabled||current.IsOffscreen)request.UiaHiddenMatches++;
                        if(!invokable)request.UiaNoInvokeMatches++;
                    }
                    if(matching&&UiaTargetPidAllowed(surface.Pid,current.ProcessId)&&current.IsEnabled&&!current.IsOffscreen&&invokable) {
                        var bounds=current.BoundingRectangle;
                        string key=bounds.X+","+bounds.Y+","+bounds.Width+","+bounds.Height;
                        // RuntimeId is the UIA identity. Coincident bounds alone
                        // do not prove that two controls are the same icon.
                        if(!String.IsNullOrEmpty(runtime)&&bounds.Width>0&&bounds.Height>0&&seen.Add(runtime))found.Add(new TrayButton {Surface=surface,Element=node.Element,RuntimeKey=runtime,BoundsKey=key});
                    }
                    // Button descendants and the task-list branch cannot contain
                    // notification icons. Avoid a whole-taskbar descendant query.
                    bool taskBranch=UiaTaskBranch(current.AutomationId,current.ClassName);
                    if(taskBranch)request.UiaTaskBranches++;
                    if(UiaStopBranch(current.AutomationId,current.ClassName,current.ControlType==ControlType.Button,knownContainer))continue;
                    if(node.Depth>=9){request.UiaDepthPrunes++;unresolvedDepth.Add(String.IsNullOrEmpty(runtime)?"unidentified:"+surface.Handle.ToInt64()+":"+request.UiaNodes:visitKey);continue;}
                    request.Operation="uia-children-read";
                    AutomationElementCollection children;using(cache.Activate())children=node.Element.FindAll(TreeScope.Children,Condition.TrueCondition);
                    if(children.Count>128){complete=false;break;}
                    request.UiaChildren+=children.Count;
                    if(nativeRoot&&children.Count==0)request.UiaEmptyRoots++;
                    foreach(AutomationElement child in children)queue.Enqueue(new UiaNode {Element=child,Depth=node.Depth+1,Notification=notification});
                }
            }catch(Exception error){Error(request,error);complete=false;}
            if(!complete)break;
        }
        request.UiaUnresolvedDepth+=unresolvedDepth.Count;
        if(unresolvedDepth.Count>0)complete=false;
        return found;
    }
    internal static bool PositiveLiveBounds(double x,double y,double width,double height) {
        return !Double.IsNaN(x)&&!Double.IsInfinity(x)&&!Double.IsNaN(y)&&!Double.IsInfinity(y)
            &&!Double.IsNaN(width)&&!Double.IsInfinity(width)&&!Double.IsNaN(height)&&!Double.IsInfinity(height)&&width>0&&height>0
            &&!Double.IsInfinity(x+width)&&!Double.IsInfinity(y+height);
    }
    static bool LiveBoundsOnScreen(System.Windows.Rect bounds) {
        if(!PositiveLiveBounds(bounds.X,bounds.Y,bounds.Width,bounds.Height)||bounds.X<Int32.MinValue+1||bounds.Y<Int32.MinValue+1
            ||bounds.Right>Int32.MaxValue-1||bounds.Bottom>Int32.MaxValue-1)return false;
        // Read-only membership check against the actual display layout. The
        // notification icon may animate/move; these coordinates are never used
        // for clicking. Invoke continues to act on the verified UIA identity.
        var rectangle=new Rect {Left=(int)Math.Floor(bounds.X),Top=(int)Math.Floor(bounds.Y),Right=(int)Math.Ceiling(bounds.Right),Bottom=(int)Math.Ceiling(bounds.Bottom)};
        return MonitorFromRect(ref rectangle,0)!=IntPtr.Zero;
    }
    internal static string UiaLiveRejectReason(bool samePid,bool sameLabel,bool enabled,bool offscreen,bool positiveBounds,bool onScreen,bool sameRuntime,bool declaredInvoke,bool availablePattern) {
        if(!samePid)return "uia-owner-mismatch";if(!sameLabel)return "uia-label-mismatch";if(!enabled)return "uia-disabled";
        if(offscreen)return "uia-offscreen";if(!positiveBounds)return "uia-bounds-invalid";if(!onScreen)return "uia-bounds-off-screen";
        if(!sameRuntime)return "uia-runtime-mismatch";if(!declaredInvoke)return "uia-invoke-not-declared";if(!availablePattern)return "uia-pattern-unavailable";
        return "none";
    }
    static bool Act(TrayButton target,string app,bool chevron,string identity,RequestScope request) {
        bool submitting=false;
        try {
            ClearCurrentError(request);ClearLiveState(request);request.Operation="default-action-revalidate";
            Phase(request,chevron?"chevron-revalidate":"icon-revalidate");
            request.Operation="default-action-owner-revalidate";
            if(!RequestCurrent(request))return Reject(request,RequestRejectReason(request));
            request.LiveSurfaceValid=SurfaceCurrent(target.Surface);if(!request.LiveSurfaceValid)return Reject(request,"surface-changed");
            request.LiveProcessValid=ExactProcessPresent(identity);if(!request.LiveProcessValid)return Reject(request,"process-unavailable");
            Action action;
            if(target.Native) {
                request.Operation="msaa-name-revalidate";
                string name=target.Accessible.get_accName(target.Child);
                request.Operation="msaa-role-revalidate";int role=Convert.ToInt32(target.Accessible.get_accRole(target.Child));
                request.Operation="msaa-state-revalidate";int state=Convert.ToInt32(target.Accessible.get_accState(target.Child));
                request.Operation="msaa-default-action-revalidate";
                string defaultAction=target.Accessible.get_accDefaultAction(target.Child);
                request.Operation="msaa-location-revalidate";
                int x,y,width,height;target.Accessible.accLocation(out x,out y,out width,out height,target.Child);
                if(!LegacyButtonMatches(name,role,state,app,chevron)||String.IsNullOrWhiteSpace(defaultAction))return Reject(request,"msaa-identity-state-changed");
                if(target.BoundsKey!=x+","+y+","+width+","+height)return Reject(request,"msaa-bounds-changed");
                action=delegate {target.Accessible.accDoDefaultAction(target.Child);};
            }else {
                request.Operation="uia-live-cache-refresh";
                var cache=TrayCache();cache.Add(InvokePattern.Pattern);
                var fresh=target.Element.GetUpdatedCache(cache);request.LiveRefreshes++;request.LiveSnapshotRead=true;
                if(!RequestCurrent(request))return Reject(request,RequestRejectReason(request));
                // One bulk refresh reduces mixed animation snapshots from
                // repeated Current property calls. It is not a transaction.
                request.Operation="uia-live-properties-cache";
                var current=fresh.Cached;
                request.LivePidMatches=UiaTargetPidAllowed(target.Surface.Pid,current.ProcessId);
                request.LiveLabelMatches=LabelMatches(current.Name,app,chevron);
                request.LiveEnabled=current.IsEnabled;request.LiveOffscreen=current.IsOffscreen;
                var bounds=current.BoundingRectangle;
                request.LiveBoundsValid=PositiveLiveBounds(bounds.X,bounds.Y,bounds.Width,bounds.Height);
                request.LiveBoundsChanged=target.BoundsKey!=bounds.X+","+bounds.Y+","+bounds.Width+","+bounds.Height;if(request.LiveBoundsChanged)request.LayoutChanges++;
                request.Operation="uia-live-screen-membership";request.LiveOnScreen=LiveBoundsOnScreen(bounds);
                request.Operation="uia-live-runtime-cache";request.LiveRuntimeMatches=RuntimeKey(fresh)==target.RuntimeKey;
                request.Operation="uia-live-invoke-cache";
                request.LiveInvokeDeclared=(bool)fresh.GetCachedPropertyValue(AutomationElement.IsInvokePatternAvailableProperty);
                object pattern;request.LivePatternAvailable=fresh.TryGetCachedPattern(InvokePattern.Pattern,out pattern)&&pattern is InvokePattern;
                string reason=UiaLiveRejectReason(request.LivePidMatches,request.LiveLabelMatches,request.LiveEnabled,request.LiveOffscreen,request.LiveBoundsValid,
                    request.LiveOnScreen,request.LiveRuntimeMatches,request.LiveInvokeDeclared,request.LivePatternAvailable);
                if(reason!="none")return Reject(request,reason);
                action=delegate {((InvokePattern)pattern).Invoke();};
            }
            request.Operation="default-action-owner-final";
            if(!RequestCurrent(request))return Reject(request,RequestRejectReason(request));
            request.LiveProcessValid=ExactProcessPresent(identity);if(!request.LiveProcessValid)return Reject(request,"process-unavailable");
            request.LiveSurfaceValid=SurfaceCurrent(target.Surface);if(!request.LiveSurfaceValid)return Reject(request,"surface-changed");
            if(!RequestCurrent(request))return Reject(request,RequestRejectReason(request));
            if(!chevron)request.Result.Route=app+(target.Native?"-tray-msaa-default":"-tray-uia-invoke");
            // One provider-declared default action. Never synthesize a double
            // click by invoking twice, and never retry through a second API.
            request.Operation=(chevron?"chevron":"icon")+(target.Native?"-msaa-default-action":"-uia-invoke");
            if(chevron){request.ChevronAttempted++;request.LastChevronApi=target.Native?"msaa":"uia";}
            Phase(request,chevron?"chevron-submitting":"wake-submitting");
            // File/provider work above may itself consume the deadline. No
            // action follows a late return or a newer click generation.
            if(!RequestCurrent(request)){if(chevron)request.ChevronAttempted--;return Reject(request,RequestRejectReason(request));}
            submitting=true;action();
            if(chevron)request.ChevronSubmitted++;else request.Result.WakeRequested=true;
            Phase(request,chevron?"chevron-submitted":"wake-submitted");return RequestCurrent(request);
        }catch(Exception error){
            request.Result.RejectReason=submitting?"default-action-unconfirmed":"provider-validation-exception";
            if(submitting&&!chevron){request.Result.WakeRequested=false;request.Result.ActionUncertain=true;Phase(request,"action-unconfirmed");}
            Error(request,error);return false;
        }
    }
    static bool EligibleShown(IntPtr h,int pid,string identity,string app) {
        if(!SameWindow(h,pid)||!IsWindowVisible(h)||IsIconic(h)||!IsWindowEnabled(h)||IsHungAppWindow(h)||GetWindow(h,4)!=IntPtr.Zero||!OnScreen(h))return false;
        if(!String.Equals(NativeDockIcons.ProcessPath(pid,""),identity,StringComparison.OrdinalIgnoreCase))return false;
        long extended=GetLong(h,-20),style=GetLong(h,-16);if((extended&0x08000080L)!=0||(style&0x40000000L)!=0)return false;
        int cloak;if(DwmGetWindowAttribute(h,14,out cloak,4)!=0||cloak!=0)return false;
        var text=new StringBuilder(256);GetClassName(h,text,text.Capacity);string cls=text.ToString();text.Clear();GetWindowText(h,text,text.Capacity);string caption=text.ToString().Trim();
        var placement=new Placement {Length=Marshal.SizeOf(typeof(Placement))};if(!GetWindowPlacement(h,ref placement))return false;
        uint dpi=GetDpiForWindow(h);if(dpi==0)return false;
        double width=(placement.Normal.Right-placement.Normal.Left)*96.0/dpi,height=(placement.Normal.Bottom-placement.Normal.Top)*96.0/dpi;
        return app=="qq"?QqMainShape(cls,caption,width,height):MainShape(cls,caption,width,height);
    }
    internal static bool OnScreen(IntPtr h) {
        Rect r;if(!GetWindowRect(h,out r))return false;var rectangle=System.Drawing.Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
        return Forms.Screen.AllScreens.Any(s=>{var visible=System.Drawing.Rectangle.Intersect(s.Bounds,rectangle);return visible.Width>=120&&visible.Height>=120;});
    }
    sealed class ScopeFixtureNode {
        internal int Pid;internal string Id="",Class="",Name="",Runtime="",Bounds="fixture-same-bounds";internal bool Button;
        internal readonly List<ScopeFixtureNode> Children=new List<ScopeFixtureNode>();
    }
    sealed class ScopeFixtureVisit {internal ScopeFixtureNode Node;internal int Depth;internal bool Notification;}
    static HashSet<string> DiscoverScopeFixture(IEnumerable<ScopeFixtureNode> roots,int shellPid,int budget) {
        var found=new HashSet<string>(StringComparer.Ordinal);var visited=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var root in roots) {
            var queue=new Queue<ScopeFixtureVisit>();queue.Enqueue(new ScopeFixtureVisit {Node=root});
            while(queue.Count>0) {
                var item=queue.Dequeue();var node=item.Node;
                if(!UiaTraversalPidAllowed(shellPid,node.Pid,item.Depth==0))continue;
                bool known=NotificationContainer(node.Id,node.Class,""),notification=item.Notification||known;
                if(!VisitUiaNode(visited,node.Runtime,notification,item.Depth))continue;
                if(--budget<0)return null;
                if(notification&&node.Button&&QqTrayLabel(node.Name)&&UiaTargetPidAllowed(shellPid,node.Pid))found.Add(node.Runtime);
                if(UiaStopBranch(node.Id,node.Class,node.Button,known))continue;
                if(item.Depth>=9)return null;
                foreach(var child in node.Children)queue.Enqueue(new ScopeFixtureVisit {Node=child,Depth=item.Depth+1,Notification=notification});
            }
        }
        return found;
    }
    static void CheckScopeFixture() {
        const int shell=42;
        var legacy=new ScopeFixtureNode {Pid=shell,Class="TrayNotifyWnd",Runtime="legacy-empty"};
        var host=new ScopeFixtureNode {Pid=0,Class="Windows.UI.Composition.DesktopWindowContentBridge",Runtime="host-zero"};
        var area=new ScopeFixtureNode {Pid=shell,Id="SystemTrayFrame",Runtime="notification-area"};
        var qq=new ScopeFixtureNode {Pid=shell,Name="QQ",Button=true,Runtime="qq-icon"};
        var unknown=new ScopeFixtureNode {Pid=0,Name="QQ",Button=true,Runtime="unproven-qq"};
        area.Children.Add(qq);area.Children.Add(unknown);host.Children.Add(area);
        var tasklist=new ScopeFixtureNode {Pid=shell,Id="Taskbar.TaskList",Runtime="tasklist"};
        tasklist.Children.Add(new ScopeFixtureNode {Pid=shell,Name="QQ",Button=true,Runtime="taskbar-qq"});
        var foreign=new ScopeFixtureNode {Pid=shell+1,Id="SystemTrayFrame",Runtime="foreign-area"};
        foreign.Children.Add(new ScopeFixtureNode {Pid=shell,Name="QQ",Button=true,Runtime="foreign-path-qq"});
        var root=new ScopeFixtureNode {Pid=0,Class="Shell_TrayWnd",Runtime="shell-root-zero"};
        root.Children.Add(host);root.Children.Add(tasklist);root.Children.Add(foreign);
        var found=DiscoverScopeFixture(new[]{legacy,root,host},shell,320);
        if(found==null||found.Count!=1||!found.Contains("qq-icon"))throw new InvalidOperationException("Empty legacy sibling/PID-zero XAML discovery regression");
        if(UiaTargetPidAllowed(shell,0)||UiaTraversalPidAllowed(shell,shell+1,false)||!UiaTraversalPidAllowed(shell,0,false))throw new InvalidOperationException("Read-only bridge versus exact-owner action policy");
        var visits=new Dictionary<string,int>();
        if(!VisitUiaNode(visits,"identity",false,9)||VisitUiaNode(visits,"identity",false,9)||!VisitUiaNode(visits,"identity",false,1)
            ||!VisitUiaNode(visits,"identity",true,3)||VisitUiaNode(visits,"identity",true,4))throw new InvalidOperationException("UIA identity/scope/shallower-visit policy");
        area.Children.Add(new ScopeFixtureNode {Pid=shell,Name="QQ",Button=true,Runtime="second-distinct-qq",Bounds=qq.Bounds});
        found=DiscoverScopeFixture(new[]{legacy,root,host},shell,320);
        if(found==null||found.Count!=2)throw new InvalidOperationException("Distinct overlapped icons incorrectly merged");
        if(DiscoverScopeFixture(new[]{legacy,root},shell,1)!=null)throw new InvalidOperationException("Incomplete scope budget accepted");
    }
    internal static void Check(string report) {
        CheckScopeFixture();
        // A complete unique UIA result is sufficient even if an unqueried MSAA
        // representation would be partial. Only complete-zero permits legacy
        // scanning; partial/ambiguous results never become a single target.
        if(CompletedScanChoice(true,1)!=1||CompletedScanChoice(true,0)!=0||CompletedScanChoice(true,2)!=-1
            ||CompletedScanChoice(false,0)!=-1||CompletedScanChoice(false,1)!=-1||CompletedScanChoice(true,-1)!=-1)
            throw new InvalidOperationException("UIA-first complete/unique versus legacy fallback selection");
        if(!PositiveLiveBounds(-100,20,32,32)||!PositiveLiveBounds(140,90,40,36)||PositiveLiveBounds(0,0,0,32)
            ||PositiveLiveBounds(0,0,32,-1)||PositiveLiveBounds(Double.NaN,0,32,32)||PositiveLiveBounds(0,0,Double.PositiveInfinity,32)
            ||PositiveLiveBounds(Double.MaxValue,0,Double.MaxValue,32))throw new InvalidOperationException("Live finite positive icon bounds");
        // Position/size changes do not enter identity eligibility. A moved live
        // icon keeps its exact PID/runtime, declared pattern and visible state.
        if(UiaLiveRejectReason(true,true,true,false,true,true,true,true,true)!="none"
            ||UiaLiveRejectReason(false,true,true,false,true,true,true,true,true)!="uia-owner-mismatch"
            ||UiaLiveRejectReason(true,false,true,false,true,true,true,true,true)!="uia-label-mismatch"
            ||UiaLiveRejectReason(true,true,false,false,true,true,true,true,true)!="uia-disabled"
            ||UiaLiveRejectReason(true,true,true,true,true,true,true,true,true)!="uia-offscreen"
            ||UiaLiveRejectReason(true,true,true,false,false,true,true,true,true)!="uia-bounds-invalid"
            ||UiaLiveRejectReason(true,true,true,false,true,false,true,true,true)!="uia-bounds-off-screen"
            ||UiaLiveRejectReason(true,true,true,false,true,true,false,true,true)!="uia-runtime-mismatch"
            ||UiaLiveRejectReason(true,true,true,false,true,true,true,false,true)!="uia-invoke-not-declared"
            ||UiaLiveRejectReason(true,true,true,false,true,true,true,true,false)!="uia-pattern-unavailable")
            throw new InvalidOperationException("Live identity eligibility and explicit rejection reasons");
        var diagnosticFixture=new RequestScope(null,"fixture",new Result());diagnosticFixture.Operation="msaa-role-read";
        diagnosticFixture.Result.ErrorType="COMException";diagnosticFixture.Result.ErrorHResult="0x80004005";diagnosticFixture.Result.ErrorOperation="msaa-role-read";
        diagnosticFixture.Events.Add("fixture-error-history");ClearCurrentError(diagnosticFixture);
        if(diagnosticFixture.Result.ErrorType!="None"||diagnosticFixture.Result.ErrorHResult!="0x00000000"||diagnosticFixture.Result.ErrorOperation!="none"||diagnosticFixture.Events.Count!=1)
            throw new InvalidOperationException("Current diagnostics versus preserved bounded history");
        if(!Supported(@"C:\fixture\Weixin.exe")||!Supported(@"C:\fixture\WeChat.exe")||!Supported(@"C:\fixture\QQ.exe")||Supported(@"C:\fixture\QQMusic.exe"))throw new InvalidOperationException("Messenger recovery dispatch");
        if(!QqMainShape("Chrome_WidgetWin_1","QQ",1000,700)||!QqMainShape("Chrome_WidgetWin_1","QQ",350,800)
            ||QqMainShape("Chrome_WidgetWin_1","QQ",320,460)||QqMainShape("Chrome_WidgetWin_1","QQ",480*96.0/144,690*96.0/144)
            ||QqMainShape("Chrome_WidgetWin_1","QQ",640*96.0/192,920*96.0/192)||QqMainShape("Chrome_WidgetWin_0","QQ",1000,700)
            ||QqMainShape("Chrome_WidgetWin_1","QQ聊天",1000,700)||QqMainShape("Chrome_WidgetWin_1","QQ",70,56))throw new InvalidOperationException("QQ login/helper/main classification");
        if(!QqTrayLabel("QQ")||!QqTrayLabel("QQ: fixture")||!QqTrayLabel("QQ：fixture")||!QqTrayLabel("QQ\nfixture")||!QqTrayLabel("QQ（1）")
            ||QqTrayLabel("QQ音乐")||QqTrayLabel("QQMusic")||QqTrayLabel("Open QQ")||QqTrayLabel("QQ浏览器")||QqTrayLabel("QQ account"))throw new InvalidOperationException("QQ tray identity isolation");
        if(!WechatTrayLabel("微信")||!WechatTrayLabel("微信（1条新消息）")||!WechatTrayLabel("WeChat: fixture")||!WechatTrayLabel("Weixin")
            ||WechatTrayLabel("微信开发者工具")||WechatTrayLabel("企业微信")||WechatTrayLabel("WeChatWork"))throw new InvalidOperationException("WeChat tray identity isolation");
        if(!TrayRootClass("Shell_TrayWnd")||!TrayRootClass("TopLevelWindowForOverflowXamlIsland")||TrayRootClass("Chrome_WidgetWin_1")
            ||!ChevronLabel("显示隐藏的图标")||ChevronLabel("关闭")||ChevronLabel("QQ"))throw new InvalidOperationException("Shell tray/chevron scope");
        if(!LegacyButtonMatches("QQ",0x2b,0,"qq",false)||LegacyButtonMatches("QQ",0x2b,1,"qq",false)||LegacyButtonMatches("QQ",0x2b,0x8000,"qq",false)
            ||LegacyButtonMatches("QQ",0x2b,0x10000,"qq",false)||LegacyButtonMatches("QQ",0x16,0,"qq",false)||LegacyButtonMatches("QQ音乐",0x2b,0,"qq",false))throw new InvalidOperationException("Legacy default-action role/state/identity isolation");
        if(!NotificationContainer("SystemTray.NotifyIconStack","","")||!NotificationContainer("SystemTrayFrame","","")||!NotificationContainer("","TrayNotifyWnd","")
            ||NotificationContainer("Taskbar.TaskList","MSTaskListWClass","QQ"))throw new InvalidOperationException("Notification subtree isolation");
        if(!UnsupportedMsaa(new COMException("fixture",unchecked((int)0x80004001)))||!UnsupportedMsaa(new COMException("fixture",unchecked((int)0x80004002)))
            ||!UnsupportedMsaa(new COMException("fixture",unchecked((int)0x80020003)))||UnsupportedMsaa(new COMException("fixture",unchecked((int)0x80004005)))
            ||UnsupportedMsaa(new InvalidOperationException("fixture"))||!NativeScanComplete(0,true)||NativeScanComplete(1,true)||NativeScanComplete(2,true)
            ||!NativeScanComplete(1,false)||NativeScanComplete(-1,false))throw new InvalidOperationException("Unavailable MSAA fallback/partial-tree refusal");
        if(!RequestAgeCurrent(0)||!RequestAgeCurrent(4999)||RequestAgeCurrent(5000)||RequestAgeCurrent(300000)||RequestAgeCurrent(-1))throw new InvalidOperationException("Expired tray request accepted");
        if(!Wanted(null)||!Wanted(delegate{return true;})||Wanted(delegate{return false;})||Wanted(delegate{throw new InvalidOperationException("fixture");}))throw new InvalidOperationException("Cancelled request accepted");
        bool desired=true;var fixtureRequest=new RequestScope(delegate{return desired;},"fixture",new Result());
        if(!RequestCurrent(fixtureRequest))throw new InvalidOperationException("Fresh request refused");desired=false;if(RequestCurrent(fixtureRequest))throw new InvalidOperationException("Changed generation ignored");
        if(!MainShape("Qt51514QWindowIcon","微信",800,600)||!MainShape("WeChatMainWndForPC","WeChat",650,520)
            ||MainShape("Qt51514QWindowIcon","微信",320,460)||MainShape("Qt51514QWindowIcon","登录",800,600)||MainShape("Chrome_WidgetWin_1","QQ",800,600))throw new InvalidOperationException("Login/helper root admitted");
        using(var fixture=new Forms.Form {Text="Owned tray fixture",StartPosition=Forms.FormStartPosition.Manual,Location=new System.Drawing.Point(-20000,-20000),Size=new System.Drawing.Size(800,600),ShowInTaskbar=false}) {
            IntPtr h=fixture.Handle;int pid=Process.GetCurrentProcess().Id;
            if(!SameWindow(h,pid)||SameWindow(h,pid+1)||IsWindowVisible(h)||OnScreen(h))throw new InvalidOperationException("Owned HWND/PID preconditions");
            // Diagnostics never show even the fixture: product recovery contains
            // no ShowWindow or forced foreground path to accidentally exercise.
            using(var button=new Forms.Button {Text="Owned default-action fixture"}) {
                fixture.Controls.Add(button);IntPtr buttonHandle=button.Handle;
                IAccessible accessible=null;Guid iid=new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                try {
                    if(AccessibleObjectFromWindow(buttonHandle,unchecked((uint)-4),ref iid,out accessible)<0||accessible==null
                        ||Convert.ToInt32(accessible.get_accRole(0))!=0x2b||String.IsNullOrWhiteSpace(accessible.get_accDefaultAction(0)))
                        throw new InvalidOperationException("Owned MSAA default-action interface");
                    // Read only. The declared default action is never performed.
                    var cache=TrayCache();cache.Add(InvokePattern.Pattern);var element=AutomationElement.FromHandle(buttonHandle).GetUpdatedCache(cache);
                    if(element.Cached.ProcessId!=pid||element.Cached.ControlType!=ControlType.Button
                        ||!(bool)element.GetCachedPropertyValue(AutomationElement.IsInvokePatternAvailableProperty))
                        throw new InvalidOperationException("Owned UIA notification-property cache");
                    object declaredPattern;if(!element.TryGetCachedPattern(InvokePattern.Pattern,out declaredPattern)||!(declaredPattern is InvokePattern))
                        throw new InvalidOperationException("Owned cached Invoke pattern read-only fixture");
                }finally{if(accessible!=null&&Marshal.IsComObject(accessible))Marshal.ReleaseComObject(accessible);}
                IAccessible parentAccessible=null;
                try {
                    if(AccessibleObjectFromWindow(h,unchecked((uint)-4),ref iid,out parentAccessible)<0||parentAccessible==null)
                        throw new InvalidOperationException("Owned MSAA child container");
                    int count=parentAccessible.accChildCount;
                    if(count<1||count>128)throw new InvalidOperationException("Owned MSAA child count");
                    var children=new object[count];int obtained;
                    if(AccessibleChildren(parentAccessible,0,count,children,out obtained)<0||obtained<1)
                        throw new InvalidOperationException("Owned MSAA VARIANT child enumeration");
                    foreach(object child in children)if(child!=null&&Marshal.IsComObject(child))Marshal.ReleaseComObject(child);
                }finally{if(parentAccessible!=null&&Marshal.IsComObject(parentAccessible))Marshal.ReleaseComObject(parentAccessible);}
            }
        }
        File.WriteAllText(report,"PASS: live finite positive bounds permit geometry changes while exact PID/runtime/label/state/pattern remain required; fixed live rejection reasons; owned fresh cached Invoke pattern is read-only; UIA-first complete/unique selection and legacy complete-zero fallback; partial/ambiguous representations refused; diagnostic error reset/history; empty legacy/PID-zero XAML discovery; task-list/foreign-PID isolation; runtime/scope/depth deduplication; overlapping distinct controls retain ambiguity; bounded incomplete scans refused; exact labels; owned MSAA/UIA fixtures; request expiry/cancellation; QQ login exclusion at 96/144/192 DPI. No existing application or Explorer icon was shown, invoked, relaunched, styled, enabled or focused. Real animation/default action/interaction pending user trial.");
    }
    static long GetLong(IntPtr h,int index){return IntPtr.Size==8?GetWindowLongPtr(h,index).ToInt64():GetWindowLong(h,index);}
    delegate bool EnumProc(IntPtr h,IntPtr l);
    [StructLayout(LayoutKind.Sequential)]struct Point{internal int X,Y;}
    [StructLayout(LayoutKind.Sequential)]struct Rect{internal int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]struct Placement{internal int Length,Flags,Show;internal Point Min,Max;internal Rect Normal;}
    [DllImport("oleacc.dll")]static extern int AccessibleObjectFromWindow(IntPtr window,uint objectId,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IAccessible accessible);
    [DllImport("oleacc.dll")]static extern int AccessibleChildren(IAccessible container,int start,int count,[Out,MarshalAs(UnmanagedType.LPArray,ArraySubType=UnmanagedType.Struct,SizeParamIndex=2)]object[] children,out int obtained);
    [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc callback,IntPtr l);
    [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr parent,EnumProc callback,IntPtr l);
    [DllImport("user32.dll")]static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")]static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll")]static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr h,uint relation);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder text,int count);
    [DllImport("user32.dll")]static extern bool GetWindowPlacement(IntPtr h,ref Placement placement);
    [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr h,out Rect rectangle);
    [DllImport("user32.dll")]static extern IntPtr MonitorFromRect(ref Rect rectangle,uint flags);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern int GetWindowLong(IntPtr h,int index);
    [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr h,int attr,out int value,int size);
    [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
}
