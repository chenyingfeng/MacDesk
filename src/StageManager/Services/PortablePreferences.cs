using System;
using System.IO;
namespace StageManager.Services;
// Local, reversible preferences. No registry writes, no credentials or window titles.
internal static class PortablePreferences
{
    internal static string Root => AppContext.BaseDirectory;
    internal static string StopFile => Path.Combine(Root, "stop.request");
    internal static bool Read(string name) => File.Exists(Path.Combine(Root, name + ".enabled"));
    internal static void Write(string name, bool value)
    {
        var path = Path.Combine(Root, name + ".enabled");
        if(value) File.WriteAllText(path, "1"); else if(File.Exists(path)) File.Delete(path);
    }
    internal static bool IsExcludedProcess(string name)
    {
        name = Path.GetFileNameWithoutExtension(name);
        // Exclude utility windows before startup operations that modify windows.
        return name.StartsWith("PersonalDock", StringComparison.OrdinalIgnoreCase)
            || name.Equals("MacDesk", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MailBridge", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DesktopStart", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CampusStartAdmin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CampusApply", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TrafficMonitor", StringComparison.OrdinalIgnoreCase);
    }
}
