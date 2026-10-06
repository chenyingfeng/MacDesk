using System;
using System.Globalization;
namespace StageManager.Services;
// Snapshots are advisory, never an indefinite lease after their writer disappears.
internal static class RuntimeSnapshotPolicy
{
    internal static string Value(string text,string name) {
        foreach(var raw in text.Split('\n')) {
            var line=raw.TrimEnd('\r');
            if(line.StartsWith(name+"=",StringComparison.Ordinal))return line[(name.Length+1)..];
        }
        return "";
    }
    internal static bool Fresh(string text,string field,DateTime now,TimeSpan age) =>
        DateTime.TryParse(Value(text,field),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var stamp)
        && now-stamp.ToUniversalTime()>=TimeSpan.FromSeconds(-1)
        && now-stamp.ToUniversalTime()<=age;
    internal static bool TaskbarPeek(string text,DateTime now)=>Value(text,"State")=="peek"
        && Fresh(text,"Utc",now,TimeSpan.FromSeconds(4));
    internal static bool UiResponsive(string text,int expectedPid,DateTime now)=>
        int.TryParse(Value(text,"Pid"),out var pid)&&pid==expectedPid&&Value(text,"State")!="stopped"
        && Fresh(text,"UiUtc",now,TimeSpan.FromSeconds(12));
    internal static bool RecoveryToken(string token,DateTime stamp,DateTime now)=>
        Guid.TryParseExact(token.Trim(),"N",out _)&&now-stamp.ToUniversalTime()>=TimeSpan.FromSeconds(-1)
        && now-stamp.ToUniversalTime()<=TimeSpan.FromSeconds(5);
    internal static bool ShouldRearm(bool hidden,bool armed,bool nearEdge,DateTime suppressionEnd,DateTime now)=>
        hidden&&!armed&&nearEdge&&now>=suppressionEnd;
}
