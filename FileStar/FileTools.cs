using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace FileStar;

/// <summary>Recently visited folders and recently opened files (most recent first), kept with the settings.</summary>
public static class RecentItems
{
    const int Limit = 20;

    public static List<string> Folders { get; private set; } = new();
    public static List<string> Files { get; private set; } = new();

    public static void Load(AppSettings settings)
    {
        Folders = settings.RecentFolders.ToList();
        Files = settings.RecentFiles.ToList();
    }

    public static void AddFolder(string path) => Add(Folders, path);
    public static void AddFile(string path) => Add(Files, path);

    static void Add(List<string> list, string path)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > Limit)
            list.RemoveRange(Limit, list.Count - Limit);
    }

    public static void Clear()
    {
        Folders.Clear();
        Files.Clear();
    }
}

/// <summary>zip / unzip with Windows' own conventions, run off the UI thread by the caller.</summary>
public static class ZipTools
{
    /// <summary>"이름.zip" in <paramref name="folder"/>, or "이름 (2).zip" … if taken.</summary>
    public static string FreeName(string folder, string baseName, string extension)
    {
        string path = Path.Combine(folder, baseName + extension);
        for (int n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{baseName} ({n}){extension}");
        return path;
    }

    /// <summary>Packs files and whole folders (with everything inside) into a new zip; names are stored as UTF-8.</summary>
    public static void Create(string zipPath, IReadOnlyList<string> items, CancellationToken token)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.UTF8);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(item))
                zip.CreateEntryFromFile(item, Path.GetFileName(item), CompressionLevel.Optimal);
            else if (Directory.Exists(item))
            {
                string root = Path.GetDirectoryName(item.TrimEnd('\\'))!;
                var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
                foreach (var file in Directory.EnumerateFiles(item, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
                }
                // Keep empty folders too.
                foreach (var dir in Directory.EnumerateDirectories(item, "*", options).Prepend(item))
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        zip.CreateEntry(Path.GetRelativePath(root, dir).Replace('\\', '/') + "/");
                }
            }
        }
    }

    /// <summary>
    /// Unpacks into a new folder. Entry names are UTF-8 in modern zips (FileStar's own included) but CP949 in
    /// zips made by Korean Windows' "압축(zip) 폴더", so each name is read as UTF-8 if it is valid UTF-8, else as CP949.
    /// </summary>
    public static void Extract(string zipPath, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        ZipFile.ExtractToDirectory(zipPath, targetFolder, new Utf8OrCp949(), overwriteFiles: false);
    }

    /// <summary>Decodes with strict UTF-8 and falls back to CP949 for byte sequences that aren't UTF-8 (reading only).</summary>
    sealed class Utf8OrCp949 : Encoding
    {
        static readonly Encoding Utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        static readonly Encoding Cp949 = GetEncoding(949);

        static Encoding For(byte[] bytes, int index, int count)
        {
            try
            {
                Utf8.GetCharCount(bytes, index, count);
                return Utf8;
            }
            catch (DecoderFallbackException)
            {
                return Cp949;
            }
        }

        public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
            UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => For(bytes, index, count).GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
            For(bytes, byteIndex, byteCount).GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        public override string GetString(byte[] bytes, int index, int count) => For(bytes, index, count).GetString(bytes, index, count);
        public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => Math.Max(UTF8.GetMaxCharCount(byteCount), Cp949.GetMaxCharCount(byteCount));
    }
}

/// <summary>"이름 한꺼번에 바꾸기": find/replace or a numbering pattern, with a live preview of every new name.</summary>
public sealed class BatchRenameWindow : Window
{
    readonly IReadOnlyList<FileItem> _items;
    readonly RadioButton _replaceMode, _patternMode;
    readonly TextBox _find, _replace, _pattern, _start;
    readonly CheckBox _includeExt;
    readonly ListView _preview;
    readonly TextBlock _problem;
    readonly Button _ok;

    /// <summary>(old full path, new name) for every item whose name changes; filled when the user confirms.</summary>
    public List<(string Path, string NewName)> Plan { get; private set; } = new();

