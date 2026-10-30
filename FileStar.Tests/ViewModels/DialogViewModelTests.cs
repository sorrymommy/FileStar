using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

public sealed class FavoritesManagerViewModelTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly JsonFavoritesStore _store;
    readonly FakeDialogs _dialogs = new();

    public FavoritesManagerViewModelTests()
    {
        System.IO.File.WriteAllText(_dir.Combine("favorites.json"), "[]");
        _store = JsonFavoritesStore.Load(_dir.Combine("favorites.json"));
        _store.Add(_dir.Folder("one"));
        _store.Add(_dir.Folder("two"));
    }

    public void Dispose() => _dir.Dispose();

    FavoritesManagerViewModel Create(string? current = null) => new(_store, _dialogs, current ?? _dir.Folder("current"));

    [Fact]
    public void Edits_stay_in_the_working_copy_until_accepted()
    {
        var vm = Create();
        vm.Selected!.Name = "바뀐 이름";
        Assert.NotEqual("바뀐 이름", _store.Items[0].Name);
        vm.Accept();
        Assert.Equal("바뀐 이름", _store.Items[0].Name);
    }

    [Fact]
    public void New_items_go_into_the_selected_group()
    {
        var vm = Create();
        _dialogs.Inputs.Enqueue("업무");
        vm.NewGroupCommand.Execute(null);
        var group = vm.Selected!;
        Assert.True(group.IsGroup);

        vm.AddCurrentCommand.Execute(null);

        Assert.Single(group.Children);
        Assert.False(vm.AddCurrentCommand.CanExecute(null));
    }

    [Fact]
    public void Items_move_within_their_list_and_out_of_groups()
    {
        var vm = Create();
        var second = vm.Items[1];
        vm.Selected = second;
        vm.Move(-1);
        Assert.Same(second, vm.Items[0]);

        var group = Favorite.Group("g");
        vm.Items.Add(group);
        vm.Drop(second, group);
        Assert.Same(second, group.Children.Single());
        vm.Outdent(second);
        Assert.Same(second, vm.Items[vm.Items.IndexOf(group) + 1]);
    }

    [Fact]
    public void A_group_cannot_be_dropped_into_itself()
    {
        var outer = Favorite.Group("outer");
        var inner = Favorite.Group("inner");
        outer.Children.Add(inner);
        Assert.False(FavoritesManagerViewModel.CanDrop(outer, inner));
        Assert.False(FavoritesManagerViewModel.CanDrop(outer, outer));
        Assert.True(FavoritesManagerViewModel.CanDrop(inner, null));
    }

    [Fact]
    public void Removing_a_full_group_asks_first()
    {
        var vm = Create();
        var group = Favorite.Group("g");
        group.Children.Add(Favorite.Folder(@"C:\x"));
        vm.Items.Add(group);
        _dialogs.Answers.Enqueue(false);
        vm.Remove(group);
        Assert.Contains(group, vm.Items);
        vm.Remove(group);
        Assert.DoesNotContain(group, vm.Items);
    }

    [Fact]
    public void Accepting_drops_folders_left_without_a_path()
    {
        var vm = Create();
        vm.Items[0].FullPath = "  ";
        vm.Accept();
        Assert.Single(_store.Items);
    }
}

