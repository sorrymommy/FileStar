using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace FileStar;

/// <summary>
/// Updates through Velopack from the GitHub releases of sorrymommy/FileStar (installer\release.ps1 publishes them).
/// Only a copy installed by FileStar's Setup.exe can update itself; a build run from the project folder can't.
/// </summary>
public static class Updater
{
    public const string RepoUrl = "https://github.com/sorrymommy/FileStar";

    static UpdateManager? _manager;
    // FILESTAR_UPDATE_URL (a folder or web address with Velopack packages) lets tests update from a local build.
    static UpdateManager Manager => _manager ??= Environment.GetEnvironmentVariable("FILESTAR_UPDATE_URL") is { Length: > 0 } testFeed
        ? new UpdateManager(testFeed)
        : new UpdateManager(new GithubSource(RepoUrl, null, false));

    /// <summary>An update already downloaded, applied when FileStar closes (or restarts).</summary>
    public static UpdateInfo? Ready { get; private set; }

    /// <summary>Only the first window checks at start-up.</summary>
    public static bool CheckedAtStartup { get; set; }

    public static bool IsInstalled
    {
        get
        {
            try { return Manager.IsInstalled; }
            catch { return false; }
        }
    }

    public static string CurrentVersion
    {
        get
        {
            try
            {
                if (Manager.IsInstalled && Manager.CurrentVersion is { } version)
                    return version.ToString();
            }
            catch { }
            var assembly = Assembly.GetExecutingAssembly();
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                   ?? assembly.GetName().Version?.ToString(3) ?? "";
        }
    }

    /// <summary>Looks for a newer release and downloads it; returns it when one is ready to apply.</summary>
    public static async Task<UpdateInfo?> CheckAndDownloadAsync(Action<int>? progress = null)
    {
        if (!IsInstalled)
            return null;
        var update = await Manager.CheckForUpdatesAsync();
        if (update == null)
            return null;
        await Manager.DownloadUpdatesAsync(update, progress);
        return Ready = update;
    }

    /// <summary>Closes FileStar, installs the downloaded update and starts the new version.</summary>
    public static void ApplyAndRestart()
    {
        if (Ready != null)
            Manager.ApplyUpdatesAndRestart(Ready.TargetFullRelease);
    }

    /// <summary>When the last window closes with an update downloaded: install it quietly once FileStar has exited.</summary>
    public static void ApplyOnExit()
    {
        if (Ready == null)
            return;
        try
        {
            Manager.WaitExitThenApplyUpdates(Ready.TargetFullRelease, silent: true, restart: false);
        }
        catch { }
        Ready = null;
    }
}
