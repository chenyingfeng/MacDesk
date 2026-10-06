using System;
using System.Runtime.InteropServices;
namespace StageManager.Services;
// Read-only mandatory integrity RID. No account SID, privileges or token changes.
internal static class WindowIntegrity
{
    private static readonly uint? Caller=Read(Environment.ProcessId);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int,(long At,uint? Rid)> Cache=new();
    internal static bool MayControl(uint? caller,uint? target)=>!caller.HasValue || !target.HasValue || caller.Value>=target.Value;
    internal static bool CanControl(int processId)
    {
        long now=Environment.TickCount64;
        if(!Cache.TryGetValue(processId,out var entry) || now-entry.At>1000) {
            entry=(now,Read(processId));Cache[processId]=entry;
        }
        return MayControl(Caller,entry.Rid);
    }
    internal static void RequireControl(int processId)
    {
        if(!MayControl(Caller,Read(processId)))
            throw new System.ComponentModel.Win32Exception(5,"应用以更高权限运行，请使用管理员模式启动台前调度，或从应用托盘打开。");
    }
    internal static uint? Read(int processId)
    {
        IntPtr process=OpenProcess(0x1000,false,processId),token=IntPtr.Zero,buffer=IntPtr.Zero;
        try {
            if(process==IntPtr.Zero || !OpenProcessToken(process,0x8,out token))return null;
            GetTokenInformation(token,25,IntPtr.Zero,0,out uint length);
            if(length==0 || length>65536)return null;
            buffer=Marshal.AllocHGlobal((int)length);
            if(!GetTokenInformation(token,25,buffer,length,out _))return null;
            IntPtr sid=Marshal.ReadIntPtr(buffer),count=GetSidSubAuthorityCount(sid);
            if(count==IntPtr.Zero)return null;
            byte n=Marshal.ReadByte(count);if(n==0)return null;
            IntPtr rid=GetSidSubAuthority(sid,(uint)(n-1));
            return rid==IntPtr.Zero?null:(uint)Marshal.ReadInt32(rid);
        } finally {
            if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);
            if(token!=IntPtr.Zero)CloseHandle(token);
            if(process!=IntPtr.Zero)CloseHandle(process);
        }
    }
    [DllImport("kernel32.dll")]private static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll")]private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll")]private static extern bool GetTokenInformation(IntPtr token,int cls,IntPtr data,uint length,out uint required);
    [DllImport("advapi32.dll")]private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")]private static extern IntPtr GetSidSubAuthority(IntPtr sid,uint index);
}
