using System.Windows;
using System.Windows.Controls;

namespace FileStar;

/// <summary>A minimal one-line text prompt.</summary>
public sealed class InputDialog : Window
{
    readonly TextBox _box;

    InputDialog(string title, string prompt, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 12;
        SetResourceReference(BackgroundProperty, "Th.Window.Bg");
        SetResourceReference(ForegroundProperty, "Th.Text");

        _box = new TextBox { Text = initial, Padding = new Thickness(2, 3, 2, 3), Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "확인", Width = 80, Padding = new Thickness(0, 3, 0, 3), IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "취소", Width = 80, Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    /// <summary>Returns the trimmed text, or null if cancelled or left empty.</summary>
    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var dialog = new InputDialog(title, prompt, initial) { Owner = owner };
        if (dialog.ShowDialog() != true)
            return null;
        string text = dialog._box.Text.Trim();
        return text.Length > 0 ? text : null;
    }
}
