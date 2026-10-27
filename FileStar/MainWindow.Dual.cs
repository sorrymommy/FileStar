using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FileStar;

// 두 창 보기 and 폴더 비교.
// The two lists are equals: each has the same title strip, columns and sort, and the window's address bar,
// back/forward/up, tree, filter and info pane all serve whichever list is active (a blue line marks it).
// Folder compare marks every item against the other list by name, size and modified time.
public partial class MainWindow
{
    // ── IFolderPaneHost ──

    public string? MainFolder => InVirtual ? null : _active.FullPath;
    public void OpenInMain(string path) => Navigate(path);
    public void Close(FolderPane pane) => SetDualPane(false);
    public void FilesChanged() => Refresh(reloadTree: false);

    // ── Which list is active ──

    /// <summary>The right list had the keyboard last.</summary>
    bool _dualActive;

    /// <summary>The right list is open and active: the window's navigation and info pane serve it.</summary>
    bool DualActive => _dualActive && DualPane.Visibility == Visibility.Visible;

    /// <summary>The folder (or 내 PC / result list) of the active list.</summary>
    string ActiveFolder => DualActive ? DualPane.Folder : _active.FullPath;

    /// <summary>The selection the info pane, Ctrl+M and AI work on: the active list's.</summary>
    List<FileItem> InfoSelection => DualActive ? DualPane.Selected : SelectedItems;

    /// <summary>The folder described when nothing is selected (null for 내 PC and result lists).</summary>
    string? InfoFolder => DualActive ? DualPane.Folder : InVirtual ? null : _active.FullPath;

    ImageSource? InfoIcon => DualActive ? ShellInfo.GetIcon(DualPane.Folder, true) : _active.Icon;
    string InfoTitle => DualActive ? FileSystem.DisplayName(DualPane.Folder) : _active.Title;

    /// <summary>Goes somewhere from the shared controls (address bar, tree, breadcrumb, menus): in the active list.</summary>
    void Go(string path, string? selectName = null)
    {
        // 내 PC and result lists only open in the main list.
        if (DualActive && !FileSystem.IsVirtual(path))
            DualPane.Navigate(path, selectName);
        else
            Navigate(path, selectName);
    }

    void SetActiveList(bool dual)
    {
        if (_dualActive == dual)
            return;
        _dualActive = dual;
        UpdateActivePane();
        UpdateChrome();
        UpdateNotePane();
    }

    void UpdateActivePane()
    {
        bool dual = DualPane.Visibility == Visibility.Visible;
        DualPane.SetActive(dual && _dualActive);
        if (dual && !_dualActive)
            MainPaneHeader.SetResourceReference(Border.BorderBrushProperty, "Th.Accent");
        else
            MainPaneHeader.BorderBrush = Brushes.Transparent;
    }

    void MainPaneHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => FileList.Focus();

    /// <summary>Address bar, breadcrumb, back/forward/up, favorite star, tree and title for the active list.</summary>
    void UpdateChrome()
    {
        if (!DualActive)
        {
            string path = _active.FullPath;
            AddressBox.Text = FileSystem.IsVirtual(path) ? FileSystem.DisplayName(path) : path;
            CrumbBar.ItemsSource = BuildCrumbs(path);
            BackButton.IsEnabled = _active.Back.Count > 0;
            ForwardButton.IsEnabled = _active.Forward.Count > 0;
            UpButton.IsEnabled = !InVirtual || CurrentSearch is { Kind: SearchKind.Here };
            UpdateFavoriteButton();
            SyncTree(path);
            UpdateDriveText(path);
            UpdateWindowTitle();
            return;
        }
        string folder = DualPane.Folder;
        AddressBox.Text = folder;
        CrumbBar.ItemsSource = BuildCrumbs(folder);
        BackButton.IsEnabled = DualPane.CanGoBack;
        ForwardButton.IsEnabled = DualPane.CanGoForward;
        UpButton.IsEnabled = Directory.GetParent(folder) != null;
        UpdateFavoriteButton();
        _favoriteAnchor = null;
        SyncTree(folder);
        UpdateDriveText(folder);
        UpdateWindowTitle();
    }

    // ── Showing and hiding the second list ──

    void DualPane_Click(object sender, RoutedEventArgs e) => SetDualPane(DualPane.Visibility != Visibility.Visible);
    void DualPaneMenu_Click(object sender, RoutedEventArgs e) => SetDualPane(DualPaneMenu.IsChecked);