    public BatchRenameWindow(IReadOnlyList<FileItem> items)
    {
        _items = items;
        Title = $"이름 한꺼번에 바꾸기 — {items.Count:N0}개";
        Width = 720;
        Height = 560;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 12;
        SetResourceReference(BackgroundProperty, "Th.Window.Bg");
        SetResourceReference(ForegroundProperty, "Th.Text");

        var root = new DockPanel { Margin = new Thickness(16) };

        var options = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++)
            options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _replaceMode = new RadioButton { Content = "찾아 바꾸기", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
        Place(options, _replaceMode, 0, 0, 4);
        Place(options, Label("찾을 글자"), 1, 0);
        _find = Box();
        Place(options, _find, 1, 1);
        Place(options, Label("바꿀 글자"), 1, 2);
        _replace = Box();
        Place(options, _replace, 1, 3);

        _patternMode = new RadioButton { Content = "새 이름 규칙", Margin = new Thickness(0, 10, 0, 6) };
        Place(options, _patternMode, 2, 0, 4);
        Place(options, Label("규칙"), 3, 0);
        _pattern = Box("{name}_{n}");
        _pattern.ToolTip = "{name} 원래 이름 · {n} 번호(001처럼 자릿수는 시작 번호에 맞춤) · {date} 수정한 날짜(20261027)";
        Place(options, _pattern, 3, 1);
        Place(options, Label("시작 번호"), 3, 2);
        _start = Box("001");
        Place(options, _start, 3, 3);
        DockPanel.SetDock(options, Dock.Top);

        var hint = new TextBlock
        {
            Text = "규칙: {name} 원래 이름, {n} 번호 (시작 번호를 001로 쓰면 001, 002 …), {date} 수정한 날짜. 목록 순서대로 번호가 붙습니다.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Th.Text.Faint");
        DockPanel.SetDock(hint, Dock.Top);
        _includeExt = new CheckBox { Content = "확장자도 바꾸기 (보통은 끔)", Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_includeExt, Dock.Top);

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _ok = new Button { Content = "이름 바꾸기", Width = 100, Padding = new Thickness(0, 4, 0, 4), IsDefault = true };
        _ok.Click += (_, _) => { Plan = BuildPlan().Where(p => p.Problem == null && p.Changed).Select(p => (p.Path, p.NewName)).ToList(); DialogResult = true; };
        var cancel = new Button { Content = "취소", Width = 84, Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(_ok, Dock.Right);
        _problem = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "Th.Error");
        buttons.Children.Add(cancel);
        buttons.Children.Add(_ok);
        buttons.Children.Add(_problem);
        DockPanel.SetDock(buttons, Dock.Bottom);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "지금 이름", Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding("Old") });
        grid.Columns.Add(new GridViewColumn { Header = "새 이름", Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding("New") });
        _preview = new ListView { View = grid };

        root.Children.Add(options);
        root.Children.Add(hint);
        root.Children.Add(_includeExt);
        root.Children.Add(buttons);
        root.Children.Add(_preview);
        Content = root;

        foreach (var box in new[] { _find, _replace, _pattern, _start })
            box.TextChanged += (_, _) => Update();
        _find.GotFocus += (_, _) => _replaceMode.IsChecked = true;
        _replace.GotFocus += (_, _) => _replaceMode.IsChecked = true;
        _pattern.GotFocus += (_, _) => _patternMode.IsChecked = true;
        _start.GotFocus += (_, _) => _patternMode.IsChecked = true;
        _replaceMode.Checked += (_, _) => Update();
        _patternMode.Checked += (_, _) => Update();
        _includeExt.Checked += (_, _) => Update();
        _includeExt.Unchecked += (_, _) => Update();
        Loaded += (_, _) => { _find.Focus(); Update(); };
    }

    static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 6) };
    static TextBox Box(string text = "") => new() { Text = text, Padding = new Thickness(2, 3, 2, 3), Margin = new Thickness(0, 0, 0, 6) };

    static void Place(Grid grid, UIElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, span);
        grid.Children.Add(element);
    }

    sealed record Row(string Path, string Old, string NewName, bool Changed, string? Problem)
    {
        public string New => Problem == null ? NewName : $"{NewName}   ⚠ {Problem}";
    }

    List<Row> BuildPlan()
    {
        var rows = new List<Row>();
        string startText = _start.Text.Trim();
        int number = int.TryParse(startText, out int s) ? s : 1;
        int digits = startText.Length > 1 && startText.StartsWith('0') ? startText.Length : 1;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = _items.Select(i => i.FolderPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var item in _items)
        {
            bool keepExt = _includeExt.IsChecked != true && !item.IsDirectory;
            string ext = keepExt ? item.RawExtension : "";
            string stem = keepExt ? Path.GetFileNameWithoutExtension(item.Name) : item.Name;
            string newStem = _replaceMode.IsChecked == true
                ? (_find.Text.Length == 0 ? stem : stem.Replace(_find.Text, _replace.Text, StringComparison.CurrentCultureIgnoreCase))
                : _pattern.Text
                    .Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
                    .Replace("{n}", number.ToString(new string('0', digits)), StringComparison.OrdinalIgnoreCase)
                    .Replace("{date}", item.Modified.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase);
            number++;
            string newName = (newStem + ext).Trim();
            string? problem = null;
            if (newName.Length == 0)
                problem = "빈 이름";
            else if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                problem = "쓸 수 없는 문자";
            else if (!taken.Add(Path.Combine(item.FolderPath, newName)))
                problem = "이름이 겹침";
            else if (!string.Equals(newName, item.Name, StringComparison.OrdinalIgnoreCase) &&
                     (File.Exists(Path.Combine(item.FolderPath, newName)) || Directory.Exists(Path.Combine(item.FolderPath, newName))) &&
                     !_items.Any(other => string.Equals(other.FullPath, Path.Combine(item.FolderPath, newName), StringComparison.OrdinalIgnoreCase)))
                problem = "같은 이름이 이미 있음";
            rows.Add(new Row(item.FullPath, item.Name, newName, newName != item.Name, problem));
        }
        return rows;
    }

    void Update()
    {
        if (_preview == null)
            return;
        var rows = BuildPlan();
        _preview.ItemsSource = rows;
        int changing = rows.Count(r => r.Changed && r.Problem == null);
        int problems = rows.Count(r => r.Problem != null);
        _problem.Text = problems > 0 ? $"문제가 있는 {problems:N0}개는 건너뜁니다." : "";
        _ok.Content = $"{changing:N0}개 바꾸기";
        _ok.IsEnabled = changing > 0;
    }
}
