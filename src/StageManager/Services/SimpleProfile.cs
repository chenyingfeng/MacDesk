using System;
namespace StageManager.Services;
// One supported daily-use profile. Applied only for normal startup, after diagnostic branches.
internal static class SimpleProfile
{
    internal static void Apply()=>Apply(PortablePreferences.Read,PortablePreferences.Write);
    // Separate policy from its file sink so diagnostics never change a user's preferences.
    internal static void Apply(Func<string,bool> read,Action<string,bool> write) {
        foreach(var name in new[]{"recent-first","hide-taskbar","disable-wallpaper-toggle"})
            if(!read(name))write(name,true);
        // A newly selected app joins the stage; earlier apps and all their members stay visible.
        // Clear older profile flags on each launch, so an upgrade needs no manual setup.
        foreach(var name in new[]{"mac-mode","exclusive-stage","one-window-per-app","sidebar-always-visible","hide-desktop-icons","disable-window-motion"})
            if(read(name))write(name,false);
    }
}
