using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using StageManager.Composition;
using StageManager.Native.Window;
using StageManager.Strategies;
namespace StageManager.Services;
internal sealed class WindowMotion : IDisposable
{
    private readonly List<(DwmPreviewSurface Preview,Rect Start,Rect End,bool Incoming)> cards=new();
    internal static Rect Frame(Rect start,Rect end,double t) {
        t=Math.Clamp(t,0,1);double ease=1-Math.Pow(1-t,3);
        return new Rect(start.X+(end.X-start.X)*ease,start.Y+(end.Y-start.Y)*ease,
            Math.Max(1,start.Width+(end.Width-start.Width)*ease),Math.Max(1,start.Height+(end.Height-start.Height)*ease));
    }
    internal WindowMotion(IWindow[] outgoing,IWindow[] incoming,string monitor)
    {
        if(PortablePreferences.Read("disable-window-motion")||!SystemParameters.ClientAreaAnimation)return;
        var main=Application.Current?.MainWindow;if(main==null)return;
        var bounds=WorkspaceEnvironment.ScreenFor(monitor).WorkingArea;
        var viewport=new Rect(bounds.X,bounds.Y,bounds.Width,bounds.Height);
        bool Add(IWindow w,bool arriving,int ordinal) {
            if(w.IsMinimized||!WorkspaceEnvironment.IsCurrent(w.Handle)||NativeClientWindowPolicy.UsesNativeMinimize(w))return false;
            var full=WorkspaceWindowGeometry.Bounds(w);bool parked=OpacityWindowStrategy.TryGetOriginalPosition(w.Handle,out int x,out int y);
            if(arriving!=parked)return false;
            if(parked)full=new Rect(x,y,full.Width,full.Height);
            if(Rect.Intersect(full,viewport).IsEmpty)return false;
            double scale=MainWindow.GetMonitorScale(WorkspaceEnvironment.ScreenFor(monitor));
            var fit=Model.SceneModel.MacCardSize(full.Width/scale,full.Height/scale);
            fit=(fit.Width*scale,fit.Height*scale);
            var small=new Rect(bounds.Right-fit.Width-18,Math.Clamp(bounds.Top+50+ordinal*80,bounds.Top,bounds.Bottom-fit.Height),fit.Width,fit.Height);
            var preview=new DwmPreviewSurface(new WindowInteropHelper(main).Handle);preview.Bind(w.Handle);
            cards.Add((preview,arriving?small:full,arriving?full:small,arriving));
            return true;
        }
        try {
            int added=0;foreach(var w in outgoing){if(added==2)break;if(Add(w,false,cards.Count))added++;}
            added=0;foreach(var w in incoming){if(added==2)break;if(Add(w,true,cards.Count))added++;}
        }catch(Exception ex){Log.Info("MOTION","Optional preview unavailable: "+ex.GetType().Name);Dispose();}
    }
    internal async Task PlayAsync(Func<bool> interrupted)
    {
        if(cards.Count==0)return;
        var screen=System.Windows.Forms.SystemInformation.VirtualScreen;
        var viewport=new Rect(screen.X,screen.Y,screen.Width,screen.Height);
        var clock=Stopwatch.StartNew();
        try { while(clock.ElapsedMilliseconds<170) {
            if(interrupted())return;
            double t=clock.Elapsed.TotalMilliseconds/170;
            foreach(var c in cards)c.Preview.Update(Frame(c.Start,c.End,t),viewport,(byte)(c.Incoming?255:Math.Clamp(255*(1-t*.65),0,255)));
            await Task.Delay(16);
        }}catch(Exception ex){Log.Info("MOTION","Optional preview interrupted: "+ex.GetType().Name);}
    }
    public void Dispose(){foreach(var c in cards)c.Preview.Dispose();cards.Clear();}
}
