using System;

namespace StageManager.Services
{
    // Dispatcher-owned provenance, independent of input devices. A parked app gaining
    // foreground is meaningful after a shell selector, but not after native minimize.
    internal sealed class SystemSelectionPolicy
    {
        internal bool Active { get; private set; }
        internal long Epoch { get; private set; }
        private long _expires;
        private bool _pending;

        internal bool Observe(bool shellSelector, bool trackedApplication, long now)
        {
            if (shellSelector)
            {
                if (!Active) Epoch++;
                Active = true;
                _pending = true;
                _expires = 0;
                return false;
            }
            if (Active)
            {
                Active = false;
                _expires = now + 1500; // Allow an intermediate shell/desktop frame.
            }
            if (!_pending) return false;
            if (now > _expires) { _pending = false; return false; }
            if (!trackedApplication) return false;
            _pending = false;
            return true;
        }

        internal static bool IsSelector(string process, string windowClass)
        {
            // Shell HWND classes are implementation details: these are conservative
            // compatibility cases, not a promise about every Windows shell version.
            if (process.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                || process.Equals("ShellHost", StringComparison.OrdinalIgnoreCase)) return true;
            if (!process.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return false;
            return windowClass.Equals("MultitaskingViewFrame", StringComparison.OrdinalIgnoreCase)
                || windowClass.Equals("TaskSwitcherWnd", StringComparison.OrdinalIgnoreCase)
                || windowClass.Equals("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase)
                || windowClass.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase);
        }
    }
}
