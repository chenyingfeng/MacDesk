using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
namespace StageManager.Services;
public record UpdateInfo(string TagName, Version Version, string DownloadUrl, long Size);
// Modified releases must never download upstream executables over the bundle.
public sealed class UpdateService : IDisposable
{
    internal static readonly string StagingFolder = Path.Combine(AppContext.BaseDirectory, "updates");
    public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default) => Task.FromResult<UpdateInfo?>(null);
    public Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
        => throw new NotSupportedException("Install MacDesk releases manually as a complete bundle.");
    public static void ApplyUpdate(string downloadedExePath) => throw new NotSupportedException("Automatic update replacement is disabled.");
    public static void LaunchAndExit(string? snapshotPath) => throw new NotSupportedException("Automatic update replacement is disabled.");
    public static void CleanupOldVersion() { }
    public static void CleanupStagingFolder() { }
    internal static Version GetCurrentVersion()
    {
        string value = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int suffix = value.IndexOf('+');
        if (suffix >= 0) value = value[..suffix];
        return Version.TryParse(value.TrimStart('v', 'V'), out var version) ? version : new Version(0, 0, 0);
    }
    public void Dispose() { }
}