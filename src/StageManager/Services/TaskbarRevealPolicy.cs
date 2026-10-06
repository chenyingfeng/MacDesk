namespace StageManager.Services;
// A peek never edits Explorer's auto-hide settings and never stops the restore watcher.
internal sealed class TaskbarRevealPolicy
{
    private long _edgeSince=-1;
    private long _until;
    private bool _latched,_dismissed;
    internal void Request(long now) {
        // Recovery requests mean "show", so retries cannot cancel one another like toggles.
        // A fresh explicit request supersedes a prior button dismissal even while the
        // pointer remains over Explorer's tray, and starts a fresh edge dwell afterward.
        _dismissed=false;_edgeSince=-1;
        _until=System.Math.Max(_until,now+20000);
    }
    internal void Toggle(long now) {
        if(_latched||now<_until){_latched=false;_until=0;_dismissed=true;_edgeSince=-1;}
        else {_latched=true;_dismissed=false;}
    }
    internal bool Update(bool atEdge,bool inTaskbar,bool shellInteraction,long now)
    {
        if(_latched)return true;
        if(_dismissed) {
            if(!atEdge&&!inTaskbar&&!shellInteraction)_dismissed=false;
            return false;
        }
        if(atEdge) {
            if(_edgeSince<0)_edgeSince=now;
            if(now-_edgeSince>=180)_until=System.Math.Max(_until,now+1800);
        } else _edgeSince=-1;
        if(inTaskbar||shellInteraction)_until=System.Math.Max(_until,now+1800);
        return now<_until;
    }
}
