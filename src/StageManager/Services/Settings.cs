namespace StageManager.Services;
public static class Settings
{
    public static void SetHideDesktopIcons(bool value) => PortablePreferences.Write("hide-desktop-icons", value);
    public static bool GetHideDesktopIcons() => PortablePreferences.Read("hide-desktop-icons");
}
