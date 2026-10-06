using System;
using System.Windows;
using System.Windows.Interop;
using StageManager.Native.PInvoke;

namespace StageManager.Native;
internal sealed class NonActivatingWindow : IDisposable
{
    private readonly HwndSource source;
    internal NonActivatingWindow(System.Windows.Window window)
    {
        var h = new WindowInteropHelper(window).Handle;
        Win32.SetWindowStyleExLongPtr(h, Win32.GetWindowExStyleLongPtr(h) | Win32.WS_EX.WS_EX_NOACTIVATE);
        source = HwndSource.FromHwnd(h)!;
        source.AddHook(Message);
    }
    private static IntPtr Message(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE, without eating the click.
        return IntPtr.Zero;
    }
    public void Dispose() => source.RemoveHook(Message);
}
