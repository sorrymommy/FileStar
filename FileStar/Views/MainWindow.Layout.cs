using System.Windows;

namespace FileStar.Views;

// The window's own layout: size and place, the widths of the tree, the second list and the info pane, the
// room above the lists for title strips and bars, and full screen. Saved through MainViewModel.SaveSettings.
public partial class MainWindow
{
    double _notePaneWidth = 340, _dualRatio = 0.5;
    WindowState _stateBeforeFullScreen;
    bool _fullScreen;

    void ApplyLayout(AppSettings settings)
    {
        Width = settings.Width;
        Height = settings.Height;
        // Back where it was, unless that spot is no longer on any screen (e.g. a monitor was unplugged).
        if (settings.Left is double left && settings.Top is double top &&
            new Rect(left, top, Width, Height).IntersectsWith(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (settings.Maximized)
            WindowState = WindowState.Maximized;
        TreeColumn.Width = new GridLength(Math.Max(120, settings.TreeWidth));
        _notePaneWidth = Math.Max(200, settings.NotePaneWidth);
        _dualRatio = Math.Clamp(settings.DualPaneRatio, 0.15, 0.85);
        ApplyColumnSettings(settings.Columns);
        UpdateSortHeaders();
        ApplyInfoPaneLayout(_vm.IsInfoPaneVisible);
    }

    /// <summary>What the settings keep about the window itself; null while it has no usable bounds.</summary>
    WindowLayout? CurrentLayout()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty)
            return null;
        return new WindowLayout(
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            Maximized: WindowState == WindowState.Maximized,
            TreeWidth: TreeColumn.ActualWidth,
            NotePaneWidth: NotePane.Visibility == Visibility.Visible ? NoteColumn.ActualWidth : _notePaneWidth,
            DualPaneRatio: _vm.IsDualVisible ? DualRatioNow() : _dualRatio,
            Columns: CurrentColumnSettings());
    }

    void ApplyInfoPaneLayout(bool visible)
    {
        if (!visible && NotePane.Visibility == Visibility.Visible)
        {
            ClearPreview();
            _notePaneWidth = NoteColumn.ActualWidth;
        }
        NoteColumn.Width = visible ? new GridLength(_notePaneWidth) : new GridLength(0);
        NoteColumn.MinWidth = visible ? 200 : 0;
        NoteSplitterColumn.Width = new GridLength(visible ? 5 : 0);
        NotePane.Visibility = NoteSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    void ApplyDualLayout(bool show)
    {
        if (!show)
            _dualRatio = DualRatioNow();   // before the columns collapse
        DualSplitterColumn.Width = new GridLength(show ? 5 : 0);
        // Star widths split the space between the two lists in the saved proportion.
        ListColumn.Width = new GridLength(show ? 1 - _dualRatio : 1, GridUnitType.Star);
        DualColumn.Width = show ? new GridLength(_dualRatio, GridUnitType.Star) : new GridLength(0);
        DualColumn.MinWidth = show ? 200 : 0;
        ApplyListOffsets();
        if (show)
            SyncDualColumns();
    }

    double DualRatioNow()
    {
        double main = ListColumn.ActualWidth, second = DualColumn.ActualWidth;
        return main + second > 0 && second > 0 ? Math.Clamp(second / (main + second), 0.15, 0.85) : _dualRatio;
    }

    /// <summary>Room above the lists for the title strips (two lists), the compare bar and the search panel.</summary>
    void ApplyListOffsets()
    {
        double compare = _vm.IsCompareOn ? CompareBar.Height : 0;
        double strip = _vm.IsDualVisible ? FolderPane.BarHeight : 0;
        double search = SearchPanel.Visibility == Visibility.Visible ? SearchPanel.ActualHeight : 0;
        MainPaneHeader.Margin = new Thickness(0, compare, 0, 0);
        DualPane.Margin = new Thickness(0, compare, 0, 0);
        SearchPanel.Margin = new Thickness(0, compare + strip, 0, 0);
        FileList.Margin = MarqueeCanvas.Margin = new Thickness(0, compare + strip + search, 0, 0);
        EmptyText.Margin = new Thickness(0, 60 + compare + strip + search, 0, 0);
    }

    void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    /// <summary>F11: full screen without title bar, and back.</summary>
    void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;   // re-maximizing after the style change covers the taskbar
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
        FullScreenGlyph.Text = _fullScreen ? "" : "";   // shrink while full screen, expand otherwise
        FullScreenButton.ToolTip = _fullScreen ? "전체 화면 끝내기 (F11)" : "전체 화면 (F11)";
    }
}
