using System.IO;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace FileStar.Services;

/// <summary>What the folder tree lists besides favorites and drives, as Explorer's navigation pane does.</summary>
public interface ITreePlaces
{
    /// <summary>The folders pinned at the top: 바탕 화면, 다운로드, 문서.</summary>
    IReadOnlyList<(string Name, string Path)> PinnedFolders();

    /// <summary>The PC is on a network: the tree shows 네트워크.</summary>
    bool IsNetworkAvailable { get; }

    /// <summary>The computers 네트워크 lists ("\\NAME"). Network discovery takes seconds: never call it on the UI thread.</summary>
    IReadOnlyList<string> FindComputers();

    /// <summary>The root folders of the installed WSL distributions ("\\wsl.localhost\Ubuntu"), read without starting WSL.</summary>
    IReadOnlyList<string> LinuxDistros();
}

public sealed class WindowsTreePlaces : ITreePlaces
{
    const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public IReadOnlyList<(string Name, string Path)> PinnedFolders() => new (string Name, string Path)[]
    {
        ("바탕 화면", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
        ("다운로드", Locations.Downloads),
        ("문서", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
    }.Where(f => f.Path.Length > 0).ToList();

    public bool IsNetworkAvailable => NetworkInterface.GetIsNetworkAvailable();

    public IReadOnlyList<string> FindComputers() => NetworkShell.FindComputers();

    /// <remarks>
    /// Each registered distribution has a subkey with its name; a distribution still being installed or removed
    /// (State other than 1) can't be opened yet. "\\wsl.localhost" is the name Windows 10 21H2 and later give them.
    /// </remarks>
    public IReadOnlyList<string> LinuxDistros()
    {
        var distros = new List<string>();
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey);
            if (lxss == null)
                return distros;
            foreach (var id in lxss.GetSubKeyNames())
            {
                using var distro = lxss.OpenSubKey(id);
                if (distro?.GetValue("DistributionName") is string name && FileNames.IsPlainName(name) &&
                    distro.GetValue("State") is 1)
                    distros.Add(@"\\wsl.localhost\" + name);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return distros;
        }
        distros.Sort(Native.StrCmpLogicalW);
        return distros;
    }
}