public sealed class AiSettingsViewModelTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly JsonAiSettingsStore _store;
    readonly FakeDialogs _dialogs = new();

    sealed class NoCatalog : IAiModelCatalog
    {
        public Task<List<string>> ListOpenAiModelsAsync(string key, CancellationToken token = default) => Task.FromResult(new List<string> { "gpt-x", "gpt-y" });
        public Task<List<string>> ListGeminiModelsAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("Gemini API 키가 설정되지 않았습니다. (파일은 전송되지 않았습니다)");
        public Task<List<string>> ListServerModelsAsync(AiServer server, CancellationToken token = default) => Task.FromResult(new List<string> { "llama" });
    }

    public AiSettingsViewModelTests() => _store = new JsonAiSettingsStore(_dir.Combine("ai.json"));

    public void Dispose() => _dir.Dispose();

    AiSettingsViewModel Create(AiSettings settings) => new(settings, _store, new NoCatalog(), _dialogs);

    [Fact]
    public void Choosing_a_default_jumps_to_its_tab()
    {
        var vm = Create(new AiSettings());
        Assert.Equal(AiSettingsViewModel.ClaudeTab, vm.TabIndex);
        vm.SelectedDefault = vm.DefaultChoices.First(c => c.Provider == AiProvider.Gemini);
        Assert.Equal(AiSettingsViewModel.GeminiTab, vm.TabIndex);
        vm.SelectedDefault = vm.DefaultChoices.First(c => c.Provider == AiProvider.Custom);
        Assert.Equal(AiSettingsViewModel.ServersTab, vm.TabIndex);
        Assert.Same(vm.Servers[0], vm.SelectedServer);
    }

    [Fact]
    public void Renaming_a_server_updates_the_default_list_without_losing_the_choice()
    {
        var vm = Create(new AiSettings());
        vm.SelectedDefault = vm.DefaultChoices.Last();
        vm.Servers[0].Name = "사무실 서버";
        Assert.Equal("사무실 서버", vm.SelectedDefault?.Server?.Name);
        Assert.Contains("사무실 서버", vm.DefaultChoices.Last().Label);
    }

    [Fact]
    public void Switching_server_kind_swaps_the_usual_address()
    {
        var vm = Create(new AiSettings());
        var server = vm.Servers[0];
        server.KindIndex = 1;
        Assert.Equal(AiServer.OpenAiCompatibleDefaultUrl, server.Url);
        server.Url = "http://elsewhere:9000";
        server.KindIndex = 0;
        Assert.Equal("http://elsewhere:9000", server.Url);
        Assert.Contains("이 PC가 아닌 주소", server.Status);
    }

    [Fact]
    public void Removing_the_default_server_falls_back_to_Claude()
    {
        var vm = Create(new AiSettings());
        vm.SelectedDefault = vm.DefaultChoices.Last();
        vm.SelectedServer = vm.Servers[0];
        vm.RemoveServerCommand.Execute(null);
        Assert.Empty(vm.Servers);
        Assert.Equal(AiProvider.Claude, vm.SelectedDefault?.Provider);
    }

    [Fact]
    public async Task Model_lists_keep_the_current_model_when_offered_and_explain_failures()
    {
        var vm = Create(new AiSettings { OpenAiModel = "gpt-y" });
        await ((FileStar.Mvvm.AsyncRelayCommand)vm.LoadOpenAiModelsCommand).ExecuteAsync();
        Assert.Equal(new[] { "gpt-x", "gpt-y" }, vm.OpenAiModels);
        Assert.Equal("gpt-y", vm.OpenAiModel);
        Assert.StartsWith("연결됨", vm.OpenAiStatus);

        await ((FileStar.Mvvm.AsyncRelayCommand)vm.LoadGeminiModelsCommand).ExecuteAsync();
        Assert.Equal("불러오기 실패: Gemini API 키가 설정되지 않았습니다.", vm.GeminiStatus);
    }

    [Fact]
    public void Saving_writes_the_settings_with_cleaned_up_values()
    {
        var settings = new AiSettings();
        var vm = Create(settings);
        vm.ClaudeModel = "  ";
        vm.OpenAiKey = "sk-test";
        vm.AddServerCommand.Execute(null);
        vm.Servers[^1].Name = "  ";
        vm.SelectedDefault = vm.DefaultChoices.Last();

        Assert.True(vm.Save());

        var loaded = _store.Load();
        Assert.Equal(AiSettings.ClaudeModels[0], loaded.ClaudeModel);
        Assert.Equal("sk-test", loaded.OpenAiKey);
        Assert.Equal("새 서버", loaded.Servers[^1].Name);
        Assert.Equal(AiProvider.Custom, loaded.Provider);
        Assert.Equal(loaded.Servers[^1].Id, loaded.ServerId);
    }
}

