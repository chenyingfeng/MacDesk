using System;
using System.ComponentModel;
using System.IO;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Strategies;

namespace StageManager.Services
{
    internal static class WindowOperationReport
    {
        // Called only inside user-requested app operations. No titles, contents,
        // executable paths or credentials are written.
        internal static void Write(string operation,IWindow window,Exception? error)
        {
            try
            {
                var handle=window.Handle;
                var rect=new Win32.Rect();Win32.GetWindowRect(handle,ref rect);
                File.WriteAllText(Path.Combine(PortablePreferences.Root,"operation-status.ini"),
                    "Operation="+operation+"\nApp="+window.ProcessFileName
                    +"\nValid="+Win32.IsWindow(handle)+"\nVisible="+Win32.IsWindowVisible(handle)
                    +"\nMinimized="+Win32.IsIconic(handle)+"\nMaximized="+Win32.IsZoomed(handle)
                    +"\nExtendedStyle="+((long)Win32.GetWindowExStyleLongPtr(handle)).ToString("X")
                    +"\nParked="+OpacityWindowStrategy.TryGetOriginalPosition(handle,out _,out _)
                    +"\nX="+rect.Left+"\nY="+rect.Top
                    +"\nCallerIntegrity="+(WindowIntegrity.Read(Environment.ProcessId)?.ToString()??"Unknown")
                    +"\nTargetIntegrity="+(WindowIntegrity.Read(window.ProcessId)?.ToString()??"Unknown")
                    +"\nNativeError="+(error is Win32Exception native?native.NativeErrorCode:0)
                    +"\nErrorType="+(error?.GetType().Name??"None")
                    +"\nUtc="+DateTime.UtcNow.ToString("o")+"\n");
            }
            catch { }
        }
    }
}
