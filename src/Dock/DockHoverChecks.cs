using System;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal sealed class DockMeasurePanel : StackPanel {
    internal int Measures,Arranges;
    protected override Size MeasureOverride(Size available){Measures++;return base.MeasureOverride(available);}
    protected override Size ArrangeOverride(Size final){Arranges++;return base.ArrangeOverride(final);}
}
internal sealed partial class MacBottomDock {
    // Own unshown WPF visuals only. Exercises the actual hover event and frame code.
    internal static void CheckHover(string report) {
        var dock=new MacBottomDock(AppDomain.CurrentDomain.BaseDirectory,delegate{},delegate{},true);
        try {
            // Detach from the never-shown Window, whose visibility suppresses layout
            // invalidation. The visible-in-memory root now measures like an active tree.
            dock.Content=null;
            dock.RenderPreview(report+".png");
            var panel=(DockMeasurePanel)dock.row;int oldMeasures,oldArranges;
            panel.Measures=panel.Arranges=0;
            for(int frame=0;frame<240;frame++) {
                double pointer=40+(frame%120)*4;
                foreach(var item in dock.visuals) {
                    double center=item.Button.TranslatePoint(new Point(item.Button.ActualWidth/2,0),dock.row).X;
                    item.Button.Width=60+(Magnification(pointer-center)-1)*43;
                }
                dock.surface.Measure(new Size(dock.Width,dock.Height));dock.surface.Arrange(new Rect(0,0,dock.Width,dock.Height));
                dock.surface.UpdateLayout();
            }
            oldMeasures=panel.Measures;oldArranges=panel.Arranges;
            foreach(var item in dock.visuals)item.Button.Width=60;
            dock.surface.Measure(new Size(dock.Width,dock.Height));dock.surface.Arrange(new Rect(0,0,dock.Width,dock.Height));dock.surface.UpdateLayout();
            dock.hoverGeometryDirty=true;dock.StepHover(1.0/60);
            panel.Measures=panel.Arranges=0;
            var stopwatch=Stopwatch.StartNew();
            for(int frame=0;frame<240;frame++) {
                // More than 1,000 mouse samples/second at 60 rendered frames/second.
                for(int sample=0;sample<20;sample++)dock.Magnify(40+(frame%120)*4+sample*.1);
                dock.StepHover(1.0/60);
                dock.surface.Measure(new Size(dock.Width,dock.Height));dock.surface.Arrange(new Rect(0,0,dock.Width,dock.Height));
                dock.surface.UpdateLayout();
                foreach(var item in dock.visuals)if(item.Button.Width!=60||item.Button.HasAnimatedProperties||item.Scale.HasAnimatedProperties||item.Shift.HasAnimatedProperties)
                    throw new InvalidOperationException("Hover changed layout width or created animation clocks");
            }
            stopwatch.Stop();
            int measures=panel.Measures,arranges=panel.Arranges;
            if(oldMeasures<200||oldArranges<200||measures!=0||arranges!=0)throw new InvalidOperationException("Hover layout regression: old="+oldMeasures+"/"+oldArranges+", new="+measures+"/"+arranges);
            dock.Magnify(Double.NaN);bool settled=false;
            for(int i=0;i<120&&!settled;i++)settled=dock.StepHover(1.0/60);
            if(!settled)throw new InvalidOperationException("Hover failed to settle after pointer exit");
            foreach(var item in dock.visuals)if(item.Scale.ScaleX!=1||Math.Abs(item.Shift.X)>.001)throw new InvalidOperationException("Icon did not return to baseline");
            dock.StopHoverRendering();if(dock.renderingHover)throw new InvalidOperationException("Idle render subscription leaked");
            dock.Magnify(300);dock.StepHover(1.0/60);dock.ResetHover();if(dock.renderingHover)throw new InvalidOperationException("Collapse left hover rendering active");
            File.WriteAllText(report,"PASS: actual WPF visual hover path; 4,800 mouse samples coalesced into 240 frame updates; zero width animation clocks; fixed button geometry; pointer exit settles; collapse unsubscribes idle rendering.\nLegacy width changes: "+oldMeasures+" measure / "+oldArranges+" arrange passes.\nNew render transforms: "+measures+" measure / "+arranges+" arrange passes.\nOwn unshown visual processing elapsed: "+stopwatch.Elapsed.TotalMilliseconds.ToString("F2")+" ms (not desktop FPS or GPU timing).\n");
        }finally{dock.ResetHover();dock.iconWorker.Stop();}
    }
}
