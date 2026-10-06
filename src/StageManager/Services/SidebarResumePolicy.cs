namespace StageManager.Services;
// Shell menus can disappear while the pointer is already at the reveal edge.
// Release the old hide latch once; do not wait for an unrelated mouse excursion.
internal sealed class SidebarResumePolicy
{
    private bool navigation,taskbar;
    internal bool Observe(bool systemNavigation,bool taskbarPeek) {
        bool resume=!systemNavigation&&(navigation||taskbar&&!taskbarPeek);
        navigation=systemNavigation;taskbar=taskbarPeek;return resume;
    }
}
