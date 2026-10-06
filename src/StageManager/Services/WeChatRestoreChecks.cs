using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Strategies;

namespace StageManager.Services;

internal static class WeChatRestoreChecks
{
    internal static async Task RunAsync()
    {
        foreach(uint dpi in new uint[]{96,144,192}) {
            double scale=dpi/96.0;
            if(!NativeClientWindowPolicy.IsWeChatMain("Weixin","Qt51514QWindowIcon","微信",true,true,900*scale,650*scale,dpi)
                || !NativeClientWindowPolicy.IsWeChatMain("WeChat","WeChatMainWndForPC","WeChat",true,true,800*scale,600*scale,dpi)
                || NativeClientWindowPolicy.IsWeChatMain("Other","Qt51514QWindowIcon","微信",true,true,900*scale,650*scale,dpi)
                || NativeClientWindowPolicy.IsWeChatMain("Weixin","Qt51514QWindowIcon","登录",true,true,900*scale,650*scale,dpi)
                || NativeClientWindowPolicy.IsWeChatMain("Weixin","Qt51514QWindowIcon","微信",true,true,320*scale,460*scale,dpi)
                || NativeClientWindowPolicy.IsWeChatMain("Weixin","Qt51514QWindowIcon","微信",true,true,480*scale,800*scale,dpi)
                || NativeClientWindowPolicy.IsWeChatMain("Weixin","QtOtherQWindowIcon","微信",true,true,900*scale,650*scale,dpi)
                || NativeClientWindowPolicy.IsWeChatMain("Weixin","Qt51514QWindowIcon","微信",false,true,900*scale,650*scale,dpi)
                || NativeClientWindowPolicy.IsWeChatMain("Weixin","Qt51514QWindowIcon","微信",true,false,900*scale,650*scale,dpi))
                throw new InvalidOperationException("WeChat main/login/helper classification across DPI");
        }
        var parameters=new HwndSourceParameters("CampusStage owned WeChat restore fixture") {
            WindowStyle=unchecked((int)0x90CF0000),ExtendedWindowStyle=0,
            PositionX=-20000,PositionY=-20000,Width=900,Height=650};
        using var fixture=new HwndSource(parameters);
        fixture.RootVisual=new Border();
        var native=new WindowsWindow(fixture.Handle);
        var messenger=new WeChatAlias(native);
        uint fixtureDpi=GetDpiForWindow(fixture.Handle);if(fixtureDpi==0)fixtureDpi=96;
        Win32.SetWindowPos(fixture.Handle,IntPtr.Zero,-20000,-20000,(int)(900*fixtureDpi/96),(int)(650*fixtureDpi/96),
            Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate);
        var before=native.Location;
        var ex=Win32.GetWindowExStyleLongPtr(fixture.Handle);
        int restores=0,minimizes=0;
        fixture.AddHook((IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled)=> {
            if(message==Win32.WM_SYSCOMMAND) {
                long command=w.ToInt64()&0xFFF0;
                if(command==0xF120)restores++;
                if(command==0xF020)minimizes++;
            }
            return IntPtr.Zero;
        });
        if(!NativeClientWindowPolicy.UsesNativeMinimize(messenger))throw new InvalidOperationException("Visible WeChat main not classified");
        await WindowRestore.RunAsync(messenger,true);
        if(restores!=0)throw new InvalidOperationException("Visible normal WeChat received forced restore");
        var strategy=new OpacityWindowStrategy();
        for(int cycle=0;cycle<4;cycle++) {
            strategy.Hide(messenger);strategy.Hide(messenger);
            for(int i=0;i<80 && !native.IsMinimized;i++)await Task.Delay(25);
            if(!native.IsMinimized || !OpacityWindowStrategy.IsStageMinimized(fixture.Handle)
                || OpacityWindowStrategy.TryGetOriginalPosition(fixture.Handle,out _,out _))
                throw new InvalidOperationException("WeChat main parked instead of native-minimized");
            strategy.Show(messenger);
            for(int i=0;i<80 && native.IsMinimized;i++)await Task.Delay(25);
            var after=native.Location;
            if(native.IsMinimized || before.X!=after.X || before.Y!=after.Y
                || before.Width!=after.Width || before.Height!=after.Height
                || ex!=Win32.GetWindowExStyleLongPtr(fixture.Handle))
                throw new InvalidOperationException("WeChat native restore changed geometry/styles");
        }
        if(minimizes!=4 || restores!=4)throw new InvalidOperationException("Duplicate WeChat native command ownership");
        Win32.ShowWindow(fixture.Handle,Win32.SW.SW_SHOWMINNOACTIVE);
        strategy.Hide(messenger);strategy.Show(messenger);
        if(!native.IsMinimized || OpacityWindowStrategy.IsStageMinimized(fixture.Handle))
            throw new InvalidOperationException("Stage restored user-owned WeChat minimize");
        await WindowRestore.RunAsync(messenger,true);
        strategy.Hide(messenger);
        for(int i=0;i<80 && !native.IsMinimized;i++)await Task.Delay(25);
        Win32.ShowWindow(fixture.Handle,Win32.SW.SW_HIDE);
        strategy.Show(messenger);
        await Task.Delay(50);
        bool refused=false;
        try{await WindowRestore.RunAsync(messenger,true);}catch(InvalidOperationException){refused=true;}
        if(!refused || Win32.IsWindowVisible(fixture.Handle) || OpacityWindowStrategy.IsStageMinimized(fixture.Handle)
            || NativeClientWindowPolicy.IsWeChatMain(messenger))
            throw new InvalidOperationException("Hidden WeChat renderer restored, retained or left stage-owned");
        OpacityWindowStrategy.CleanupWindow(fixture.Handle);
        fixture.Dispose();
        refused=false;
        try{await WindowRestore.RunAsync(messenger,true);}catch(InvalidOperationException){refused=true;}
        if(!refused)throw new InvalidOperationException("Destroyed WeChat fixture restored");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    // Alias only this process's off-screen fixture. No messenger process is queried.
    private sealed class WeChatAlias(IWindow inner):IWindow
    {
        public IntPtr Handle=>inner.Handle;public string Title=>"微信";public string Class=>"Qt51514QWindowIcon";
        public int ProcessId=>inner.ProcessId;public string ProcessFileName=>"Weixin.exe";public string ProcessName=>"Weixin";
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
