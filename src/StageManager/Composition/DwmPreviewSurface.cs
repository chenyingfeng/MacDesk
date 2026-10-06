using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using StageManager.Native.PInvoke;
using StageManager.Services;
namespace StageManager.Composition;

// DWM owns rendering and source lifetime. There are no frame pools, GPU locks or capture consent.
internal sealed class DwmPreviewSurface : IDisposable
{
    private HwndSource? _host;
    private IntPtr _source,_thumbnail;
    private bool _shown,_disposed;
    private Rect _lastDestination=Rect.Empty,_lastCrop=Rect.Empty;
    private byte _lastOpacity;
    private Guid _hostDesktop;
    private readonly IntPtr _owner;
    private readonly PreviewPointerInput? _input;
    private bool _pointerCaptured;
    internal static int ActiveRelations {get;private set;}
    internal IntPtr HostHandle => _host?.Handle ?? IntPtr.Zero; // Own-window lifecycle checks only.
    internal DwmPreviewSurface(IntPtr owner,PreviewPointerInput? input=null) {_owner=owner;_input=input;}
    internal void Bind(IntPtr source) {
        if(_source==source)return;
        ReleaseThumbnail();_source=source;
    }
    private void EnsureHost() {
        if(_host!=null)return;
        var p=new HwndSourceParameters("台前调度 · 窗口预览") {
            // Disabled popup previews swallow input rather than passing it to an owned layered WPF window.
            // Sidebar previews receive native input and relay it only to their same-thread owner.
            // Drag ghosts remain disabled and hit-transparent.
            WindowStyle=unchecked((int)(_input!=null ? 0x80000000u : 0x88000000u)),
            ExtendedWindowStyle=_input!=null ? 0x08000080 : 0x080000A0,
            ParentWindow=_owner,PositionX=-20000,PositionY=-20000,Width=1,Height=1
        };
        _host=new HwndSource(p);
        _host.CompositionTarget.BackgroundColor=Colors.Transparent;
        _host.AddHook(Message);
        var margins=new Margins {Left=-1,Right=-1,Top=-1,Bottom=-1};
        DwmExtendFrameIntoClientArea(_host.Handle,ref margins);
    }
    private IntPtr Message(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled) {
        if(message==0x84){handled=true;return new IntPtr(_input!=null ? 1 : -1);} // HTCLIENT / HTTRANSPARENT
        if(message==0x21){handled=true;return new IntPtr(3);} // MA_NOACTIVATE
        if(_input!=null && (message==0x215 || message==0x1F)) {
            if(_pointerCaptured) CancelPointer();
        }
        if(_input!=null && (message==0x200 || message==0x201 || message==0x203 || message==0x202 || message==0x20A)) {
            handled=true;
            try {
                long packed=l.ToInt64();
                var point=new PointNative {X=unchecked((short)(packed&0xffff)),Y=unchecked((short)((packed>>16)&0xffff))};
                if(message!=0x20A && !ClientToScreen(h,ref point))return IntPtr.Zero;
                var screen=new Point(point.X,point.Y);
                switch(message) {
                    case 0x201: case 0x203:
                        if(_input.Down?.Invoke(screen)==true) {
                            SetCapture(h);_pointerCaptured=GetCapture()==h;
                            if(!_pointerCaptured)_input.Cancel?.Invoke();
                        }
                        break;
                    case 0x200: _input.Move?.Invoke(screen,(w.ToInt64()&1)!=0);break;
                    case 0x202:
                        // Release before activation may hide/unload this host. Expected capture loss is not cancellation.
                        ReleasePointer();_input.Up?.Invoke(screen);break;
                    case 0x20A:
                        CancelPointer();_input.Wheel?.Invoke(unchecked((short)((w.ToInt64()>>16)&0xffff)));break;
                }
            } catch(Exception ex) {
                CancelPointer();Log.Info("PREVIEWINPUT","Pointer operation failed: "+ex.GetType().Name);
            }
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }
    private void ReleasePointer() {
        bool release=_pointerCaptured && _host!=null && GetCapture()==_host.Handle;
        _pointerCaptured=false;
        if(release)ReleaseCapture();
    }
    internal void ReleaseInputCapture() => ReleasePointer();
    private void CancelPointer() {
        bool wasCaptured=_pointerCaptured;
        ReleasePointer();
        if(wasCaptured)_input?.Cancel?.Invoke();
    }
    internal bool Update(Rect fullPixels,Rect viewportPixels,byte opacity=255) {
        if(_disposed)return false;
        if(!Win32.IsWindow(_source) || !Win32.IsWindowVisible(_source) || Win32.IsIconic(_source)
            ||WorkspaceEnvironment.IsCloaked(_source)) {
            Hide();ReleaseThumbnail();return false;
        }
        if(Rect.Intersect(fullPixels,viewportPixels).IsEmpty) {Hide();return false;}
        EnsureHost();
        if(_hostDesktop!=WorkspaceEnvironment.CurrentDesktop) {
            WorkspaceEnvironment.AttachOwnPreview(_host!.Handle);_hostDesktop=WorkspaceEnvironment.CurrentDesktop;
        }
        if(_thumbnail==IntPtr.Zero) {
            if(DwmRegisterThumbnail(_host!.Handle,_source,out _thumbnail)!=0 || _thumbnail==IntPtr.Zero) {
                _thumbnail=IntPtr.Zero;Hide();return false;
            }
            ActiveRelations++;_lastDestination=Rect.Empty;
        }
        if(DwmQueryThumbnailSourceSize(_thumbnail,out var size)!=0 || size.Width<=0 || size.Height<=0) {
            Hide();ReleaseThumbnail();return false;
        }
        var mapping=ThumbnailViewport.Map(fullPixels,viewportPixels,new Size(size.Width,size.Height));
        if(mapping.Destination.IsEmpty) {Hide();return false;}
        var destination=mapping.Destination;
        if(_shown && _lastDestination==destination && _lastCrop==mapping.Source && _lastOpacity==opacity)return true;
        var props=new Properties {
            Flags=0x1F,Destination=new NativeRect(0,0,(int)Math.Ceiling(destination.Width),(int)Math.Ceiling(destination.Height)),
            Source=new NativeRect((int)mapping.Source.Left,(int)mapping.Source.Top,
                (int)Math.Ceiling(mapping.Source.Right),(int)Math.Ceiling(mapping.Source.Bottom)),
            Opacity=opacity,Visible=true,ClientOnly=false
        };
        if(DwmUpdateThumbnailProperties(_thumbnail,ref props)!=0) {Hide();ReleaseThumbnail();return false;}
        Win32.SetWindowPos(_host!.Handle,new IntPtr(-1),(int)Math.Floor(destination.X),(int)Math.Floor(destination.Y),
            props.Destination.Right,props.Destination.Bottom,
            Win32.SetWindowPosFlags.DoNotActivate | (Win32.SetWindowPosFlags)0x40);
        _lastDestination=destination;_lastCrop=mapping.Source;_lastOpacity=opacity;_shown=true;
        return true;
    }
    internal void Hide() {
        CancelPointer();
        if(!_shown)return;
        if(_host!=null)Win32.ShowWindowAsync(_host.Handle,Win32.SW.SW_HIDE);
        _shown=false;
    }
    private void ReleaseThumbnail() {
        if(_thumbnail==IntPtr.Zero)return;
        DwmUnregisterThumbnail(_thumbnail);_thumbnail=IntPtr.Zero;
        ActiveRelations=Math.Max(0,ActiveRelations-1);_lastDestination=Rect.Empty;
    }
    public void Dispose() {
        if(_disposed)return;_disposed=true;Hide();ReleaseThumbnail();
        if(_host!=null){_host.RemoveHook(Message);_host.Dispose();_host=null;}
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect {
        internal int Left,Top,Right,Bottom;
        internal NativeRect(int left,int top,int right,int bottom){Left=left;Top=top;Right=right;Bottom=bottom;}
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeSize {internal int Width,Height;}
    [StructLayout(LayoutKind.Sequential)] private struct PointNative {internal int X,Y;}
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h,ref PointNative point);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [StructLayout(LayoutKind.Sequential)] internal struct Margins {internal int Left,Right,Top,Bottom;}
    [StructLayout(LayoutKind.Sequential)] internal struct Properties {
        internal uint Flags;internal NativeRect Destination,Source;internal byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] internal bool Visible;
        [MarshalAs(UnmanagedType.Bool)] internal bool ClientOnly;
    }
    [DllImport("dwmapi.dll")] internal static extern int DwmRegisterThumbnail(IntPtr destination,IntPtr source,out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] internal static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] internal static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail,out NativeSize size);
    [DllImport("dwmapi.dll")] internal static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail,ref Properties props);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr h,ref Margins margins);
}
