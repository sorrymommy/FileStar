using System.Diagnostics;
using System.Windows;

namespace FileStar.Services;

/// <summary>Handing things to Windows: opening files, Explorer, property sheets, another FileStar window.</summary>
public interface IShellService
{
    /// <summary>Opens with the default program and remembers it under 최근 파일. Throws if Windows can't open it.</summary>
    void Open(string path);

    /// <summary>Opens Explorer on a folder, or on its parent with <paramref name="path"/> selected.</summary>
    void OpenInExplorer(string path, bool select);

    void ShowProperties(string path);

    /// <summary>Another FileStar window on <paramref name="folder"/>. Throws if it can't start.</summary>
    void StartNewWindow(string folder);

    void CopyText(string text);
}

public sealed class ShellService(IRecentItems recent) : IShellService
{
    public void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        recent.AddFile(path);
    }

    public void OpenInExplorer(string path, bool select)
    {
        if (Locations.IsThisPc(path))
            Process.Start("explorer.exe", "shell:MyComputerFolder");
        else if (SearchLocation.TryParse(path) is { } search)
        {
            if (search.Kind == SearchKind.Here)
                Process.Start("explorer.exe", $"\"{search.Base}\"");
        }
        else
            Process.Start("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"");
    }

    public void ShowProperties(string path) => Native.ShowProperties(path);

    // ArgumentList quotes for us: "C:\" written by hand would arrive as C:" (the backslash escapes the quote).
    public void StartNewWindow(string folder) =>
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { folder } });

    public void CopyText(string text) => Clipboard.SetText(text);
}
