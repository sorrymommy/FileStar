using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileStar;

/// <summary>
/// 검색 탭: Ctrl+F (or the 검색 button) opens a tab whose top panel holds the search word, what to look in
/// (이름·확장자·태그·메모) and where (a folder, with or without its subfolders, or every memo and tag). The
/// results fill the list under it, and stay a location of their own (back/forward, F5, saved with the tabs).
/// </summary>
public partial class MainWindow
{
    /// <summary>The options last used, for the next search tab (saved in the settings).</summary>
    SearchFields _searchFields = SearchFields.Default;
    bool _searchSubfolders = true;

    /// <summary>The folder the 위치 choice offers (kept when switching to 모든 위치 and back).</summary>
    string? _searchFolder;

    bool _syncingSearchPanel;

    void SearchButton_Click(object sender, RoutedEventArgs e) => OpenSearch();

    /// <summary>Focuses this tab's search panel, or opens a search tab for the folder being looked at.</summary>
    void OpenSearch()
    {
        if (!DualActive && CurrentSearch is { Kind: not SearchKind.Tag })
        {
            FocusSearchBox();
            return;
        }
        string? folder = DualActive ? DualPane.Folder
            : CurrentSearch is { Kind: SearchKind.Here } here ? here.Base
            : InVirtual ? null
            : _active.FullPath;
        if (folder != null && FileSystem.IsVirtual(folder))
            folder = null;
        NewTab(SearchLocation.Find(folder, "", _searchFields, _searchSubfolders).Path);
        FocusSearchBox();
    }

    void FocusSearchBox() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        SearchQueryBox.Focus();
        SearchQueryBox.SelectAll();
    });

    /// <summary>Shows the panel on search tabs, filled in from the search the tab holds.</summary>
    void UpdateSearchPanel()
    {
        var search = CurrentSearch is { Kind: not SearchKind.Tag } s ? s : null;
        SearchPanel.Visibility = search != null ? Visibility.Visible : Visibility.Collapsed;
        if (search != null)
        {
            _syncingSearchPanel = true;
            if (search.Kind == SearchKind.Here)
                _searchFolder = search.Base;
            SearchQueryBox.Text = search.Query;
            SearchNameCheck.IsChecked = search.Fields.HasFlag(SearchFields.Name);
            SearchExtCheck.IsChecked = search.Fields.HasFlag(SearchFields.Extension);
            SearchTagCheck.IsChecked = search.Fields.HasFlag(SearchFields.Tag);
            SearchMemoCheck.IsChecked = search.Fields.HasFlag(SearchFields.Memo);
            SearchSubfoldersCheck.IsChecked = search.Subfolders;
            SearchScopeFolder.Content = _searchFolder != null ? "폴더: " + FileSystem.DisplayName(_searchFolder) : "폴더 (없음)";
            SearchScopeFolder.ToolTip = _searchFolder;
            SearchScopeFolder.IsEnabled = _searchFolder != null;
            SearchScopeBox.SelectedIndex = search.Kind == SearchKind.Here ? 0 : 1;
            UpdateSearchScopeOptions();
            _syncingSearchPanel = false;
        }
        ApplyListOffsets();
    }

    /// <summary>Names, extensions and subfolders only mean something when a folder is searched.</summary>
    void UpdateSearchScopeOptions()
    {
        bool folder = SearchScopeBox.SelectedIndex == 0;
        SearchNameCheck.IsEnabled = SearchExtCheck.IsEnabled = SearchSubfoldersCheck.IsEnabled = folder;
    }

    SearchFields PanelFields()
    {
        var fields = SearchFields.None;
        if (SearchNameCheck.IsChecked == true) fields |= SearchFields.Name;
        if (SearchExtCheck.IsChecked == true) fields |= SearchFields.Extension;
        if (SearchTagCheck.IsChecked == true) fields |= SearchFields.Tag;
        if (SearchMemoCheck.IsChecked == true) fields |= SearchFields.Memo;
        return fields;
    }

    /// <summary>Runs the search the panel describes, in this tab (the previous one stays in 뒤로).</summary>
    void RunPanelSearch()
    {
        bool inFolder = SearchScopeBox.SelectedIndex == 0 && _searchFolder != null;
        var fields = PanelFields();
        if ((inFolder ? fields : fields & (SearchFields.Tag | SearchFields.Memo)) == SearchFields.None)
        {
            ShowStatusNote(inFolder ? "찾을 대상을 하나 이상 고르세요." : "모든 위치에서는 태그나 메모에서만 찾을 수 있습니다.");
            return;
        }
        _searchFields = fields;
        _searchSubfolders = SearchSubfoldersCheck.IsChecked == true;
        ScheduleSave();
        var search = SearchLocation.Find(inFolder ? _searchFolder : null, SearchQueryBox.Text, fields, _searchSubfolders);
        if (search.Path == _active.FullPath)
            Refresh();
        else
            Navigate(search.Path);
    }

    void SearchGo_Click(object sender, RoutedEventArgs e) => RunPanelSearch();

    void SearchQueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunPanelSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Down || e.Key == Key.Escape)
        {
            FileList.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Changing what to look in redoes the search right away (when there is something to look for).</summary>
    void SearchOption_Click(object sender, RoutedEventArgs e)
    {
        if (!_syncingSearchPanel && SearchQueryBox.Text.Trim().Length > 0)
            RunPanelSearch();
    }

    void SearchScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSearchPanel || SearchPanel == null)
            return;
        UpdateSearchScopeOptions();
        if (SearchQueryBox.Text.Trim().Length > 0)
            RunPanelSearch();
    }

    void SearchPanel_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyListOffsets();
}
