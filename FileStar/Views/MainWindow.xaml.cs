using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileStar.Views;

/// <summary>
/// The main window's view. Everything with state or rules lives in <see cref="MainViewModel"/>; what stays here is
/// what only a view can do: keyboard routing that depends on focus, mouse gestures (selection rectangle, drag and
/// drop), the tree's scrolling, GridView columns, the Windows shell menu, previews and the window's own layout.
/// </summary>
public partial class MainWindow : Window
{
    readonly MainViewModel _vm;
    readonly DispatcherTimer _detailsTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _periodicSave = new() { Interval = TimeSpan.FromSeconds(20) };

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        ContentRendered += (_, _) => Perf.Log("first frame on screen");
        InitializeComponent();

        ApplyLayout(vm.Settings);
        WireViewModel();
        WireWindow();
        vm.Start();
    }

    void WireViewModel()
    {
        _vm.LayoutProvider = CurrentLayout;
        _vm.IsShiftDown = () => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _vm.FocusRequested += MoveFocus;
        _vm.SaveRequested += ScheduleSave;
        // Shell property lookups take a moment; wait until selection settles (e.g. while arrowing through files).
        _vm.DetailsRequested += () =>
        {
            _detailsTimer.Stop();
            _detailsTimer.Start();
        };
        _vm.SortChanged += () =>
        {
            UpdateSortHeaders();
            SyncDualColumns();
        };
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Notes.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NotePaneViewModel.IsEditing) && _vm.Notes.IsEditing)
                MoveFocus(ViewModels.FocusTarget.NoteBox);
        };
        _vm.FileList.SelectionRequested += SelectRows;
        _vm.FileList.ScrollToTopRequested += () =>
        {
            if (FileList.Items.Count > 0)
                FileList.ScrollIntoView(FileList.Items[0]);
        };
        _vm.Tree.BringIntoViewRequested += chain =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => BringTreeChainIntoView(chain));
        MenuBuilder.Fill(FavoritesMenu.Items, _vm.FavoritesMenu);
    }

    void WireWindow()
    {
        _detailsTimer.Tick += (_, _) =>
        {
            _detailsTimer.Stop();
            _vm.UpdateProperties();
            UpdatePreviewPane();
        };
        // Save the session as it changes, not only on a normal close: a Windows restart or sign-out skips
        // Closing, and so would a crash. Moves and tab changes save shortly after; everything else is
        // picked up by the periodic save.
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _vm.SaveSettings();
        };
        _periodicSave.Tick += (_, _) => _vm.SaveSettings();
        _periodicSave.Start();
        Application.Current.SessionEnding += (_, _) => _vm.SaveSettings();
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();

        // Which list has the keyboard decides the "active pane" line in 두 창 보기.
        FileList.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                _vm.SetActiveList(false);
        };
        DualPane.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                _vm.SetActiveList(true);
        };
        SearchPanel.IsVisibleChanged += (_, _) => ApplyListOffsets();
        DualPane.HeaderClicked += _vm.SortBy;
        // Coming back to this window: another FileStar window may have changed memos, and files may have been
        // moved in another program while FileStar was closed or in the background.
        Activated += async (_, _) => await _vm.OnActivatedAsync();
        _windowsMenuHover.Tick += WindowsMenuHover_Tick;
        Loaded += async (_, _) =>
        {
            SetGoMenuIcons();
            // Once the window is up and idle, load the Windows menu's shell extensions in the background.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var sampleFile = _vm.FileList.AllItems.FirstOrDefault(i => !i.IsDirectory)?.FullPath ?? Environment.ProcessPath;
                ShellContextMenu.PrewarmInBackground(sampleFile,
                    _vm.InVirtual ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _vm.ActiveTab.FullPath);
            });
            await _vm.OnLoadedAsync();
        };
    }

    void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsDualVisible):
                ApplyDualLayout(_vm.IsDualVisible);
                break;
            case nameof(MainViewModel.IsInfoPaneVisible):
                ApplyInfoPaneLayout(_vm.IsInfoPaneVisible);
                break;
            case nameof(MainViewModel.IsCompareOn):
                ShowAutoColumn("Compare", _vm.IsCompareOn);
                SyncDualColumns();
                ApplyListOffsets();
                break;
            case nameof(MainViewModel.IsResultList):
                ShowAutoColumn("Folder", _vm.IsResultList);
                break;
            case nameof(MainViewModel.FavoritesMenu):
                MenuBuilder.Fill(FavoritesMenu.Items, _vm.FavoritesMenu);
                break;
        }
    }

    void MoveFocus(FocusTarget target)
    {
        switch (target)
        {
            case ViewModels.FocusTarget.FileList:
                if (FileList.SelectedItem != null && FileList.ItemContainerGenerator.ContainerFromItem(FileList.SelectedItem) is ListViewItem row)
                    row.Focus();
                else
                    FileList.Focus();
                break;
            case ViewModels.FocusTarget.Tree:
                FolderTree.Focus();
                break;
            case ViewModels.FocusTarget.TreeIfFocused:
                if (FolderTree.IsKeyboardFocusWithin || Keyboard.FocusedElement == null)
                    FolderTree.Focus();
                break;
            case ViewModels.FocusTarget.SearchBox:
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    SearchQueryBox.Focus();
                    SearchQueryBox.SelectAll();
                });
                break;
            case ViewModels.FocusTarget.NoteBox:
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    NoteBox.Focus();
                    NoteBox.CaretIndex = NoteBox.Text.Length;
                });
                break;
        }
    }

    // ───────────── Keyboard ─────────────

    static bool IsTypingInTextBox =>
        Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox || Keyboard.FocusedElement is FrameworkElement { TemplatedParent: ComboBox };

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool none = mods == ModifierKeys.None;
        bool ctrl = mods == ModifierKeys.Control;
        bool alt = mods == ModifierKeys.Alt;
        bool shift = mods == ModifierKeys.Shift;
        bool ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);
        bool altShift = mods == (ModifierKeys.Alt | ModifierKeys.Shift);
        bool typing = IsTypingInTextBox;
        // The second pane handles its own list keys (Backspace, Del, F2, F5, Ctrl+A/C/X/V, Alt+↑).
        if (DualPane.HasFocus && (key is Key.Back or Key.Delete or Key.F2 or Key.F5 || ctrl && key is Key.A or Key.C or Key.X or Key.V ||
                                  alt && key == Key.Up))
            return;
        bool inList = FileList.IsKeyboardFocusWithin && !typing;

        // ── Windows Explorer shortcuts ──
        // Navigation
        if (alt && key == Key.Left) _vm.GoBack();
        else if (alt && key == Key.Right) _vm.GoForward();
        else if (alt && key == Key.Up) _vm.GoUp();
        else if (none && key == Key.Back && !typing) _vm.GoBack();                    // Windows 10/11: Backspace = back
        else if ((ctrl && key == Key.L) || (alt && key == Key.D) || (none && key == Key.F4))
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        }
        else if ((ctrl && key is Key.E or Key.F) || (none && key == Key.F3)) _vm.OpenSearch();
        else if ((none && key == Key.F5) || (ctrl && key == Key.R)) _vm.Refresh();
        else if ((none || shift) && key == Key.F6) CyclePaneFocus(forward: none);
        else if (none && key == Key.F11) ToggleFullScreen();
        // Files
        else if (ctrl && key == Key.A && !typing)
        {
            FileList.Focus();
            FileList.SelectAll();
        }
        else if (FolderTree.IsKeyboardFocusWithin && !typing && HandleTreeKey(key, mods)) { }
        else if (ctrl && key is Key.C or Key.X or Key.V && HandleFileClipboardKey(key)) { }
        else if (inList && ((key == Key.Delete && (none || shift)) || (ctrl && key == Key.D))) _ = _vm.DeleteSelectedAsync(permanently: shift);
        else if (inList && ctrl && key == Key.Enter) _vm.OpenSelectedFoldersInNewTabs();
        else if (ctrlShift && key == Key.N) _vm.NewFolder();
        // Windows and tabs
        else if (ctrl && key == Key.N) _vm.OpenNewWindow();
        else if (ctrl && key == Key.T) _vm.NewTab(_vm.ActiveTab.FullPath);
        else if (ctrl && key == Key.W) _vm.CloseTab(_vm.ActiveTab);
        else if (ctrl && key == Key.Tab) _vm.CycleTab(1);
        else if (ctrlShift && key == Key.Tab) _vm.CycleTab(-1);
        // Panes
        else if (alt && key == Key.P) _vm.ToggleInfoTab(MainViewModel.PreviewTab);
        else if (altShift && key == Key.P) _vm.ToggleInfoTab(MainViewModel.PropertiesTab);
        else if (ctrlShift && key == Key.P) _vm.Navigate(Locations.ThisPc);
        else if (ctrlShift && key == Key.H) _vm.ShowNoteMarks = !_vm.ShowNoteMarks;
        else if (ctrl && key == Key.Z && !typing) _ = _vm.UndoLastAsync();
        else if (ctrlShift && key == Key.D) _vm.IsDualVisible = !_vm.IsDualVisible;
        else if (ctrlShift && key == Key.K) _vm.IsCompareOn = !_vm.IsCompareOn;
        else if (ctrlShift && key == Key.E)                                          // expand tree to current folder
        {
            _vm.Tree.Sync(_vm.ActiveTab.FullPath);
            FolderTree.Focus();
        }
        else if (none && key == Key.F1) _vm.ShortcutHelpCommand.Execute(null);
        // ── FileStar's own ──
        else if (ctrl && key == Key.H) _vm.ShowHidden = !_vm.ShowHidden;
        else if (ctrl && key == Key.B) _vm.ToggleFavorite();
        else if (ctrl && key == Key.M) _vm.EditNote();
        else if (ctrlShift && key == Key.M) _ = _vm.RunAiSummaryAsync();
        else if (none && key == Key.F9) _vm.IsInfoPaneVisible = !_vm.IsInfoPaneVisible;
        else if (ctrl && key == Key.S && _vm.Notes.IsEditing) _vm.Notes.SaveEdit();
        else if (none && key == Key.Escape && NoteBox.IsKeyboardFocusWithin)
        {
            _vm.Notes.ResolvePendingEdit();
            FileList.Focus();
        }
        else if (ctrl && key is >= Key.D1 and <= Key.D9 && Keyboard.FocusedElement is not TextBox) _vm.OpenFavorite(key - Key.D1);
        else return;
        e.Handled = true;
    }

    /// <summary>Ctrl+C/X/V act on files unless the user is typing in a text box (there they copy text).</summary>
    bool HandleFileClipboardKey(Key key)
    {
        if (IsTypingInTextBox || !FileList.IsKeyboardFocusWithin)
            return false;
        switch (key)
        {
            case Key.C:
                _vm.ClipboardSelected(cut: false);
                return true;
            case Key.X:
                _vm.ClipboardSelected(cut: true);
                return true;
            case Key.V:
                _ = _vm.PasteIntoCurrentFolderAsync();
                return true;
            default:
                return false;
        }
    }

    bool HandleTreeKey(Key key, ModifierKeys mods)
    {
        bool none = mods == ModifierKeys.None, ctrl = mods == ModifierKeys.Control, shift = mods == ModifierKeys.Shift;
        if (ctrl && key == Key.C) _vm.TreeClipboard(cut: false);
        else if (ctrl && key == Key.X) _vm.TreeClipboard(cut: true);
        else if (ctrl && key == Key.V) _ = _vm.TreePasteAsync();
        else if ((key == Key.Delete && (none || shift)) || (ctrl && key == Key.D)) _ = _vm.TreeDeleteAsync(permanently: shift);
        else if (none && key == Key.F2) _vm.BeginTreeRename();
        else
            return false;
        return true;
    }

    /// <summary>F6 / Shift+F6: address bar → folder tree → file list → info pane → …, like Explorer.</summary>
    void CyclePaneFocus(bool forward)
    {
        var panes = new List<FrameworkElement> { AddressBox, FolderTree, FileList };
        if (NotePane.Visibility == Visibility.Visible)
            panes.Add(InfoTabs);
        int current = panes.FindIndex(p => p.IsKeyboardFocusWithin);
        var next = panes[((current < 0 ? (forward ? -1 : 0) : current) + (forward ? 1 : panes.Count - 1)) % panes.Count];
        if (next == FileList)
            MoveFocus(ViewModels.FocusTarget.FileList);
        else if (next == FolderTree && _vm.Tree.Selected != null)
        {
            FolderTree.Focus();
            BringTreeChainIntoView(_vm.Tree.PathChain);
        }
        else
            next.Focus();
    }

    void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Mouse side buttons
        if (e.ChangedButton == MouseButton.XButton1) _vm.GoBack();
        else if (e.ChangedButton == MouseButton.XButton2) _vm.GoForward();
    }

    // ───────────── Address bar, search box, memo box ─────────────

    void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            _vm.ResetAddress();
        else if (e.Key == Key.Enter)
            _vm.CommitAddress();
    }

    void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBox.SelectAll();

    void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The first click only focuses (selecting everything); otherwise the click would immediately
        // move the caret to the click point and undo the selection. Later clicks place the caret normally.
        if (AddressBox.IsKeyboardFocusWithin)
            return;
        AddressBox.Focus();
        e.Handled = true;
    }

    void SearchQueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.Search.Run();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Escape)
        {
            FileList.Focus();
            e.Handled = true;
        }
    }

    void SearchPanel_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyListOffsets();

    void NoteBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.Notes.IsEditing)
            _vm.Notes.BeginEdit();
    }

    void TagInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        _vm.Notes.AddTypedTags();
        e.Handled = true;
    }

    // Suggest tags used elsewhere that this item doesn't have yet.
    void TagInput_DropDownOpened(object sender, EventArgs e) => TagInput.ItemsSource = _vm.Notes.TagSuggestions();

    void TagInput_DropDownClosed(object sender, EventArgs e)
    {
        if (TagInput.SelectedItem is string tag)
            _vm.Notes.AddTags(tag);
        TagInput.SelectedItem = null;
        _vm.Notes.TagInput = "";
    }

    // ───────────── Info pane ─────────────

    void InfoTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != InfoTabs)
            return;
        _vm.UpdateProperties();
        // Release the previewed file (and any preview handler process) as soon as the tab is left.
        if (_vm.IsPreviewTabVisible)
            UpdatePreviewPane(force: true);
        else
            ClearPreview();
    }

    void PropsChooser_Click(object sender, RoutedEventArgs e)
    {
        var properties = _vm.Properties;
        var menu = new ContextMenu { PlacementTarget = PropsChooserButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, FontFamily = FontFamily, FontSize = FontSize };
        menu.Items.Add(new MenuItem { Header = "표시할 속성", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var (label, visible) in properties.ChooserItems())
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = visible, StaysOpenOnClick = true };
            item.Checked += (_, _) => properties.SetVisible(label, true);
            item.Unchecked += (_, _) => properties.SetVisible(label, false);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuBuilder.Create(MenuItemViewModel.Action("모두 표시", properties.ShowAll, properties.AnyHidden)));
        menu.Items.Add(MenuBuilder.Create(MenuItemViewModel.Action($"이름 칸 폭 기본값 ({PropertiesViewModel.DefaultLabelWidth:0})", properties.ResetLabelWidth)));
        menu.IsOpen = true;
    }

    void PropsSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _vm.Properties.CommitLabelWidth();

    // ───────────── Menus and buttons that open a menu of their own ─────────────

    void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.FavoriteButtonClicked() is { } choices)
            MenuBuilder.ShowBelow(FavoriteButton, choices, FontFamily, FontSize);   // groups exist: let the user pick where to put it
    }

    void RecentMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem menu && (menu == RecentFoldersMenu || menu == RecentFilesMenu))
            MenuBuilder.Fill(menu.Items, _vm.RecentMenu(folders: menu == RecentFoldersMenu));
    }

    /// <summary>내 PC, 바탕 화면, 문서 … in the 이동 menu show the same icons Windows uses for them.</summary>
    void SetGoMenuIcons()
    {
        foreach (var item in GoMenu.Items.OfType<MenuItem>())
        {
            if (ThisPcItems.SpecialFolder(item.Tag as string) is not string path)
                continue;
            var icon = Locations.IsThisPc(path) ? ShellInfo.ThisPcIcon
                : Directory.Exists(path) ? ShellInfo.GetIcon(path, true, exact: true) : null;
            if (icon != null)
                item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
        }
    }

    void MainPaneHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => FileList.Focus();

    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // On release, not press (a MouseBinding acts on press): moving off before letting go still cancels.
    void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: BrowserTab tab })
            _vm.CloseTabCommand.Execute(tab);
    }

    void TagChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string tag })
            _vm.Notes.OpenTagCommand.Execute(tag);
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        _saveTimer.Stop();
        _periodicSave.Stop();
        PreviewHandlerHost.Unload();
        _vm.OnClosing();
    }
}
