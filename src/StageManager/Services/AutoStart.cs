namespace StageManager.Services;
// Optional integration with the companion launcher, not a Windows startup registration.
public static class AutoStart
{
    public static void SetStartup(string appName, bool startup) => PortablePreferences.Write("autostart", startup);
    public static bool IsStartup(string appName) => PortablePreferences.Read("autostart");
}
