using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using StageManager.Native;
using StageManager.Native.PInvoke;
namespace StageManager.Services;
internal static class WindowStateDiagnostics
{
    internal static void Run(string processName,string output)
    {
        var rows=new List<object>();
        EnumWindows((h,_)=> {
            try {
                Win32.GetWindowThreadProcessId(h,out var pid);
                using var process=Process.GetProcessById((int)pid);
                if(!process.ProcessName.Equals(processName,StringComparison.OrdinalIgnoreCase))return true;
                var w=new WindowsWindow(h);var r=w.Location;
                rows.Add(new {Handle=h.ToInt64(),ProcessId=pid,Class=w.Class,HasTitle=w.Title.Length>0,
                    IsQQMainCaption=w.Title.Trim()=="QQ",QQMainEligible=QqWindowPolicy.IsMain(w),Visible=Win32.IsWindowVisible(h),Minimized=w.IsMinimized,Maximized=w.IsMaximized,
                    Integrity=WindowIntegrity.Read((int)pid),CanLayout=w.CanLayout,Owner=Win32.GetWindow(h,Win32.GW.GW_OWNER).ToInt64(),
                    ExStyle=((long)Win32.GetWindowExStyleLongPtr(h)).ToString("X"),
                    Style=((long)Win32.GetWindowStyleLongPtr(h)).ToString("X"),r.X,r.Y,r.Width,r.Height});
            }catch{} return true;
        },IntPtr.Zero);
        File.WriteAllText(output,JsonSerializer.Serialize(new {Utc=DateTime.UtcNow,Process=processName,CallerIntegrity=WindowIntegrity.Read(Environment.ProcessId),Windows=rows},new JsonSerializerOptions{WriteIndented=true}));
    }
    private delegate bool EnumProc(IntPtr h,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumProc proc,IntPtr data);
}
