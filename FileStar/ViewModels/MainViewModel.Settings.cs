namespace FileStar.ViewModels;

// Reading the settings at start-up and writing the whole session back (tabs, panes, options), with the
// window's own layout supplied by the view.
public sealed partial class MainViewModel
{
    /// <summary>The window's current layout, or null while it has none worth saving (e.g. not shown yet).</summary>
    public Func<WindowLayout?> LayoutProvider { get; set; } = () => null;

    void ApplySettings(AppSettings settings)
    {
        _s.Theme.Apply(settings.DarkMode);
        _s.FileSystem.ShowHidden = settings.ShowHidden;
        _s.Recent.Load(settings.RecentFolders, settings.RecentFiles);
        string sortKey = FileItemComparer.SortableKeys.Contains(settings.SortKey) ? settings.SortKey : "Name";
        FileList.SetSort(sortKey, settings.SortDescending);
        _showNoteMarks = settings.ShowNoteMarks;
        _listNoteMarks = settings.ShowNoteMarks;
        Search.LoadSettings(settings.SearchOptions ?? "ntms");
        Properties.LoadSettings(settings.PropLabelWidth, settings.HiddenProps);
        _infoTabIndex = Math.Clamp(settings.InfoTab, MemoTab, PreviewTab);
        _isInfoPaneVisible = settings.NotePaneVisible;
        RebuildFavoritesMenu();
    }

    /// <summary>Writes the whole session: tabs and their folders, layout, columns, panes, theme.</summary>
    public void SaveSettings()
    {
        if (_active == null || LayoutProvider() is not WindowLayout layout)
            return;   // still starting up
        _s.Settings.Save(new AppSettings
        {
            Tabs = Tabs.Select(t => t.FullPath).ToList(),
            ActiveTab = Tabs.IndexOf(_active),
            PinnedTabs = Tabs.Select((t, i) => t.IsPinned ? i : -1).Where(i => i >= 0).ToList(),
            Left = layout.Left,
            Top = layout.Top,
            Width = layout.Width,
            Height = layout.Height,
            Maximized = layout.Maximized,
            TreeWidth = layout.TreeWidth,
            NotePaneWidth = layout.NotePaneWidth,
            DualPaneRatio = layout.DualPaneRatio,
            Columns = layout.Columns,
            InfoTab = InfoTabIndex,
            NotePaneVisible = IsInfoPaneVisible,
            ShowNoteMarks = ShowNoteMarks,
            SearchOptions = Search.OptionLetters,
            DualPaneVisible = IsDualVisible,
            DualPanePath = Dual.HasFolder ? Dual.Folder : null,
            RecentFolders = _s.Recent.Folders.ToList(),
            RecentFiles = _s.Recent.Files.ToList(),
            PropLabelWidth = Properties.LabelWidth,
            HiddenProps = Properties.HiddenLabels.OrderBy(p => p).ToList(),
            ShowHidden = ShowHidden,
            SortKey = FileList.SortKey,
            SortDescending = FileList.SortDescending,
            DarkMode = IsDarkMode,
        });
    }

    /// <summary>Everything that must be on disk before FileStar exits (closing, or restarting for an update).</summary>
    void SaveBeforeExit()
    {
        _s.Favorites.Save(notify: false);   // keeps group expand/collapse state
        Notes.ResolvePendingEdit();
        Notes.CommitTags();
        SaveSettings();
    }

    /// <summary>The window is closing: save, stop watching, and install a downloaded update once FileStar has exited.</summary>
    public void OnClosing()
    {
        _watcher?.Dispose();
        Dual.Dispose();
        SaveBeforeExit();
        _s.Updates.ApplyOnExit();
    }
}
