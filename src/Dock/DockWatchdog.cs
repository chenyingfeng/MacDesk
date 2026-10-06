using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// Private Dock companion only. No service, elevation, Stage restart, or task registration.
internal static class DockWatchdog
{
    const string Prefix = "dock-watchdog.";
    const int MaximumRecoveries = 3;
    static readonly long RetryWindowTicks = TimeSpan.FromSeconds(60).Ticks;
    static volatile bool sessionEnding;

    sealed class ParentRecord
    {
        internal string Token, ExeHash;
        internal int Pid, Session;
        internal long StartedUtcTicks, CreatedUtcTicks;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQuerySessionInformation(IntPtr server, int session, int informationClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int informationClass, out int value, int length, out int returnedLength);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    // Call once after the real (non-preview) Dock has acquired its normal singleton.
    // A unique Guid per Dock process is essential; do not reuse a prior process's token.
    internal static bool Start(string rootPath, int parentPid, string runGuid)
    {
        string token = NormalizeToken(runGuid);
        if (token == null || parentPid <= 0) return false;
        try
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            string root = Path.GetFullPath(rootPath);
            if (!SamePath(root, Path.GetDirectoryName(exe))) return false;
            using (Process parent = Process.GetProcessById(parentPid))
            using (Process self = Process.GetCurrentProcess())
            {
                // Only the Dock may create its own guardian. Inherited elevation is never revived.
                if (parent.Id != self.Id || !SamePath(parent.MainModule.FileName, exe) || !IsOrdinaryToken(self)) return false;
                var record = new ParentRecord {
                    Token = token, Pid = parentPid, Session = self.SessionId,
                    StartedUtcTicks = parent.StartTime.ToUniversalTime().Ticks,
                    CreatedUtcTicks = DateTime.UtcNow.Ticks, ExeHash = HashFile(exe)
                };
                if (HasIntent(root, record, DateTime.UtcNow.Ticks)) return false;
                WriteRecord(root, record);
                CleanupExpiredRecords(root);
                var launch = new ProcessStartInfo(exe, "/watch-dock " + parentPid.ToString(CultureInfo.InvariantCulture) + " " + token) {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root
                };
                using (Process child = Process.Start(launch)) return child != null;
            }
        }
        catch { return false; }
    }

    // Invoke BEFORE a user exit menu, fresh installer exit.marker, or SessionEnding closes the Dock.
    // Do not invoke for an unhandled exception: that exit should be recoverable.
    internal static void MarkIntentionalExit(string rootPath, string runGuid, string reason)
    {
        string token = NormalizeToken(runGuid);
        if (token == null) return;
        try
        {
            string safeReason = reason == "session-ending" ? "session-ending" :
                reason == "installer" ? "installer" : reason == "exit-marker" ? "exit-marker" : "user-exit";
            AtomicWrite(IntentPath(rootPath, token), "Token=" + token + "\nUtcTicks=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "\nReason=" + safeReason + "\n");
        }
        catch { }
    }

    // Place before singleton acquisition, WPF creation, and preview dispatch in Main.
    // A recognized (even malformed) guardian invocation must never become an ordinary Dock.
    internal static bool TryRun(string[] args)
    {
        if (args == null || args.Length == 0 || !String.Equals(args[0], "/watch-dock", StringComparison.OrdinalIgnoreCase)) return false;
        int pid;
        string token;
        if (args.Length != 3 || !Int32.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0 || (token = NormalizeToken(args[2])) == null) return true;
        Run(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), pid, token);
        return true;
    }

