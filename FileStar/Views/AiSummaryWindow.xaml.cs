using System.ComponentModel;
using System.Windows;

namespace FileStar.Views;

/// <summary>"AI로 메모·태그 만들기", the view of <see cref="AiSummaryViewModel"/>. Modeless, so browsing can continue during a long run.</summary>
public partial class AiSummaryWindow : Window
{
    readonly AiSummaryViewModel _vm;

    public AiSummaryWindow(AiSummaryViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Window_Closing(object? sender, CancelEventArgs e) => e.Cancel = !_vm.ConfirmClose();

    protected override void OnClosed(EventArgs e)
    {
        _vm.Closed();   // stop a run in progress instead of leaving it working in the background
        base.OnClosed(e);
    }
}