    bool _dualHooked;

    void SetDualPane(bool show, string? folder = null)
    {
        if (!show && DualPane.Visibility == Visibility.Visible)
        {
            _dualRatio = DualRatioNow();
            if (_compareOn)
                SetCompare(false);
        }
        if (!_dualHooked)
        {
            _dualHooked = true;
            DualPane.Host = this;
            DualPane.Navigated += () =>
            {
                if (DualActive)
                    UpdateChrome();
                RecomputeCompare();
                ScheduleSave();
            };
            DualPane.SelectionChanged += () =>
            {
                if (DualActive)
                {
                    UpdateNotePane();
                    UpdateWindowTitle();
                }
            };
            DualPane.Reloaded += RecomputeCompare;
            DualPane.HeaderClicked += SortBy;
        }
        DualPane.Visibility = DualSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DualSplitterColumn.Width = new GridLength(show ? 5 : 0);
        // Star widths split the space between the two lists in the saved proportion.
        ListColumn.Width = new GridLength(show ? 1 - _dualRatio : 1, GridUnitType.Star);
        DualColumn.Width = show ? new GridLength(_dualRatio, GridUnitType.Star) : new GridLength(0);
        DualColumn.MinWidth = show ? 200 : 0;
        DualPaneMenu.IsChecked = show;
        DualToggle.IsChecked = show;
        CompareToggle.IsEnabled = show;
        MainPaneHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ApplyListOffsets();
        if (show)
        {
            SyncDualColumns();
            DualPane.SetSort(_sortKey, _sortDescending);
            if (DualPane.Folder.Length == 0)
            {
                string start = folder != null && Directory.Exists(folder) ? folder
                    : MainFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                DualPane.Navigate(start);
            }
        }
        else if (_dualActive)
        {
            SetActiveList(false);
            FileList.Focus();
        }
        UpdateActivePane();
        if (_saveTimer != null)
            ScheduleSave();
    }

    /// <summary>Room above the lists for the title strips (two lists) and the compare bar.</summary>
    void ApplyListOffsets()
    {
        bool dual = DualPane.Visibility == Visibility.Visible;
        double compare = _compareOn ? CompareBar.Height : 0;
        double strip = dual ? FolderPane.BarHeight : 0;
        double search = SearchPanel.Visibility == Visibility.Visible ? SearchPanel.ActualHeight : 0;
        MainPaneHeader.Margin = new Thickness(0, compare, 0, 0);
        DualPane.Margin = new Thickness(0, compare, 0, 0);
        SearchPanel.Margin = new Thickness(0, compare + strip, 0, 0);
        FileList.Margin = MarqueeCanvas.Margin = new Thickness(0, compare + strip + search, 0, 0);
        EmptyText.Margin = new Thickness(0, 60 + compare + strip + search, 0, 0);
    }

    double _dualRatio = 0.5;

    double DualRatioNow()
    {
        double main = ListColumn.ActualWidth, second = DualColumn.ActualWidth;
        return main + second > 0 && second > 0 ? Math.Clamp(second / (main + second), 0.15, 0.85) : _dualRatio;
    }

    /// <summary>The second list shows the main list's columns, in the same order and widths, with the same sort arrow.</summary>
    void SyncDualColumns()
    {
        if (DualPane.Visibility != Visibility.Visible)
            return;
        DualPane.SyncColumns(FileGridView.Columns
            .Where(c => ColumnKey(c) != "Folder")   // results-only column
            .Select(c => new PaneColumn(ColumnKey(c),
                (c.Header as GridViewColumnHeader)?.Content as string ?? ColumnTitle(ColumnKey(c)),
                double.IsNaN(c.Width) ? c.ActualWidth : c.Width,
                c.CellTemplate,
                c.DisplayMemberBinding)));
    }

    async void SendToDualPane(bool move)
    {
        var paths = SelectedItems.Select(i => i.FullPath).ToList();
        if (DualPane.Visibility != Visibility.Visible || paths.Count == 0)
            return;
        await DualPane.ReceiveAsync(paths, move);
        Refresh(reloadTree: false);
    }

    void CopyToDual_Click(object sender, RoutedEventArgs e) => SendToDualPane(move: false);
    void MoveToDual_Click(object sender, RoutedEventArgs e) => SendToDualPane(move: true);

    // ── 폴더 비교 ──

    bool _compareOn, _compareDiffOnly;