    static void Run(string root, int parentPid, string token)
    {
        ParentRecord record = ReadRecord(root, token);
        Process parent = null;
        SessionEndingEventHandler ending = delegate(object sender, SessionEndingEventArgs e) {
            sessionEnding = true; MarkIntentionalExit(root, token, "session-ending");
        };
        SessionSwitchEventHandler switched = delegate(object sender, SessionSwitchEventArgs e) {
            if (e.Reason == SessionSwitchReason.SessionLogoff || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect) {
                sessionEnding = true; MarkIntentionalExit(root, token, "session-ending");
            }
        };
        bool eventsAttached = false;
        try
        {
            using (Process self = Process.GetCurrentProcess())
            {
                if (record == null || record.Pid != parentPid || record.Session != self.SessionId || !IsOrdinaryToken(self)) { WriteStatus(root, token, "invalid-parent-record"); return; }
            }
            string exe = Assembly.GetExecutingAssembly().Location;
            if (!String.Equals(record.ExeHash, HashFile(exe), StringComparison.Ordinal)) { WriteStatus(root, token, "executable-changed"); return; }
            try { SystemEvents.SessionEnding += ending; SystemEvents.SessionSwitch += switched; eventsAttached = true; } catch { }
            if (HasIntent(root, record, DateTime.UtcNow.Ticks)) { WriteStatus(root, token, "intentional-exit"); return; }

            try { parent = Process.GetProcessById(parentPid); }
            catch (ArgumentException) { parent = null; }
            if (parent != null)
            {
                // Retaining this Process handle pins the original process, even if its PID is reused.
                if (!ParentMatches(record.Pid, record.StartedUtcTicks, exe, record.Session,
                    parent.Id, parent.StartTime.ToUniversalTime().Ticks, parent.MainModule.FileName, parent.SessionId)) {
                    WriteStatus(root, token, "parent-identity-changed"); return;
                }
                WriteStatus(root, token, "monitoring");
                while (!parent.WaitForExit(350))
                {
                    if (sessionEnding || HasIntent(root, record, DateTime.UtcNow.Ticks)) {
                        // Exit promptly, including the installer's process enumeration wait.
                        WriteStatus(root, token, "intentional-exit"); return;
                    }
                }
            }
            else if (DateTime.UtcNow.Ticks - record.CreatedUtcTicks > TimeSpan.FromSeconds(15).Ticks)
            {
                // An absent parent is trustworthy only immediately after its self-verified Start.
                WriteStatus(root, token, "stale-parent-record"); return;
            }

            // Allow shutdown/session events and an already-issued close intent to settle.
            for (int i = 0; i < 3; i++)
            {
                if (sessionEnding || HasIntent(root, record, DateTime.UtcNow.Ticks)) { WriteStatus(root, token, "intentional-exit"); return; }
                Thread.Sleep(250);
            }
            if (!InteractiveSessionReady(record.Session)) { WriteStatus(root, token, "session-unavailable"); return; }
            if (!TakeRecoverySlot(root, DateTime.UtcNow.Ticks)) { WriteStatus(root, token, "rate-limited"); return; }
            if (sessionEnding || HasIntent(root, record, DateTime.UtcNow.Ticks) || !InteractiveSessionReady(record.Session)) { WriteStatus(root, token, "intentional-exit"); return; }
            var launch = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            using (Process replacement = Process.Start(launch)) WriteStatus(root, token, replacement == null ? "restart-failed" : "restarted");
            // Exactly one attempt. The replacement Dock owns a new token and its own guardian.
        }
        catch { WriteStatus(root, token, "monitor-unavailable"); }
        finally
        {
            if (eventsAttached) { try { SystemEvents.SessionEnding -= ending; SystemEvents.SessionSwitch -= switched; } catch { } }
            bool gone = parent == null;
            if (parent != null) { try { gone = parent.HasExited; } catch { } parent.Dispose(); }
            try { File.Delete(RecordPath(root, token)); } catch { }
            if (gone) { try { File.Delete(IntentPath(root, token)); } catch { } }
        }
    }

    static bool IsOrdinaryToken(Process process)
    {
        IntPtr token;
        if (!OpenProcessToken(process.Handle, 0x0008, out token)) return false;
        try { int elevated, count; return GetTokenInformation(token, 20, out elevated, 4, out count) && elevated == 0; }
        finally { CloseHandle(token); }
    }

