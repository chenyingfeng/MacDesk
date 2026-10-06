using System;
using System.Windows;
namespace StageManager.Services;
internal static class ThumbnailViewport
{
    internal readonly record struct Mapping(Rect Destination,Rect Source);
    internal static Mapping Map(Rect full,Rect viewport,Size source) {
        if(full.IsEmpty || viewport.IsEmpty || full.Width<=0 || full.Height<=0 || source.Width<=0 || source.Height<=0)
            return new Mapping(Rect.Empty,Rect.Empty);
        var visible=Rect.Intersect(full,viewport);
        if(visible.IsEmpty || visible.Width<1 || visible.Height<1)return new Mapping(Rect.Empty,Rect.Empty);
        var crop=new Rect((visible.Left-full.Left)/full.Width*source.Width,
            (visible.Top-full.Top)/full.Height*source.Height,
            visible.Width/full.Width*source.Width,visible.Height/full.Height*source.Height);
        return new Mapping(visible,crop);
    }
    internal static double ScrollOffset(double current,double delta,double extent,double viewport)
        =>Math.Clamp(current-delta*0.6,0,Math.Max(0,extent-viewport));
}
