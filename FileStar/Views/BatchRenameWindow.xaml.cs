using System.Windows;

namespace FileStar.Views;

/// <summary>"이름 한꺼번에 바꾸기", the view of <see cref="BatchRenameViewModel"/>.</summary>
public partial class BatchRenameWindow : Window
{
    readonly BatchRenameViewModel _vm;

    public BatchRenameWindow(BatchRenameViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => FindBox.Focus();
    }

    void ReplaceBox_GotFocus(object sender, RoutedEventArgs e) => _vm.ReplaceMode = true;

    void PatternBox_GotFocus(object sender, RoutedEventArgs e) => _vm.PatternMode = true;

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
