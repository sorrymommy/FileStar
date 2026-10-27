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
        window.Show();
        await WpfThread.Settle(200);
        if (exercise != null)
            await exercise();
        await WpfThread.Settle(100);
        var errors = WpfThread.TakeBindingErrors();
        window.Close();
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
