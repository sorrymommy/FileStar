using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace FileStar.Views;

/// <summary>
/// 도구 > AI 설정, the view of <see cref="AiSettingsViewModel"/>. PasswordBoxes can't be bound (by design, so keys
/// don't sit in bindable properties of the control), so their values are passed to the ViewModel here.
/// </summary>
public partial class AiSettingsWindow : Window
{
    readonly AiSettingsViewModel _vm;

    public AiSettingsWindow(AiSettingsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        // Show that keys are stored without revealing them.
        ApiKeyBox.Password = vm.ApiKey;
        OpenAiKeyBox.Password = vm.OpenAiKey;
        GeminiKeyBox.Password = vm.GeminiKey;
        ApiKeyBox.PasswordChanged += (_, _) => vm.ApiKey = ApiKeyBox.Password;
        OpenAiKeyBox.PasswordChanged += (_, _) => vm.OpenAiKey = OpenAiKeyBox.Password;
        GeminiKeyBox.PasswordChanged += (_, _) => vm.GeminiKey = GeminiKeyBox.Password;

        ShowServerKey();
        ServerKeyBox.PasswordChanged += (_, _) =>
        {
            if (!_showingServerKey && vm.SelectedServer is { } server)
                server.ApiKey = ServerKeyBox.Password;
        };
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.ServerAdded += () => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            ServerNameBox.Focus();
            ServerNameBox.SelectAll();
        });
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiSettingsViewModel.SelectedServer))
            ShowServerKey();
    }

    /// <summary>Filling the box for another server, not the user typing a key.</summary>
    bool _showingServerKey;

    void ShowServerKey()
    {
        _showingServerKey = true;
        ServerKeyBox.Password = _vm.SelectedServer?.ApiKey ?? "";
        _showingServerKey = false;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Save())
            DialogResult = true;
    }
}