    void CompareToggle_Click(object sender, RoutedEventArgs e) => SetCompare(!_compareOn);
    void CompareMenu_Click(object sender, RoutedEventArgs e) => SetCompare(CompareMenu.IsChecked);
    void CompareOff_Click(object sender, RoutedEventArgs e) => SetCompare(false);

    void SetCompare(bool on)
    {
        if (on && DualPane.Visibility != Visibility.Visible)
            SetDualPane(true);
        _compareOn = on;
        CompareBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CompareToggle.IsChecked = on;
        CompareMenu.IsChecked = on;
        ShowAutoColumn("Compare", on);
        SyncDualColumns();
        ApplyListOffsets();
        if (!on)
        {
            _compareDiffOnly = false;
            CompareDiffOnly.IsChecked = false;
            foreach (var item in AllMainItems.Concat(DualPane.AllItems))
                item.CompareState = 0;
            _view?.Refresh();
            DualPane.SetFilter(PaneFilter);
        }
        RecomputeCompare();
    }

    IEnumerable<FileItem> AllMainItems => _view?.SourceCollection.Cast<FileItem>() ?? [];

    /// <summary>Marks every item in both lists against the other, and updates the compare bar.</summary>
    void RecomputeCompare()
    {
        if (!_compareOn)
            return;
        if (MainFolder == null || DualPane.Folder.Length == 0)
        {
            CompareSummary.Text = "비교하려면 왼쪽에 실제 폴더를 여세요.";
            return;
        }
        var result = FolderCompare.Mark(AllMainItems.ToList(), DualPane.AllItems.ToList());
        CompareSummary.Text = string.Equals(MainFolder, DualPane.Folder, StringComparison.OrdinalIgnoreCase)
            ? "양쪽이 같은 폴더입니다. 오른쪽에서 다른 폴더를 여세요."
            : $"왼쪽에만 {result.LeftOnly:N0} · 오른쪽에만 {result.RightOnly:N0} · 다름 {result.Different:N0} · 같음 {result.Same:N0}";
        CopyRightButton.IsEnabled = result.LeftOnly + result.LeftNewer > 0;
        CopyLeftButton.IsEnabled = result.RightOnly + result.RightNewer > 0;
        CopyRightButton.ToolTip = $"왼쪽에만 있는 {result.LeftOnly:N0}개와 왼쪽이 더 최신인 {result.LeftNewer:N0}개를 오른쪽으로 복사";
        CopyLeftButton.ToolTip = $"오른쪽에만 있는 {result.RightOnly:N0}개와 오른쪽이 더 최신인 {result.RightNewer:N0}개를 왼쪽으로 복사";
        _view?.Refresh();
        DualPane.SetFilter(PaneFilter);
    }

    void CompareDiffOnly_Click(object sender, RoutedEventArgs e)
    {
        _compareDiffOnly = CompareDiffOnly.IsChecked == true;
        _view?.Refresh();
        DualPane.SetFilter(PaneFilter);
        UpdateStatus();
    }

    /// <summary>"다른 것만 보기" hides items that are the same on both sides.</summary>
    bool CompareHides(FileItem item) => _compareOn && _compareDiffOnly && item.CompareState == FolderCompare.Same;

    void CopyRight_Click(object sender, RoutedEventArgs e) => SyncSide(toRight: true);
    void CopyLeft_Click(object sender, RoutedEventArgs e) => SyncSide(toRight: false);

