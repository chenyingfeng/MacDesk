using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using StageManager.Services;

// One STA worker keeps slow ShellExecute/default-browser handoffs off the WPF thread.
// All requests are local, explicit link clicks; it neither reads mail nor logs URLs.
class DockLinkLauncher : IDisposable
{
    class Work { internal string Url, StageDirectory, Marker; internal Action<Exception> Complete; }
    readonly BlockingCollection<Work> queue = new BlockingCollection<Work>();
    readonly Thread worker;
    internal volatile string DefaultBrowser = "";
    internal DockLinkLauncher()
    {
        worker = new Thread(Run);
        worker.Name = "QuickCenter web links";
        worker.IsBackground = true;
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }
    internal void Open(string url, string stageDirectory, string marker, Action<Exception> complete)
    {
        queue.Add(new Work {Url=url, StageDirectory=stageDirectory, Marker=marker, Complete=complete});
    }
    void Run()
    {
        DefaultBrowser = BrowserActivationIntent.DefaultBrowser();
        foreach (Work request in queue.GetConsumingEnumerable())
        {
            Exception failure = null;
            try
            {
                try { BrowserActivationIntent.Write(request.StageDirectory, request.Marker); } catch { }
                Process.Start(new ProcessStartInfo(request.Url) { UseShellExecute = true });
            }
            catch (Exception e) { failure = e; }
            try { request.Complete(failure); } catch { }
        }
    }
    public void Dispose() { queue.CompleteAdding(); }
}

class LinkClickPolicy
{
    string last = "";
    long lastTicks;
    internal bool Accept(string url, long ticks)
    {
        if (url == last && ticks >= lastTicks && ticks-lastTicks < 500 * TimeSpan.TicksPerMillisecond) return false;
        last=url; lastTicks=ticks; return true;
    }
}
