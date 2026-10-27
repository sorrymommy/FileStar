using System.Windows;
using System.Windows.Controls;

namespace FileStar.Views;

// The file list's GridView columns: which show, in what order and width (saved with the settings), the
// columns shown only for a purpose (search results' 위치, compare's 비교), the sort arrow, and the second
// list showing exactly the same columns.
public partial class MainWindow
{
    static readonly Dictionary<string, string> ColumnLabels = new()
    {
        ["Name"] = "이름",
        ["Note"] = "메모",
        ["Tags"] = "태그",
        ["Ext"] = "확장자",
        ["Size"] = "크기",
        ["Type"] = "유형",
        ["Folder"] = "위치",
        ["Compare"] = "비교",
        ["Modified"] = "수정한 날짜",
        ["Created"] = "만든 날짜",
        ["Accessed"] = "액세스한 날짜",
        ["Attributes"] = "특성",
    };

    static readonly HashSet<string> HiddenByDefault = new() { "Accessed", "Attributes", "Folder", "Compare" };

    readonly HashSet<string> _autoColumns = new();
    List<GridViewColumn>? _allColumns;

    GridView FileGridView => (GridView)FileList.View;

    /// <summary>Every column defined in XAML, in default order (captured before any are hidden).</summary>
    List<GridViewColumn> AllColumns => _allColumns ??= FileGridView.Columns.ToList();

    static string ColumnKey(GridViewColumn column) => (column.Header as GridViewColumnHeader)?.Tag as string ?? "";

    static string ColumnTitle(string key) => key == "Index" ? "번호 (#)" : ColumnLabels.GetValueOrDefault(key, key);

    void FileList_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string key })
            _vm.SortBy(key);
    }

    void UpdateSortHeaders()
    {
        // All columns, including hidden ones, so a column shown later already has its title.
        foreach (var column in AllColumns)
        {
            if (column.Header is GridViewColumnHeader { Tag: string key } header && ColumnLabels.TryGetValue(key, out var label))
                header.Content = key == _vm.FileList.SortKey ? label + (_vm.FileList.SortDescending ? "  ▼" : "  ▲") : label;
        }
    }

    /// <summary>Shows columns in the saved order and widths; null restores the defaults.</summary>
    void ApplyColumnSettings(List<ColumnSetting>? saved)
    {
        var all = AllColumns;
        FileGridView.Columns.Clear();
        if (saved == null)
        {
            foreach (var column in all.Where(c => !HiddenByDefault.Contains(ColumnKey(c))))
                FileGridView.Columns.Add(column);
            return;
        }
        foreach (var setting in saved)
        {
            var column = all.FirstOrDefault(c => ColumnKey(c) == setting.Key);
            if (column == null)
                continue;
            if (setting.Width > 10)
                column.Width = setting.Width;
            if (setting.Visible && !FileGridView.Columns.Contains(column))
                FileGridView.Columns.Add(column);
        }
        // Columns added in a newer version than the saved settings.
        foreach (var column in all.Where(c => saved.All(s => s.Key != ColumnKey(c)) && !HiddenByDefault.Contains(ColumnKey(c))))
            SetColumnVisible(ColumnKey(column), true);
        if (!FileGridView.Columns.Any(c => ColumnKey(c) == "Name"))
            SetColumnVisible("Name", true);
    }

    void SetColumnVisible(string key, bool visible)
    {
        var column = AllColumns.FirstOrDefault(c => ColumnKey(c) == key);
        if (column == null || visible == FileGridView.Columns.Contains(column))
            return;
        if (!visible)
        {
            FileGridView.Columns.Remove(column);
            return;
        }
        // Put it back after the nearest column that precedes it in the default order (keeps user reordering).
        int defaultIndex = AllColumns.IndexOf(column);
        var before = AllColumns.Take(defaultIndex).LastOrDefault(c => FileGridView.Columns.Contains(c));
        FileGridView.Columns.Insert(before == null ? 0 : FileGridView.Columns.IndexOf(before) + 1, column);
    }

    /// <summary>Columns shown only for a purpose (위치 in results, 비교 while comparing) are not the user's choice: saved as hidden.</summary>
    List<ColumnSetting> CurrentColumnSettings() =>
        FileGridView.Columns.Select(c => new ColumnSetting(ColumnKey(c), !_autoColumns.Contains(ColumnKey(c)),
                double.IsNaN(c.Width) ? c.ActualWidth : c.Width))
            .Concat(AllColumns.Where(c => !FileGridView.Columns.Contains(c))
                .Select(c => new ColumnSetting(ColumnKey(c), false, double.IsNaN(c.Width) ? 100 : c.Width)))
            .ToList();

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

    /// <summary>One checkable entry per column (이름 is always shown), plus "reset".</summary>
    void FillColumnMenu(ItemCollection items)
    {
        items.Clear();
        foreach (var column in AllColumns)
        {
            string key = ColumnKey(column);
            var item = new MenuItem
            {
                Header = ColumnTitle(key),
                IsCheckable = true,
                IsChecked = FileGridView.Columns.Contains(column),
                IsEnabled = key != "Name",
                StaysOpenOnClick = true,
            };
            // Checked/Unchecked rather than Click, so keyboard and accessibility toggles work too.
            item.Checked += (_, _) => { SetColumnVisible(key, true); SyncDualColumns(); ScheduleSave(); };
            item.Unchecked += (_, _) => { SetColumnVisible(key, false); SyncDualColumns(); ScheduleSave(); };
            items.Add(item);
        }
        items.Add(new Separator());
        var reset = new MenuItem { Header = "기본 열로 되돌리기" };
        reset.Click += (_, _) => { ApplyColumnSettings(null); SyncDualColumns(); ScheduleSave(); };
        items.Add(reset);
        items.Add(new MenuItem
        {
            Header = "열 머리글을 끌어서 순서를, 경계선을 끌어서 너비를 바꿀 수 있습니다",
            IsEnabled = false,
        });
    }

    void ColumnsButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ColumnsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, FontFamily = FontFamily, FontSize = FontSize };   // not the button's icon font
        FillColumnMenu(menu.Items);
        menu.IsOpen = true;
    }

    void ColumnMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
            FillColumnMenu(menu.Items);
    }

    void ColumnsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource == ColumnsMenu)
            FillColumnMenu(ColumnsMenu.Items);
    }

    /// <summary>The second list shows the main list's columns, in the same order and widths, with the same sort arrow.</summary>
    void SyncDualColumns()
    {
        if (!_vm.IsDualVisible)
            return;
        DualPane.SyncColumns(FileGridView.Columns
            .Where(c => ColumnKey(c) != "Folder")   // results-only column
            .Select(c => new PaneColumn(ColumnKey(c),
                (c.Header as GridViewColumnHeader)?.Content as string ?? ColumnTitle(ColumnKey(c)),
                double.IsNaN(c.Width) ? c.ActualWidth : c.Width,
                c.CellTemplate,
                c.DisplayMemberBinding)));
    }
}
