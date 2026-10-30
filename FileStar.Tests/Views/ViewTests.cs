using System.Windows;
using System.Windows.Controls;
using FileStar.Tests.Support;
using FileStar.Views;

namespace FileStar.Tests.Views;

/// <summary>
/// Every window, shown for real with its ViewModel on the WPF test thread: the XAML must load and every binding
/// must resolve (WPF only reports a misspelled binding path at run time, as a trace warning).
/// </summary>
public sealed class ViewTests : IDisposable
{
    readonly TestServices _t = new();

    public ViewTests()
    {
        _t.Files.Folder("Alpha");
        _t.Files.File("readme.txt", "hello\nworld");
        _t.Files.File("notes.md", "# title");
    }

    public void Dispose() => _t.Dispose();

    static async Task AssertNoBindingErrors(Window window, Func<Task>? exercise = null)
    {
        WpfThread.TakeBindingErrors();
        List<string> errors;
        window.Show();
        try
        {
            await WpfThread.Settle(200);
            if (exercise != null)
                await exercise();
            await WpfThread.Settle(100);
            errors = WpfThread.TakeBindingErrors();
        }
        finally
        {
            window.Close();   // a failed assertion must not leave the window (and its folder watcher) open
        }
        Assert.True(errors.Count == 0, $"{window.GetType().Name}: {string.Join("\n", errors)}");
    }

    [Fact]
    public void The_main_window_binds_cleanly_in_every_state() => WpfThread.Run(async () =>
    {
        var vm = new MainViewModel(_t.Services, new[] { _t.Files.Path });
        var window = new MainWindow(vm);
        await AssertNoBindingErrors(window, async () =>
        {
            await Wait.Until(() => vm.FileList.View != null && vm.FileList.LoadingMessage == null, "the list");
            var list = (ListView)window.FindName("FileList");
            Assert.Equal(3, list.Items.Count);

            list.SelectedItem = vm.FileList.AllItems.Single(i => i.Name == "readme.txt");
            await WpfThread.Settle(300);
            Assert.Equal("readme.txt", ((TextBlock)window.FindName("NoteTitle")).Text);
            Assert.EndsWith(@"\readme.txt - FileStar", window.Title);

            foreach (var tab in new[] { MainViewModel.PropertiesTab, MainViewModel.PreviewTab, MainViewModel.MemoTab })
            {
                vm.InfoTabIndex = tab;
                await WpfThread.Settle(400);
            }
            Assert.Contains(vm.Properties.Entries.OfType<PropertyRow>(), r => r.Label == "유형");

            vm.IsDualVisible = true;
            await Wait.Until(() => vm.Dual.List.View != null && vm.Dual.List.LoadingMessage == null, "the second list");
            vm.IsCompareOn = true;
            vm.Compare.DiffOnly = true;
            await WpfThread.Settle(200);
            vm.IsCompareOn = false;
            vm.IsDualVisible = false;

            vm.OpenSearch();
            vm.Search.Query = "readme";
            vm.Search.Run();
            await Wait.Until(() => vm.FileList.LoadingMessage == null && vm.FileList.VisibleItems.Any(), "search results");

            vm.IsDarkMode = true;
            vm.ShowNoteMarks = false;
            vm.IsInfoPaneVisible = false;
            await WpfThread.Settle(200);
            vm.IsInfoPaneVisible = true;
        });
        Assert.NotEmpty(_t.Settings.Load().Tabs);
    });