    /// <summary>
    /// Copies what one side has and the other lacks or has older. Older copies on the other side go to the
    /// Recycle Bin first, so one Ctrl+Z puts everything back exactly as it was.
    /// </summary>
    async void SyncSide(bool toRight)
    {
        if (MainFolder is not string left || DualPane.Folder.Length == 0)
            return;
        string target = toRight ? DualPane.Folder : left;
        var source = (toRight ? AllMainItems : DualPane.AllItems)
            .Where(i => i.CompareState is FolderCompare.OnlyHere or FolderCompare.Newer)
            .ToList();
        if (source.Count == 0)
            return;
        int replacing = source.Count(i => i.CompareState == FolderCompare.Newer);
        string question = $"{(toRight ? "왼쪽 → 오른쪽" : "오른쪽 → 왼쪽")}으로 {source.Count:N0}개를 복사합니다." +
                          (replacing > 0 ? $"\n그중 {replacing:N0}개는 반대쪽의 더 오래된 파일을 대신합니다 (오래된 파일은 휴지통으로)." : "") +
                          "\n\nCtrl+Z로 한 번에 되돌릴 수 있습니다.";
        if (MessageBox.Show(this, question, "폴더 비교 — 복사", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var older = source.Where(i => i.CompareState == FolderCompare.Newer)
            .Select(i => Path.Combine(target, i.Name))
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToList();
        var steps = new List<UndoAction>();
        if (older.Count > 0)
        {
            var started = DateTime.Now;
            await FileOps.DeleteAsync(WindowHandle, older, permanently: false, noConfirm: true, record: false);
            var gone = older.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
            if (gone.Count > 0)
                steps.Add(new DeleteUndo(gone, started, DateTime.Now));
        }
        await FileOps.TransferAsync(WindowHandle, source.Select(i => i.FullPath).ToList(), target, move: false, record: false);
        var created = source.Select(i => Path.Combine(target, i.Name)).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (created.Count > 0)
            steps.Add(new CreateUndo(created, "비교 복사"));
        if (steps.Count > 0)
            UndoStack.Push(new GroupUndo(steps, $"폴더 비교 복사 ({created.Count:N0}개)"));
        DualPane.Reload();
        await LoadFilesAsync(_active.FullPath);
        ShowStatusNote($"{created.Count:N0}개를 {(toRight ? "오른쪽" : "왼쪽")}으로 복사했습니다 (Ctrl+Z로 되돌리기)");
    }

    // ── Columns shown only for a purpose (search results' 위치, compare's 비교) ──

    readonly HashSet<string> _autoColumns = new();

    /// <summary>Shows a column right after the name without changing the saved column choice.</summary>
    void ShowAutoColumn(string key, bool show)
    {
        bool visible = FileGridView.Columns.Any(c => ColumnKey(c) == key);
        if (show && !visible)
        {
            var column = AllColumns.First(c => ColumnKey(c) == key);
            var name = FileGridView.Columns.FirstOrDefault(c => ColumnKey(c) == "Name");
            FileGridView.Columns.Insert(name == null ? 0 : FileGridView.Columns.IndexOf(name) + 1, column);
            _autoColumns.Add(key);
        }
        else if (!show && _autoColumns.Remove(key))
            SetColumnVisible(key, false);
    }
}

/// <summary>Compares two folder listings by name: which side has it, and if both, whether it differs.</summary>
public static class FolderCompare
{
    public const int None = 0, OnlyHere = 1, Newer = 2, Older = 3, Different = 4, Same = 5;

    public sealed record Result(int LeftOnly, int RightOnly, int LeftNewer, int RightNewer, int Different, int Same);

    /// <summary>
    /// Same = same size and modified time (2 seconds apart at most: FAT and zip keep times that coarsely).
    /// Folders present on both sides count as the same (their contents are not compared).
    /// </summary>
    public static Result Mark(List<FileItem> left, List<FileItem> right)
    {
        var byName = right.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<FileItem>();
        int leftOnly = 0, leftNewer = 0, rightNewer = 0, different = 0, same = 0;
        foreach (var a in left)
        {
            if (!byName.TryGetValue(a.Name, out var b))
            {
                a.CompareState = OnlyHere;
                leftOnly++;
                continue;
            }
            matched.Add(b);
            if (a.IsDirectory && b.IsDirectory)
            {
                a.CompareState = b.CompareState = Same;
                same++;
                continue;
            }
            if (a.IsDirectory != b.IsDirectory)
            {
                a.CompareState = b.CompareState = Different;
                different++;
                continue;
            }
            double seconds = (a.Modified - b.Modified).TotalSeconds;
            if (a.Size == b.Size && Math.Abs(seconds) <= 2)
            {
                a.CompareState = b.CompareState = Same;
                same++;
            }
            else if (Math.Abs(seconds) <= 2)
            {
                a.CompareState = b.CompareState = Different;   // same time, different size
                different++;
            }
            else if (seconds > 0)
            {
                (a.CompareState, b.CompareState) = (Newer, Older);
                leftNewer++;
                different++;
            }
            else
            {
                (a.CompareState, b.CompareState) = (Older, Newer);
                rightNewer++;
                different++;
            }
        }
        int rightOnly = 0;
        foreach (var b in right.Where(b => !matched.Contains(b)))
        {
            b.CompareState = OnlyHere;
            rightOnly++;
        }
        return new Result(leftOnly, rightOnly, leftNewer, rightNewer, different, same);
    }
}
