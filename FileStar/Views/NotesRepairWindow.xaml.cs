using System.Windows;

namespace FileStar.Views;

/// <summary>도구 > 메모 정리, the view of <see cref="NotesRepairViewModel"/>; it scans as soon as it opens.</summary>
public partial class NotesRepairWindow : Window
{
    public NotesRepairWindow(NotesRepairViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await vm.ScanAsync();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
