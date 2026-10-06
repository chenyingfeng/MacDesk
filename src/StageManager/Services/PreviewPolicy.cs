namespace StageManager.Services;
internal static class PreviewPolicy
{
    // This preview build uses the supported DWM thumbnail API, not WGC capture.
    internal static bool UseDwmThumbnails=>true;
    internal static bool RetainTrackedWindow(bool valid,bool visible)=>valid && visible;
}
