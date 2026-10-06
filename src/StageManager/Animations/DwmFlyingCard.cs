using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using StageManager.Composition;
namespace StageManager.Animations;
internal sealed class DwmFlyingCard : IFlyingCard
{
    private readonly DwmPreviewSurface _preview;
    private readonly Point _dpi;
    private Rect _last=Rect.Empty;
    private bool _visible=true;
    private readonly BorderCard _fallback;
    internal DwmFlyingCard(IntPtr source,Point dpi,TransitionOverlayWindow overlay,ImageSource? icon) {
        _dpi=dpi;
        _fallback=new BorderCard(overlay,icon);
        _preview=new DwmPreviewSurface(new WindowInteropHelper(Application.Current.MainWindow).Handle);
        _preview.Bind(source);
    }
    public bool HasContent {get;private set;}
    public void Update(Rect rect,double skewDegrees) {
        _last=rect;
        if(!_visible){_preview.Hide();_fallback.SetVisible(false);return;}
        var vs=System.Windows.Forms.SystemInformation.VirtualScreen;
        bool live=_preview.Update(new Rect(rect.X*_dpi.X,rect.Y*_dpi.Y,rect.Width*_dpi.X,rect.Height*_dpi.Y),
            new Rect(vs.X,vs.Y,vs.Width,vs.Height));
        _fallback.Update(rect,0);_fallback.SetVisible(!live);HasContent=live || _fallback.HasContent;
    }
    public void SetVisible(bool value) {_visible=value;if(value && !_last.IsEmpty)Update(_last,0);else {_preview.Hide();_fallback.SetVisible(false);}}
    public void Release(){_preview.Dispose();_fallback.Release();}
}
