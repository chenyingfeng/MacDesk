using System;
using System.IO;
using System.Globalization;
using System.Text;
using System.Runtime.InteropServices;

namespace StageManager.Services
{
    // Shared with the companion dock. No URL, account, window handle or credentials
    // travel across this boundary. An intent only permits a foreground browser restore.
    internal sealed class BrowserActivationIntent
    {
        private string seen = "";
        private long expires;
        private long issuedAt;
        private bool pending;
        internal string TargetBrowser { get; private set; }
        internal bool HasPending { get { return pending; } }
        internal BrowserActivationIntent() { TargetBrowser=""; }
        internal long AcceptedRequests { get; private set; }
        internal void Prime(string marker) { seen = marker; pending = false; }
        internal void Receive(string marker, long utcTicks)
        {
            if (marker == seen) return;
            seen = marker;
            pending = false;
            string[] parts = marker.Split('|');
            Guid id;
            long issued;
            if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out id)
                || parts[1] != "browser" || (parts[2] != "" && !IsBrowser(parts[2]))
                || !long.TryParse(parts[3], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out issued)) return;
            long age = utcTicks - issued;
            if (issued < DateTime.MinValue.Ticks || issued > DateTime.MaxValue.Ticks
                || age < -TimeSpan.TicksPerSecond || age > 5 * TimeSpan.TicksPerSecond) return;
            expires = issued + 5 * TimeSpan.TicksPerSecond;
            issuedAt = issued;
            TargetBrowser = parts[2];
            pending = true;
            AcceptedRequests++;
        }
        internal bool TryConsume(string process, bool trackedForeground, long utcTicks)
        {
            if (!pending) return false;
            if (utcTicks > expires) { pending = false; return false; }
            if (!trackedForeground || !IsBrowser(process)
                || (!string.IsNullOrEmpty(TargetBrowser) && !MatchesTarget(process))) return false;
            pending = false;
            return true;
        }
        internal void Cancel() { pending = false; }
        internal bool MatchesTarget(string process)
        {
            return !string.IsNullOrEmpty(TargetBrowser) && Path.GetFileNameWithoutExtension(process)
                .Equals(TargetBrowser, StringComparison.OrdinalIgnoreCase);
        }
        internal bool TryConsumeDefault(string process, bool trackedVisible, long utcTicks)
        {
            if (!pending || !trackedVisible || !MatchesTarget(process)) return false;
            if (utcTicks > expires) { pending=false; return false; }
            if (utcTicks-issuedAt < 700 * TimeSpan.TicksPerMillisecond) return false;
            pending=false;
            return true;
        }
        internal static bool IsBrowser(string process)
        {
            string name = Path.GetFileNameWithoutExtension(process).ToLowerInvariant();
            switch (name)
            {
                case "msedge": case "chrome": case "firefox": case "brave":
                case "opera": case "vivaldi": case "iexplore": case "arc":
                case "zen": case "floorp": case "waterfox": case "thorium":
                case "360se": case "360chrome": case "qqbrowser": return true;
                default: return false;
            }
        }
        internal static string CreateMarker(string targetBrowser)
        {
            if (!IsBrowser(targetBrowser)) targetBrowser="";
            return Guid.NewGuid().ToString("N") + "|browser|" + targetBrowser.ToLowerInvariant()
                + "|" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        internal static void Write(string stageDirectory, string marker)
        {
            if (!Directory.Exists(stageDirectory)) return;
            File.WriteAllText(Path.Combine(stageDirectory, "external-activation.request"),
                marker);
        }
        [DllImport("shlwapi.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        private static extern int AssocQueryStringW(uint flags, uint str, string association, string extra,
            StringBuilder output, ref uint length);
        internal static string DefaultBrowser()
        {
            try {
                var output=new StringBuilder(32768); uint length=(uint)output.Capacity;
                // IS_PROTOCOL + NOFIXUPS: query current HTTPS handler without repairing or changing settings.
                if (AssocQueryStringW(0x1100, 2, "https", "open", output, ref length) != 0) return "";
                string name=Path.GetFileNameWithoutExtension(output.ToString());
                return IsBrowser(name) ? name.ToLowerInvariant() : "";
            } catch { return ""; }
        }
    }
}