    /// <summary>The row's "#" cell, wherever the user moved that column.</summary>
    static TextBlock? NumberCell(DependencyObject row)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(row); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(row, i);
            if (child is TextBlock text &&
                System.Windows.Data.BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == nameof(FileItem.RowNumber))
                return text;
            if (NumberCell(child) is { } nested)
                return nested;
        }
        return null;
    }

    [Fact]
    public void Row_numbers_stay_right_while_scrolling_both_ways() => WpfThread.Run(async () =>
    {
        // Numbering by ItemsControl.AlternationIndex showed -2147483xxx here once rows were re-created scrolling up.
        for (int i = 1; i <= 400; i++)
            _t.Files.File($@"many\file{i:D3}.txt");
        var vm = new MainViewModel(_t.Services, new[] { _t.Files.Combine("many") });
        var window = new MainWindow(vm);
        await AssertNoBindingErrors(window, async () =>
        {
            await Wait.Until(() => vm.FileList.View != null && vm.FileList.LoadingMessage == null, "the list");
            var list = (ListView)window.FindName("FileList");
            var scroller = list.FindVisualChild<ScrollViewer>()!;
            foreach (var scroll in new Action[] { scroller.ScrollToEnd, scroller.ScrollToHome, () => scroller.ScrollToVerticalOffset(150), scroller.ScrollToEnd, () => scroller.ScrollToVerticalOffset(40) })
            {
                scroll();
                await WpfThread.Settle(150);
                var shown = Enumerable.Range(0, list.Items.Count)
                    .Select(i => (Index: i, Row: list.ItemContainerGenerator.ContainerFromIndex(i) as ListViewItem))
                    .Where(r => r.Row != null)
                    .ToList();
                Assert.NotEmpty(shown);
                foreach (var (index, row) in shown)
                    Assert.Equal((index + 1).ToString(), (NumberCell(row!) ?? throw new InvalidOperationException("no # cell")).Text);
            }
        });
    });

    [Fact]
    public void The_favorites_manager_binds_cleanly() => WpfThread.Run(() =>
        AssertNoBindingErrors(new FavoritesWindow(new FavoritesManagerViewModel(_t.Favorites, _t.Dialogs, _t.Files.Path))));

    [Fact]
    public void The_AI_settings_bind_cleanly() => WpfThread.Run(() =>
    {
        var vm = new AiSettingsViewModel(new AiSettings(), _t.Services.AiSettings, new AiModelCatalog(), _t.Dialogs);
        return AssertNoBindingErrors(new AiSettingsWindow(vm), async () =>
        {
            foreach (int tab in new[] { AiSettingsViewModel.OpenAiTab, AiSettingsViewModel.GeminiTab, AiSettingsViewModel.ServersTab })
            {
                vm.TabIndex = tab;
                await WpfThread.Settle(100);
            }
            vm.AddServerCommand.Execute(null);
        });
    });

    [Fact]
    public void The_AI_summary_window_binds_cleanly() => WpfThread.Run(() =>
    {
        var vm = new AiSummaryViewModel(new[] { _t.Files.Combine("readme.txt") }, _t.Services.AiSettings, AiSummarizer.CreateDefault(),
            new FileDocumentReader(), _t.Notes, _t.Windows, _t.Dialogs, () => { });
        return AssertNoBindingErrors(new AiSummaryWindow(vm));
    });

    [Fact]
    public void The_AI_folder_choice_binds_cleanly() => WpfThread.Run(() =>
    {
        var scan = new AiFolderScan(1, new() { _t.Files.Combine("readme.txt") }, new() { _t.Files.Combine("readme.txt"), _t.Files.Combine("notes.md") }, false);
        var vm = new AiFolderChoiceViewModel(scan, _t.Notes);
        Assert.Single(vm.Chosen);
        return AssertNoBindingErrors(new AiFolderChoiceWindow(vm), () =>
        {
            vm.WithSubfolders = true;
            Assert.Equal(2, vm.Chosen.Count);
            return Task.CompletedTask;
        });
    });

    [Fact]
    public void The_batch_rename_window_binds_cleanly() => WpfThread.Run(() =>
    {
        var items = new[] { "readme.txt", "notes.md" }.Select(n => FileItem.FromPath(_t.Files.Combine(n))!).ToList();
        var vm = new BatchRenameViewModel(items);
        return AssertNoBindingErrors(new BatchRenameWindow(vm), () =>
        {
            vm.PatternMode = true;
            vm.Pattern = "{n}";
            return Task.CompletedTask;
        });
    });

    [Fact]
    public void The_notes_repair_window_binds_cleanly() => WpfThread.Run(() =>
    {
        _t.Notes.Set(_t.Files.Combine("gone.txt"), "지워진 파일의 메모", Array.Empty<string>());
        var vm = new NotesRepairViewModel(_t.Notes, () => { });
        return AssertNoBindingErrors(new NotesRepairWindow(vm), async () =>
        {
            await Wait.Until(() => vm.Entries.Count > 0, "the scan");
            Assert.Equal("지워진 파일의 메모", vm.Entries.OfType<NotesRepairRow>().Single().Lost.Note.Text);
        });
    });
}
