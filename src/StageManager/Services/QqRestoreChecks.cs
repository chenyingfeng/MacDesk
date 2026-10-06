using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
namespace StageManager.Services;
internal static class QqRestoreChecks
{
    internal static async Task RunAsync()
    {
        if(!QqWindowPolicy.IsMain("QQ","Chrome_WidgetWin_1","QQ",true,true,1000,700)
            || QqWindowPolicy.IsMain("QQ","Chrome_WidgetWin_1","Chat fixture",true,true,1000,700)
            || QqWindowPolicy.IsMain("QQ","Chrome_WidgetWin_1","QQ",true,true,70,56)
            || QqWindowPolicy.IsMain("QQ","Chrome_WidgetWin_0","QQ",true,true,1000,700)
            || QqWindowPolicy.IsMain("Other","Chrome_WidgetWin_1","QQ",true,true,1000,700)
            || QqWindowPolicy.IsMain("QQ","Chrome_WidgetWin_1","QQ",false,true,1000,700))
            throw new InvalidOperationException("QQ main/chat/helper classification");
        var p=new HwndSourceParameters("CampusStage hidden restore fixture") {
            WindowStyle=unchecked((int)0x90CF0000),ExtendedWindowStyle=0x08000080,
            PositionX=-20000,PositionY=-20000,Width=900,Height=650};
        using var fixture=new HwndSource(p);
        var native=new WindowsWindow(fixture.Handle);
        var qq=new QqAlias(native);
        var initial=native.Location;
        var ex=Win32.GetWindowExStyleLongPtr(native.Handle);
        Win32.ShowWindow(native.Handle,Win32.SW.SW_HIDE);
        bool refused=false;
        try{await WindowRestore.RunAsync(qq);}catch(InvalidOperationException){refused=true;}
        if(!refused || Win32.IsWindowVisible(native.Handle))throw new InvalidOperationException("Hidden QQ auto-restored without explicit selection");
        refused=false;
        try{await WindowRestore.RunAsync(native,true);}catch(InvalidOperationException){refused=true;}
        if(!refused || Win32.IsWindowVisible(native.Handle))throw new InvalidOperationException("Unrelated hidden app was restored");
        refused=false;
        try{await WindowRestore.RunAsync(qq,true);}catch(InvalidOperationException){refused=true;}
        if(!refused || Win32.IsWindowVisible(native.Handle) || QqWindowPolicy.Retain(qq)
            || QqWindowPolicy.Discover(native.Handle))
            throw new InvalidOperationException("Tray-hidden QQ retained or restored by explicit choice");
        // The fixture simulates a client opening its own real main UI. No launch
        // or visibility change to a real QQ process takes place in these checks.
        Win32.ShowWindow(native.Handle,Win32.SW.SW_SHOWNOACTIVATE);
        if(!QqWindowPolicy.IsLoginSize(320,460,96)
            || !QqWindowPolicy.IsLoginSize(480,690,144)
            || !QqWindowPolicy.IsLoginSize(640,920,192)
            || QqWindowPolicy.IsLoginSize(900,650,144)
            || QqWindowPolicy.IsLoginSize(70,56,96))
            throw new InvalidOperationException("QQ login/main/helper size classification across DPI");
        uint fixtureDpi=GetDpiForWindow(native.Handle);if(fixtureDpi==0)fixtureDpi=96;
        Win32.SetWindowPos(native.Handle,IntPtr.Zero,-20000,-20000,(int)(320*fixtureDpi/96),(int)(460*fixtureDpi/96),
            Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate);
        if(!QqWindowPolicy.IsLoginSurface(qq) || QqWindowPolicy.Retain(qq))
            throw new InvalidOperationException("QQ login window was retained");
        refused=false;
        try{await WindowRestore.RunAsync(qq,true);}catch(InvalidOperationException){refused=true;}
        if(!refused)throw new InvalidOperationException("QQ login restored with main group");
        Win32.SetWindowPos(native.Handle,IntPtr.Zero,initial.X,initial.Y,initial.Width,initial.Height,
            Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate);
        if(QqWindowPolicy.IsLoginSurface(qq))throw new InvalidOperationException("QQ main excluded as login");
        Win32.ShowWindow(native.Handle,Win32.SW.SW_SHOWMINNOACTIVE);
        var size=WindowRestoreGeometry.NormalSize(native.Handle,new Size(1,1));
        if(size.Width!=initial.Width || size.Height!=initial.Height)
            throw new InvalidOperationException("Minimized drag uses wrong normal size");
        await WindowRestore.RunAsync(qq,true);
        await WindowRestore.NormalizeAsync(qq);
        var strategy=new StageManager.Strategies.OpacityWindowStrategy();
        for(int cycle=0;cycle<4;cycle++) {
            var before=native.Location;
            strategy.Hide(qq);
            strategy.Hide(qq); // duplicate stow must not move or enqueue another minimize
            for(int i=0;i<80 && !native.IsMinimized;i++)await Task.Delay(25);
            if(!native.IsMinimized || !StageManager.Strategies.OpacityWindowStrategy.IsStageMinimized(native.Handle)
                || StageManager.Strategies.OpacityWindowStrategy.TryGetOriginalPosition(native.Handle,out _,out _))
                throw new InvalidOperationException("QQ stage stow used parking instead of native minimize");
            strategy.Show(qq); // same path as graceful quit; never restores a user-owned minimize
            for(int i=0;i<80 && native.IsMinimized;i++)await Task.Delay(25);
            var after=native.Location;
            if(native.IsMinimized || before.X!=after.X || before.Y!=after.Y
                || before.Width!=after.Width || before.Height!=after.Height
                || Win32.GetWindowExStyleLongPtr(native.Handle)!=ex)
                throw new InvalidOperationException("QQ system-command restore changed geometry/styles");
        }
        Win32.ShowWindow(native.Handle,Win32.SW.SW_SHOWMINNOACTIVE);
        strategy.Hide(qq);strategy.Show(qq);
        if(!native.IsMinimized || StageManager.Strategies.OpacityWindowStrategy.IsStageMinimized(native.Handle))
            throw new InvalidOperationException("Stage restored a user-owned QQ minimize");
        await WindowRestore.RunAsync(qq,true);
        strategy.Hide(qq);
        for(int i=0;i<80 && !native.IsMinimized;i++)await Task.Delay(25);
        if(!native.IsMinimized)throw new InvalidOperationException("QQ stage minimize did not settle before app close");
        Win32.ShowWindow(native.Handle,Win32.SW.SW_HIDE);
        strategy.Show(qq); // an app that has closed to tray must not be woken on exit
        await Task.Delay(50);
        if(Win32.IsWindowVisible(native.Handle))throw new InvalidOperationException("Stage quit showed tray-hidden QQ");
        StageManager.Strategies.OpacityWindowStrategy.CleanupWindow(native.Handle);
        // Removal after a new wrapper/event must match HWND identity rather than reference.
        var scene=new StageManager.Model.Scene("fixture",native);
        scene.Remove(new WindowsWindow(native.Handle));
        if(System.Linq.Enumerable.Any(scene.Windows))throw new InvalidOperationException("Stale scene window survived wrapper replacement");
        fixture.Dispose();
        refused=false;
        try{await WindowRestore.RunAsync(qq,true);}catch(InvalidOperationException){refused=true;}
        if(!refused)throw new InvalidOperationException("Destroyed QQ window falsely restored");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);
    // Alias only an owned off-screen test HWND; never a user's QQ window.
    private sealed class QqAlias(IWindow inner):IWindow
    {
        public IntPtr Handle=>inner.Handle;public string Title=>"QQ";public string Class=>"Chrome_WidgetWin_1";
        public int ProcessId=>inner.ProcessId;public string ProcessFileName=>"QQ.exe";public string ProcessName=>"QQ";
        public IWindowLocation Location=>inner.Location;public System.Drawing.Rectangle Offset=>inner.Offset;
        public bool CanLayout=>true;public bool IsFocused=>inner.IsFocused;public bool IsMinimized=>inner.IsMinimized;
        public bool IsMaximized=>inner.IsMaximized;public bool IsMouseMoving=>false;
        public event IWindowDelegate? WindowClosed{add{}remove{}}public event IWindowDelegate? WindowUpdated{add{}remove{}}
        public event IWindowDelegate? WindowFocused{add{}remove{}}
        public void Focus()=>inner.Focus();public void Hide()=>inner.Hide();public void ShowNormal()=>inner.ShowNormal();
        public void ShowMaximized()=>inner.ShowMaximized();public void ShowMinimized()=>inner.ShowMinimized();
        public void ShowInCurrentState()=>inner.ShowInCurrentState();public void BringToTop()=>inner.BringToTop();
        public void Close()=>inner.Close();public void NotifyUpdated()=>inner.NotifyUpdated();
    }
}
