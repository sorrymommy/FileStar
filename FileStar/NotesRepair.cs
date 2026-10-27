using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FileStar;

/// <summary>A memo whose file is no longer at its path: found elsewhere by its file ID, or not found at all.</summary>
public sealed record LostNote(string OldPath, FileNote Note, string? NewPath)
{
    /// <summary>Same file name at the new place: a plain move, safe to relink without asking.</summary>
    public bool SameName => NewPath != null &&
        string.Equals(Path.GetFileName(OldPath), Path.GetFileName(NewPath), StringComparison.OrdinalIgnoreCase);
}

public sealed record NoteScanResult(List<LostNote> Lost, Dictionary<string, string> NewIds);

public static class NotesRepair
{
    /// <summary>
    /// Checks every memo (run off the UI thread): fills in missing file IDs, and for memos whose file is gone,
    /// looks the file up by ID. Memos on drives that aren't connected right now (USB, network) are left alone,
    /// so unplugging a drive never makes its memos look deleted.
    /// </summary>
    public static NoteScanResult Scan(List<(string Path, FileNote Note)> notes)
    {
        var lost = new List<LostNote>();
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var available = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var notedPaths = new HashSet<string>(notes.Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var (path, note) in notes)
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\"))
                continue;   // network paths: too slow to probe and IDs don't apply
            if (!available.TryGetValue(root, out bool ready))
            {
                try { ready = new DriveInfo(root).IsReady; }
                catch { ready = false; }
                available[root] = ready;
            }
            if (!ready)
                continue;

            if (File.Exists(path) || Directory.Exists(path))
            {
                if (note.FileId == null && FileIdentity.Get(path) is string id)
                    ids[path] = id;
                continue;
            }
            string? now = note.FileId == null ? null : FileIdentity.Resolve(note.FileId);
            // The ID now names a different file that already has its own memo: not ours.
            if (now != null && notedPaths.Contains(now))
                now = null;
            lost.Add(new LostNote(path, note, now));
        }
        // Parents first, so relinking a folder carries its children's memos along before they are looked at.
        lost.Sort((a, b) => a.OldPath.Length.CompareTo(b.OldPath.Length));
        return new NoteScanResult(lost, ids);
    }

    /// <summary>Relinks the plain moves (same name, new place); returns how many memos were reattached.</summary>
    public static int AutoRelink(NoteScanResult result)
    {
        NotesStore.ApplyIds(result.NewIds);
        int count = 0;
        foreach (var lost in result.Lost.Where(l => l.SameName))
        {
            if (NotesStore.Relink(lost.OldPath, lost.NewPath!))
                count++;
        }
        return count;
    }
}

/// <summary>도구 > 메모 정리: reattach memos to files moved outside FileStar, and clear memos of deleted files.</summary>
public sealed class NotesRepairWindow : Window
{
    readonly StackPanel _list = new();
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    readonly Button _apply;
    readonly List<(CheckBox Box, LostNote Lost)> _rows = new();
    readonly Action _onChanged;

    public NotesRepairWindow(Action onChanged)
    {
        _onChanged = onChanged;
        Title = "메모 정리";
        Width = 720;
        Height = 520;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 12;
        SetResourceReference(BackgroundProperty, "Th.Window.Bg");
        SetResourceReference(ForegroundProperty, "Th.Text");

        var root = new DockPanel { Margin = new Thickness(16) };
        var intro = new TextBlock
        {
            Text = "메모는 파일 경로와 함께 저장됩니다. Windows 탐색기 등 다른 프로그램에서 파일을 옮기면 메모가 떨어지는데, " +
                   "같은 드라이브 안에서 옮긴 경우에는 파일 고유 번호로 새 위치를 찾아 다시 연결할 수 있습니다. " +
                   "(이름이 같은 채 옮겨진 파일은 FileStar가 알아서 다시 연결합니다.)",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Th.Text.Muted");
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);

        _apply = new Button { Content = "선택한 항목 적용", Padding = new Thickness(12, 4, 12, 4), IsEnabled = false };
        _apply.Click += (_, _) => Apply();
        var close = new Button { Content = "닫기", Width = 84, Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_apply);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SetResourceReference(BorderBrushProperty, "Th.Border");
        root.Children.Add(intro);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = scroll }
            .WithResource(Border.BorderBrushProperty, "Th.Border"));
        Content = root;
        Loaded += async (_, _) => await ScanAsync();
    }

    async Task ScanAsync()
    {
        _status.Text = "메모를 확인하는 중...";
        _list.Children.Clear();
        _rows.Clear();
        var snapshot = NotesStore.Snapshot();
        var result = await Task.Run(() => NotesRepair.Scan(snapshot));
        NotesStore.ApplyIds(result.NewIds);

        var moved = result.Lost.Where(l => l.NewPath != null).ToList();
        var missing = result.Lost.Where(l => l.NewPath == null).ToList();
        _status.Text = result.Lost.Count == 0
            ? $"메모 {snapshot.Count:N0}개를 확인했습니다. 떨어진 메모가 없습니다."
            : $"메모 {snapshot.Count:N0}개 중 파일을 다시 찾은 것 {moved.Count:N0}개, 찾지 못한 것 {missing.Count:N0}개.";

        if (moved.Count > 0)
        {
            AddHeader("옮겨진 파일 — 체크한 항목은 메모를 새 위치로 다시 연결합니다");
            foreach (var lost in moved)
                AddRow(lost, $"{lost.OldPath}\n→ {lost.NewPath}", isChecked: true);
        }
        if (missing.Count > 0)
        {
            AddHeader("찾을 수 없는 파일 (삭제됐거나 다른 드라이브로 옮겨짐) — 체크한 항목은 메모를 지웁니다");
            foreach (var lost in missing)
                AddRow(lost, lost.OldPath, isChecked: false);
        }
        _apply.IsEnabled = _rows.Count > 0;
        _onChanged();
    }

    void AddHeader(string text)
    {
        _list.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, _list.Children.Count == 0 ? 0 : 12, 0, 4),
        }.WithResource(TextBlock.ForegroundProperty, "Th.Note.Header"));
    }

    void AddRow(LostNote lost, string paths, bool isChecked)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new System.Windows.Documents.Run(paths));
        if (lost.Note.Summary.Length > 0)
            text.Inlines.Add(new System.Windows.Documents.Run("\n메모: " + lost.Note.Summary) { FontStyle = FontStyles.Italic });
        var box = new CheckBox { Content = text, IsChecked = isChecked, Margin = new Thickness(0, 3, 0, 3) };
        _rows.Add((box, lost));
        _list.Children.Add(box);
    }

    async void Apply()
    {
        var chosen = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Lost).ToList();
        int relinked = 0;
        foreach (var lost in chosen.Where(l => l.NewPath != null))
        {
            if (NotesStore.Relink(lost.OldPath, lost.NewPath!))
                relinked++;
        }
        var removed = chosen.Where(l => l.NewPath == null).Select(l => l.OldPath).ToList();
        NotesStore.Remove(removed);
        await ScanAsync();
        _status.Text = $"다시 연결 {relinked:N0}개, 지운 메모 {removed.Count:N0}개. " + _status.Text;
    }
}
