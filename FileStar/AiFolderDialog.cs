using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FileStar;

/// <summary>
/// When folders are picked for "AI로 메모·태그 만들기": shows what was found inside them and lets the user choose
/// whether to include subfolders and whether to skip files that already have a memo.
/// </summary>
public sealed class AiFolderDialog : Window
{
    /// <summary>Upper bound on the scan, so picking a drive root doesn't walk the whole disk.</summary>
    const int MaxFiles = 5000;

    readonly List<string> _direct, _all;
    readonly RadioButton _directOnly, _withSubfolders;
    readonly CheckBox _skipNoted;
    readonly TextBlock _summary;

    AiFolderDialog(int folderCount, List<string> direct, List<string> all, bool truncated)
    {
        _direct = direct;
        _all = all;
        Title = "AI로 메모·태그 만들기 — 폴더";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 12;
        SetResourceReference(BackgroundProperty, "Th.Window.Bg");
        SetResourceReference(ForegroundProperty, "Th.Text");

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"선택한 폴더 {folderCount:N0}개에서 AI가 읽을 수 있는 파일을 찾았습니다." +
                   "\n(문서·한글·PDF·이미지·텍스트 파일만 포함, 프로그램·압축 파일 등은 제외)",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        _directOnly = new RadioButton { Content = $"폴더 바로 안의 파일만 ({direct.Count:N0}개)", Margin = new Thickness(0, 0, 0, 4) };
        _withSubfolders = new RadioButton
        {
            Content = $"하위 폴더까지 모두 ({all.Count:N0}개{(truncated ? "+, 최대 " + MaxFiles.ToString("N0") + "개까지만" : "")})",
            IsEnabled = all.Count > direct.Count,
        };
        (direct.Count > 0 ? _directOnly : _withSubfolders).IsChecked = true;
        _skipNoted = new CheckBox { Content = "이미 메모가 있는 파일은 건너뛰기 (메모 뒤에 수정된 파일은 포함)", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        _summary = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Th.Text.Muted");
        foreach (var control in new Control[] { _directOnly, _withSubfolders, _skipNoted })
            control.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => UpdateSummary()));
        _skipNoted.Unchecked += (_, _) => UpdateSummary();
        panel.Children.Add(_directOnly);
        panel.Children.Add(_withSubfolders);
        panel.Children.Add(_skipNoted);
        panel.Children.Add(_summary);

        var ok = new Button { Content = "목록 만들기", Width = 96, Padding = new Thickness(0, 4, 0, 4), IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "취소", Width = 84, Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
        UpdateSummary();
    }

    List<string> Chosen => (_withSubfolders.IsChecked == true ? _all : _direct)
        .Where(path => _skipNoted.IsChecked != true || NotesStore.Get(path) is not { Text.Length: > 0 } note ||
                       NotesStore.IsStale(note, File.GetLastWriteTime(path), isDirectory: false))
        .ToList();

    void UpdateSummary()
    {
        int count = Chosen.Count;
        _summary.Text = count == 0
            ? "처리할 파일이 없습니다."
            : $"{count:N0}개 파일을 AI 요약 목록에 올립니다. 시작 전에 목록과 사용할 AI를 확인할 수 있습니다." +
              (count > 100 ? "\n파일이 많으면 시간이 오래 걸리고, 클라우드 AI는 요금도 그만큼 나옵니다." : "");
    }

    /// <summary>
    /// Folders and files from the selection, turned into the list of files to summarize; null if cancelled.
    /// Files picked directly are always kept; only the folder contents go through the dialog.
    /// </summary>
    public static async Task<List<string>?> ExpandAsync(Window owner, IReadOnlyList<FileItem> selected)
    {
        var files = selected.Where(i => !i.IsDirectory).Select(i => i.FullPath).ToList();
        var folders = selected.Where(i => i.IsDirectory).Select(i => i.FullPath).ToList();
        if (folders.Count == 0)
            return files;

        // Off the UI thread: a big folder tree takes a while to walk.
        var skip = FileSystem.Options.AttributesToSkip;
        owner.Cursor = System.Windows.Input.Cursors.AppStarting;
        List<string> direct, all;
        try
        {
            (direct, all) = await Task.Run(() => (
                folders.SelectMany(f => SafeFiles(f, skip, recurse: false)).ToList(),
                folders.SelectMany(f => SafeFiles(f, skip, recurse: true)).Take(MaxFiles + 1).ToList()));
        }
        finally
        {
            owner.Cursor = null;
        }
        bool truncated = all.Count > MaxFiles;
        if (truncated)
            all.RemoveAt(all.Count - 1);
        if (all.Count == 0)
        {
            MessageBox.Show(owner, "선택한 폴더에 AI가 읽을 수 있는 파일(문서·한글·PDF·이미지·텍스트)이 없습니다.",
                "AI로 메모·태그 만들기", MessageBoxButton.OK, MessageBoxImage.Information);
            return files.Count > 0 ? files : null;
        }

        var dialog = new AiFolderDialog(folders.Count, direct, all, truncated) { Owner = owner };
        if (dialog.ShowDialog() != true)
            return null;
        return files.Concat(dialog.Chosen).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Readable files in a folder (stops after <see cref="MaxFiles"/>+1, so a drive root doesn't scan forever).</summary>
    static IEnumerable<string> SafeFiles(string folder, FileAttributes skip, bool recurse)
    {
        try
        {
            var scan = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = skip, RecurseSubdirectories = recurse };
            return Directory.EnumerateFiles(folder, "*", scan).Where(DocumentReader.IsReadable).Take(MaxFiles + 1).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