public sealed class AiSummaryViewModelTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly SqliteNotesStore _notes;
    readonly JsonAiSettingsStore _store;
    readonly FakeDialogs _dialogs = new();
    int _saves;

    sealed class FakeReader : IDocumentReader
    {
        public bool IsReadable(string path) => true;
        public DocumentContent Read(string path, int maxChars) =>
            path.EndsWith("bad.txt") ? throw new InvalidOperationException("읽을 수 없음") : new DocumentContent { FileName = System.IO.Path.GetFileName(path), Note = "앞부분만" };
    }

    sealed class FakeSummarizer : IAiSummarizer
    {
        public AiChoice? LastChoice;
        public int MaxChars(AiChoice choice) => 100;
        public Task<AiResult> SummarizeAsync(AiSettings settings, AiChoice choice, DocumentContent doc, IReadOnlyCollection<string> knownTags, CancellationToken token)
        {
            LastChoice = choice;
            return Task.FromResult(new AiResult($"{doc.FileName} 요약", new() { "AI", "문서" }));
        }
    }

    public AiSummaryViewModelTests()
    {
        _notes = SqliteNotesStore.Open(_dir.Combine(@"data\notes.db"));
        _store = new JsonAiSettingsStore(_dir.Combine("ai.json"));
    }

    public void Dispose()
    {
        _notes.Dispose();
        _dir.Dispose();
    }

    AiSummaryViewModel Create(FakeSummarizer summarizer, params string[] files) =>
        new(files, _store, summarizer, new FakeReader(), _notes, new FakeWindows(), _dialogs, () => _saves++);

    [Fact]
    public async Task A_run_fills_in_each_files_proposal_and_counts_failures()
    {
        string good = _dir.File("good.txt"), bad = _dir.File("bad.txt");
        var vm = Create(new FakeSummarizer(), good, bad);

        await ((FileStar.Mvvm.AsyncRelayCommand)vm.StartCommand).ExecuteAsync();

        Assert.Equal("good.txt 요약", vm.Jobs[0].Summary);
        Assert.Equal("AI, 문서", vm.Jobs[0].TagsText);
        Assert.True(vm.Jobs[0].Apply);
        Assert.Equal("앞부분만", vm.Jobs[0].Note);
        Assert.True(vm.Jobs[1].IsError);
        Assert.StartsWith("완료 1개, 실패 1개", vm.SummaryStatus);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Saving_appends_to_a_hand_written_memo_and_merges_tags()
    {
        string file = _dir.File("doc.txt");
        _notes.Set(file, "직접 쓴 메모", new[] { "문서", "내 태그" });
        var vm = Create(new FakeSummarizer(), file);
        await ((FileStar.Mvvm.AsyncRelayCommand)vm.StartCommand).ExecuteAsync();

        vm.ApplyCommand.Execute(null);

        var note = _notes.Get(file)!;
        Assert.StartsWith("직접 쓴 메모\n\n[AI 요약 ", note.Text);
        Assert.EndsWith("doc.txt 요약", note.Text);
        Assert.Equal(new[] { "문서", "내 태그", "AI" }, note.TagList);
        Assert.Equal(1, _saves);
        Assert.Equal("저장됨", vm.Jobs[0].Status);
        Assert.True(vm.ConfirmClose());
    }

    [Fact]
    public async Task Closing_with_unsaved_results_asks()
    {
        var vm = Create(new FakeSummarizer(), _dir.File("a.txt"));
        await ((FileStar.Mvvm.AsyncRelayCommand)vm.StartCommand).ExecuteAsync();
        _dialogs.Answers.Enqueue(false);
        Assert.False(vm.ConfirmClose());
    }

    [Fact]
    public async Task The_AI_used_becomes_the_default_and_says_where_documents_go()
    {
        var summarizer = new FakeSummarizer();
        var vm = Create(summarizer, _dir.File("a.txt"));
        Assert.Contains("Anthropic(Claude) 서버", vm.PrivacyText);
        vm.SelectedChoice = vm.Choices.First(c => c.Provider == AiProvider.Custom);
        Assert.True(vm.IsPrivate);

        await ((FileStar.Mvvm.AsyncRelayCommand)vm.StartCommand).ExecuteAsync();

        Assert.Equal(AiProvider.Custom, summarizer.LastChoice?.Provider);
        Assert.Equal(AiProvider.Custom, _store.Load().Provider);
        Assert.Equal("다시 실행", vm.StartText);
    }
}
