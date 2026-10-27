using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace FileStar.Services;

/// <summary>Self-update from the GitHub releases.</summary>
public interface IUpdateService
{
    string RepoUrl { get; }

    /// <summary>Only a copy installed by FileStar's Setup.exe can update itself; a build run from the project folder can't.</summary>
    bool IsInstalled { get; }

    string CurrentVersion { get; }

    /// <summary>Looks for a newer release and downloads it; returns its version when one is ready to apply.</summary>
    Task<string?> CheckAndDownloadAsync(Action<int>? progress = null);

    /// <summary>Closes FileStar, installs the downloaded update and starts the new version.</summary>
    void ApplyAndRestart();

    /// <summary>When FileStar closes with an update downloaded: install it quietly once it has exited.</summary>
    void ApplyOnExit();
}

/// <summary>Updates through Velopack from the GitHub releases of sorrymommy/FileStar (release\release.ps1 publishes them).</summary>
public sealed class VelopackUpdateService : IUpdateService
{
    UpdateManager? _manager;
    UpdateInfo? _ready;

    public string RepoUrl => "https://github.com/sorrymommy/FileStar";

    // FILESTAR_UPDATE_URL (a folder or web address with Velopack packages) lets tests update from a local build.
    UpdateManager Manager => _manager ??= Environment.GetEnvironmentVariable("FILESTAR_UPDATE_URL") is { Length: > 0 } testFeed
        ? new UpdateManager(testFeed)
        : new UpdateManager(new GithubSource(RepoUrl, null, false));

    public bool IsInstalled
    {
        get
        {
            try { return Manager.IsInstalled; }
            catch { return false; }
        }
    }

    public string CurrentVersion
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

    public async Task<string?> CheckAndDownloadAsync(Action<int>? progress = null)
    {
        if (_ready != null)
            return _ready.TargetFullRelease.Version.ToString();
        if (!IsInstalled)
            return null;
        var update = await Manager.CheckForUpdatesAsync();
        if (update == null)
            return null;
        await Manager.DownloadUpdatesAsync(update, progress);
        _ready = update;
        return update.TargetFullRelease.Version.ToString();
    }

    public void ApplyAndRestart()
    {
        if (_ready != null)
            Manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }

    public void ApplyOnExit()
    {
        if (_ready == null)
            return;
        try
        {
            Manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false);
        }
        catch { }
        _ready = null;
    }
}
