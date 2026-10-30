using System.Windows;

namespace FileStar.Views;

/// <summary>When folders are picked for AI: which of the files found inside them to put on the list (<see cref="AiFolderChoiceViewModel"/>).</summary>
public partial class AiFolderChoiceWindow : Window
{
    public AiFolderChoiceWindow(AiFolderChoiceViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
