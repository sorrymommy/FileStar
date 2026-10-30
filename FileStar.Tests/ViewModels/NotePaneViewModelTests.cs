using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

public sealed class NotePaneViewModelTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly SqliteNotesStore _notes;
    readonly FakeDialogs _dialogs = new();
    readonly NotePaneViewModel _vm;
    readonly List<(string Path, FileNote? Note)> _saved = new();
    readonly string _file;

    public NotePaneViewModelTests()
    {
        _notes = SqliteNotesStore.Open(_dir.Combine(@"data\notes.db"));
        _vm = new NotePaneViewModel(_notes, _dialogs);
        _vm.Saved += (path, note) => _saved.Add((path, note));
        _file = _dir.File("a.txt");
    }

    public void Dispose()
    {
        _notes.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void The_memo_is_read_only_until_edit()
    {
        _notes.Set(_file, "기존 메모", new[] { "태그" });
        _vm.ShowFor(_file);
        Assert.Equal("기존 메모", _vm.Text);
        Assert.Equal(new[] { "태그" }, _vm.Tags);
        Assert.False(_vm.IsEditing);
        Assert.StartsWith("저장됨", _vm.StatusText);
    }

    [Fact]
    public void Saving_writes_the_memo_and_tells_the_list()
    {
        _vm.ShowFor(_file);
        _vm.BeginEdit();
        _vm.Text = "새 메모";
        Assert.True(_vm.IsDirty);
        Assert.StartsWith("수정 중 (저장 안 됨)", _vm.StatusText);

        _vm.SaveEdit();

        Assert.False(_vm.IsEditing);
        Assert.Equal("새 메모", _notes.Get(_file)?.Text);
        Assert.Equal(_file, _saved.Single().Path);
    }

    [Fact]
    public void Cancel_throws_the_edit_away()
    {
        _notes.Set(_file, "원래", Array.Empty<string>());
        _vm.ShowFor(_file);
        _vm.BeginEdit();
        _vm.Text = "바꿈";
        _vm.CancelEdit();
        Assert.Equal("원래", _vm.Text);
        Assert.Equal("원래", _notes.Get(_file)?.Text);
    }

    [Fact]
    public void Moving_on_with_an_unsaved_edit_asks_first()
    {
        string other = _dir.File("b.txt");
        _vm.ShowFor(_file);
        _vm.BeginEdit();
        _vm.Text = "저장할까?";
        _dialogs.Answers.Enqueue(true);

        _vm.ShowFor(other);

        Assert.Contains(_dialogs.Messages, m => m.StartsWith("confirm: 수정한 메모를 저장할까요?"));
        Assert.Equal("저장할까?", _notes.Get(_file)?.Text);
        Assert.Equal(other, _vm.Path);
    }

    [Fact]
    public void Tags_are_saved_right_away_without_an_unsaved_memo_edit()
    {
        _notes.Set(_file, "저장된 메모", Array.Empty<string>());
        _vm.ShowFor(_file);
        _vm.BeginEdit();
        _vm.Text = "아직 저장 안 함";

        _vm.AddTags("#예산, 회의");

        var note = _notes.Get(_file)!;
        Assert.Equal("저장된 메모", note.Text);
        Assert.Equal(new[] { "예산", "회의" }, note.TagList);
        Assert.True(_vm.IsEditing);
    }

    [Fact]
    public void Removing_the_last_tag_of_a_tag_only_item_deletes_its_note()
    {
        _notes.Set(_file, "", new[] { "하나" });
        _vm.ShowFor(_file);
        _vm.RemoveTagCommand.Execute("하나");
        Assert.Null(_notes.Get(_file));
        Assert.Empty(_vm.Tags);
    }

    [Fact]
    public void Delete_asks_and_clears_memo_and_tags()
    {
        _notes.Set(_file, "메모", new[] { "t" });
        _vm.ShowFor(_file);
        _dialogs.Answers.Enqueue(true);
        _vm.DeleteCommand.Execute(null);
        Assert.Null(_notes.Get(_file));
        Assert.Equal("", _vm.Text);
        Assert.Empty(_vm.Tags);
    }

    [Fact]
    public void A_file_saved_after_its_memo_shows_the_stale_banner_until_confirmed()
    {
        _notes.Set(_file, "메모", Array.Empty<string>());
        File.SetLastWriteTime(_file, DateTime.Now.AddMinutes(10));
        _vm.ShowFor(_file);
        Assert.True(_vm.IsStale);
        Assert.Contains("파일이 수정되었습니다", _vm.StaleText);

        _vm.ConfirmCommand.Execute(null);

        Assert.True(_notes.Get(_file)!.Updated >= DateTime.Now.AddMinutes(-1));
        Assert.Single(_saved);
    }

    [Fact]
    public void Nothing_can_be_written_for_a_multiple_selection()
    {
        _vm.ShowFor(null);
        Assert.False(_vm.CanEdit);
        _vm.BeginEdit();
        Assert.False(_vm.IsEditing);
        _vm.AddTags("x");
        Assert.Empty(_vm.Tags);
    }

    [Fact]
    public void Tag_suggestions_leave_out_the_tags_already_there()
    {
        _notes.Set(_dir.File("other.txt"), "", new[] { "가", "나" });
        _notes.Set(_file, "", new[] { "가" });
        _vm.ShowFor(_file);
        Assert.Equal(new[] { "나" }, _vm.TagSuggestions());
    }
}
