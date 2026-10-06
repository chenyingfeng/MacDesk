using System;
using StageManager.Controls;

namespace StageManager.Services;
internal static class InputHandoffChecks
{
    internal static void Run()
    {
        long now=638000000000000000;
        string Marker(string target,long time,string kind="browser") => Guid.NewGuid().ToString("N")+"|"+kind+"|"+target+"|"+time;
        var browser=new BrowserActivationIntent();
        var first=Marker("msedge",now);
        browser.Prime(first);browser.Receive(first,now);
        if(browser.HasPending || browser.AcceptedRequests!=0) throw new InvalidOperationException("Startup replayed an old dock link.");
        var valid=Marker("msedge",now);
        browser.Receive(valid,now);
        browser.Receive(valid,now+1);
        if(browser.AcceptedRequests!=1 || browser.TryConsume("chrome",true,now)
            || browser.TryConsume("msedge",false,now) || !browser.TryConsume("msedge.exe",true,now)
            || browser.TryConsume("msedge",true,now+1)) throw new InvalidOperationException("Browser intent target/lifetime/deduplication failed.");
        browser.Receive(Marker("msedge",now),now);
        if(browser.TryConsumeDefault("msedge",true,now+600*TimeSpan.TicksPerMillisecond)
            || browser.TryConsumeDefault("chrome",true,now+800*TimeSpan.TicksPerMillisecond)
            || !browser.TryConsumeDefault("msedge",true,now+800*TimeSpan.TicksPerMillisecond))
            throw new InvalidOperationException("Default-browser no-foreground handoff failed.");
        browser.Receive(Marker("msedge",now),now);browser.Cancel();
        if(browser.TryConsumeDefault("msedge",true,now+TimeSpan.TicksPerSecond)) throw new InvalidOperationException("New input did not cancel an older browser intent.");
        browser.Receive(Marker("msedge",now),now);
        if(browser.TryConsume("msedge",true,now+6*TimeSpan.TicksPerSecond)) throw new InvalidOperationException("Expired browser intent restored a window.");
        browser.Receive(Marker("",now),now);
        if(browser.TryConsumeDefault("msedge",true,now+TimeSpan.TicksPerSecond)
            || browser.TryConsume("Clash-Verge",true,now) || !browser.TryConsume("msedge",true,now))
            throw new InvalidOperationException("Unknown default browser activated an unrelated window.");
        foreach(var bad in new[]{"invalid",Marker("msedge",now-6*TimeSpan.TicksPerSecond),
            Marker("msedge",now+2*TimeSpan.TicksPerSecond),Marker("msedge",now,"arbitrary"),Marker("Clash-Verge",now)}) {
            browser.Receive(bad,now);
            if(browser.HasPending)throw new InvalidOperationException("Malformed/stale/unsafe dock marker accepted.");
        }
        var a=new IntPtr(1);var b=new IntPtr(2);var unrelated=new IntPtr(3);var target=new[]{b};
        if(!FocusHandoff.MayFinish(a,a,target,1,1,1,1,false)
            || !FocusHandoff.MayFinish(a,b,target,1,1,1,1,false)
            || FocusHandoff.MayFinish(a,unrelated,target,1,1,1,1,false)
            || FocusHandoff.MayFinish(a,a,target,1,2,1,1,false)
            || FocusHandoff.MayFinish(a,a,target,1,1,1,2,false)
            || FocusHandoff.MayFinish(a,a,target,1,1,1,1,true))
            throw new InvalidOperationException("Delayed focus overwrote newer input/system navigation.");
        foreach(var name in new[]{"StageManager.Composition.CaptureSession","StageManager.Composition.CaptureBorder",
            "StageManager.Animations.LiveCardHost","StageManager.Animations.DebugZoneOverlay"})
            if(typeof(CompositionThumbnail).Assembly.GetType(name)!=null)
                throw new InvalidOperationException("Obsolete capture/debug code remains in the executable.");
    }
}
