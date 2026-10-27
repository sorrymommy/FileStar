using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FileStar.Views;

/// <summary>즐겨찾기 관리, the view of <see cref="FavoritesManagerViewModel"/>: the tree's selection, keys and drag and drop.</summary>
public partial class FavoritesWindow : Window
{
    readonly FavoritesManagerViewModel _vm;
    Point _dragStart;
    Favorite? _dragCandidate;

    public FavoritesWindow(FavoritesManagerViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (vm.StartWithName)
                FocusName();
            else
                FavoriteTree.Focus();
        };
    }

    void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    void FavoriteTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.Selected = FavoriteTree.SelectedItem as Favorite;

    void FavoriteTree_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.Delete && _vm.Selected is Favorite item) _vm.Remove(item);
        else if (key == Key.F2) FocusName();
        else if (key == Key.Up && mods == ModifierKeys.Alt) _vm.Move(-1);
        else if (key == Key.Down && mods == ModifierKeys.Alt) _vm.Move(1);
        else if (key == Key.Left && mods == ModifierKeys.Alt && _vm.Selected is Favorite outItem) _vm.Outdent(outItem);
        else
            return;
        e.Handled = true;
    }

    // ── Drag & drop ──

    static Favorite? FavoriteAt(object source) =>
        (source as DependencyObject).FindAncestor<TreeViewItem>()?.DataContext as Favorite;

    void FavoriteTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = FavoriteAt(e.OriginalSource);
    }

    void FavoriteTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null)
            return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var item = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(FavoriteTree, item, DragDropEffects.Move);
    }

    void FavoriteTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(Favorite)) is Favorite source && FavoritesManagerViewModel.CanDrop(source, FavoriteAt(e.OriginalSource))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    void FavoriteTree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(Favorite)) is Favorite source)
            _vm.Drop(source, FavoriteAt(e.OriginalSource));
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        _vm.Accept();
        DialogResult = true;
    }
}