    static bool InteractiveSessionReady(int session)
    {
        if (sessionEnding || !Environment.UserInteractive || Environment.HasShutdownStarted) return false;
        try
        {
            if (GetSystemMetrics(0x2000) != 0) return false; // SM_SHUTTINGDOWN
            IntPtr buffer; int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero, session, 8, out buffer, out bytes)) return false;
            try { return bytes >= 4 && Marshal.ReadInt32(buffer) == 0; } // WTSActive
            finally { WTSFreeMemory(buffer); }
        }
        catch { return false; }
    }

    static bool ParentMatches(int expectedPid, long expectedStarted, string expectedExe, int expectedSession,
        int actualPid, long actualStarted, string actualExe, int actualSession)
    {
        return expectedPid == actualPid && expectedStarted == actualStarted && expectedSession == actualSession && SamePath(expectedExe, actualExe);
    }

    static bool SamePath(string a, string b)
    {
        try { return String.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static string NormalizeToken(string value)
    {
        Guid token; return Guid.TryParse(value, out token) && token != Guid.Empty ? token.ToString("N") : null;
    }

    static string RecordPath(string root, string token) { return Path.Combine(root, Prefix + token + ".state"); }
    static string IntentPath(string root, string token) { return Path.Combine(root, Prefix + token + ".intent"); }
    static string HashFile(string path)
    {
        using (SHA256 hash = SHA256.Create()) using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    static void WriteRecord(string root, ParentRecord record)
    {
        AtomicWrite(RecordPath(root, record.Token), "Token=" + record.Token + "\nPid=" + record.Pid.ToString(CultureInfo.InvariantCulture) +
            "\nSession=" + record.Session.ToString(CultureInfo.InvariantCulture) + "\nStartedUtcTicks=" + record.StartedUtcTicks.ToString(CultureInfo.InvariantCulture) +
            "\nCreatedUtcTicks=" + record.CreatedUtcTicks.ToString(CultureInfo.InvariantCulture) + "\nExeHash=" + record.ExeHash + "\n");
    }

    static Dictionary<string, string> ReadFields(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path)) { int split = line.IndexOf('='); if (split > 0) fields[line.Substring(0, split)] = line.Substring(split + 1); }
        return fields;
    }

    static ParentRecord ReadRecord(string root, string token)
    {
        try
        {
            Dictionary<string, string> fields = ReadFields(RecordPath(root, token));
            if (fields == null || fields["Token"] != token) return null;
            var result = new ParentRecord {
                Token = token, Pid = Int32.Parse(fields["Pid"], CultureInfo.InvariantCulture), Session = Int32.Parse(fields["Session"], CultureInfo.InvariantCulture),
                StartedUtcTicks = Int64.Parse(fields["StartedUtcTicks"], CultureInfo.InvariantCulture), CreatedUtcTicks = Int64.Parse(fields["CreatedUtcTicks"], CultureInfo.InvariantCulture), ExeHash = fields["ExeHash"]
            };
            if (result.Pid <= 0 || result.Session < 0 || result.StartedUtcTicks <= 0 || result.CreatedUtcTicks < result.StartedUtcTicks || result.ExeHash.Length != 64) return null;
            return result;
        }
        catch { return null; }
    }

    static bool IntentMatches(string expectedToken, long parentStarted, string markerToken, long markerUtc, long now)
    {
        return String.Equals(expectedToken, markerToken, StringComparison.Ordinal) && markerUtc >= parentStarted && markerUtc <= now + TimeSpan.FromMinutes(5).Ticks;
    }

    static bool HasIntent(string root, ParentRecord parent, long now)
    {
        try
        {
            Dictionary<string, string> fields = ReadFields(IntentPath(root, parent.Token));
            return fields != null && IntentMatches(parent.Token, parent.StartedUtcTicks, fields["Token"], Int64.Parse(fields["UtcTicks"], CultureInfo.InvariantCulture), now);
        }
        catch { return false; }
    }

    static List<long> RecentAttempts(IEnumerable<long> attempts, long now)
    {
        var recent = new List<long>();
        foreach (long at in attempts) if (at > now - RetryWindowTicks && at <= now + TimeSpan.FromMinutes(5).Ticks) recent.Add(at);
        return recent;
    }

    static string RecoveryDecision(bool matchingIntent, bool matchingParent, bool activeSession, IEnumerable<long> attempts, long now)
    {
        if (matchingIntent) return "intentional";
        if (!matchingParent) return "identity-changed";
        if (!activeSession) return "session-ending";
        return RecentAttempts(attempts, now).Count < MaximumRecoveries ? "recover" : "rate-limited";
    }

    static bool TakeRecoverySlot(string root, long now)
    {
        string key;
        using (SHA256 hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))).Replace("-", "");
        using (var gate = new Mutex(false, "Local\\MacDesk.Watchdog." + key.Substring(0, 24)))
        {
            bool owns = false;
            try
            {
                try { owns = gate.WaitOne(1500); } catch (AbandonedMutexException) { owns = true; }
                if (!owns) return false;
                string path = Path.Combine(root, "dock-watchdog-retries.txt");
                var attempts = new List<long>();
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > 16384) return false;
                    foreach (string line in File.ReadAllLines(path)) { long at; if (Int64.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out at)) attempts.Add(at); }
                }
                List<long> recent = RecentAttempts(attempts, now);
                if (recent.Count >= MaximumRecoveries) return false;
                recent.Add(now);
                var output = new StringBuilder(); foreach (long at in recent) output.Append(at.ToString(CultureInfo.InvariantCulture)).Append('\n');
                AtomicWrite(path, output.ToString());
                return true;
            }
            catch { return false; }
            finally { if (owns) gate.ReleaseMutex(); }
        }
    }

    static void AtomicWrite(string path, string content)
    {
        string pending = path + ".pending";
        File.WriteAllText(pending, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(pending, path, null); else File.Move(pending, path);
    }

    static void WriteStatus(string root, string token, string state)
    {
        try { AtomicWrite(Path.Combine(root, "dock-watchdog.status"), "Run=" + token + "\nState=" + state + "\nUtcTicks=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "\n"); } catch { }
    }

    static void CleanupExpiredRecords(string root)
    {
        try
        {
            int count = 0;
            foreach (string path in Directory.GetFiles(root, Prefix + "*"))
            {
                if (++count > 200) break;
                string name = Path.GetFileName(path);
                if ((!name.EndsWith(".state", StringComparison.Ordinal) && !name.EndsWith(".intent", StringComparison.Ordinal)) || name.Length != Prefix.Length + 32 + (name.EndsWith(".state", StringComparison.Ordinal) ? 6 : 7)) continue;
                if (NormalizeToken(name.Substring(Prefix.Length, 32)) == null) continue;
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-2)) File.Delete(path);
            }
        }
        catch { }
    }

    // Deterministic fixture-only checks: never starts, stops, or queries any desktop app.
    internal static bool Check(string reportPath)
    {
        var report = new StringBuilder();
        bool passed = true;
        try
        {
            long now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc).Ticks;
            string token = "00112233445566778899aabbccddeeff";
            var empty = new long[0];
            Expect(report, RecoveryDecision(false, true, true, empty, now) == "recover", "unexpected exit is recoverable");
            Expect(report, RecoveryDecision(true, true, true, empty, now) == "intentional", "manual and installer exits remain exited");
            Expect(report, RecoveryDecision(false, false, true, empty, now) == "identity-changed", "PID reuse or executable changes veto recovery");
            Expect(report, RecoveryDecision(false, true, false, empty, now) == "session-ending", "logoff or inactive session vetoes recovery");
            Expect(report, RecoveryDecision(false, true, true, new long[] { now - 1, now - 2, now - 3 }, now) == "rate-limited", "three recoveries per minute is a hard limit");
            Expect(report, RecoveryDecision(false, true, true, new long[] { now - RetryWindowTicks - 1, now - 1, now - 2 }, now) == "recover", "expired retries release a slot");
            Expect(report, IntentMatches(token, now - 100, token, now, now), "matching current token closes recovery");
            Expect(report, !IntentMatches(token, now - 100, "ffeeddccbbaa99887766554433221100", now, now), "another run's exit token cannot close this run");
            Expect(report, !IntentMatches(token, now, token, now - 1, now), "an intent older than the parent is stale");
            Expect(report, NormalizeToken("../anything") == null && NormalizeToken(Guid.Empty.ToString()) == null, "malformed tokens cannot become file names");
            string exe = Path.Combine(Path.GetTempPath(), "fixture-dock.exe");
            Expect(report, ParentMatches(42, now, exe, 1, 42, now, exe, 1), "exact parent identity matches");
            Expect(report, !ParentMatches(42, now, exe, 1, 42, now + 1, exe, 1), "same PID with another start time never matches");
            Expect(report, !ParentMatches(42, now, exe, 1, 42, now, exe + ".other", 1), "same PID with another executable never matches");
            Expect(report, !ParentMatches(42, now, exe, 1, 42, now, exe, 2), "another user session never matches");
            string fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "watchdog-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var record = new ParentRecord { Token = token, Pid = 42, Session = 1, StartedUtcTicks = now - 100, CreatedUtcTicks = now, ExeHash = new string('a', 64) };
            WriteRecord(fixture, record);
            ParentRecord roundtrip = ReadRecord(fixture, token);
            Expect(report, roundtrip != null && roundtrip.Pid == 42 && roundtrip.StartedUtcTicks == now - 100, "parent metadata round trips without paths");
            AtomicWrite(IntentPath(fixture, token), "Token=" + token + "\nUtcTicks=" + now.ToString(CultureInfo.InvariantCulture) + "\nReason=user-exit\n");
            Expect(report, HasIntent(fixture, record, now), "intent marker is scoped to the current run");
            Expect(report, TakeRecoverySlot(fixture, now) && TakeRecoverySlot(fixture, now + 1) && TakeRecoverySlot(fixture, now + 2) && !TakeRecoverySlot(fixture, now + 3), "persisted records enforce the restart limit");
            Expect(report, TakeRecoverySlot(fixture, now + RetryWindowTicks + 3), "persisted retry records expire");
            string retryContents = File.ReadAllText(Path.Combine(fixture, "dock-watchdog-retries.txt"));
            Expect(report, retryContents.IndexOf('\\') < 0 && retryContents.IndexOf(':') < 0 && retryContents.IndexOf('@') < 0, "retry records contain timestamps only");
        }
        catch (Exception error) { passed = false; report.Append("FAIL ").Append(error.GetType().Name).Append('\n'); }
        try { File.WriteAllText(reportPath, (passed ? "PASS\n" : "FAIL\n") + report.ToString(), new UTF8Encoding(false)); } catch { return false; }
        return passed;
    }

    static void Expect(StringBuilder output, bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        output.Append("PASS ").Append(description).Append('\n');
    }
}
