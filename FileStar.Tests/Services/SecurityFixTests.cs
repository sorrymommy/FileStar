using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using FileStar.Tests.Support;

namespace FileStar.Tests.Services;

/// <summary>Checks for the findings of the secure-coding review (KISA / OWASP / CWE).</summary>
public sealed class SecurityFixTests : IDisposable
{
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(@"..\escaped.txt")]
    [InlineData(@"D:\elsewhere\x.txt")]
    [InlineData("a/b")]
    [InlineData("  ")]
    public void Renames_and_new_folders_stay_inside_their_folder(string name)   // CWE-22
    {
        using var notes = SqliteNotesStore.Open(_dir.Combine(@"data\notes.db"));
        var files = new ShellFileOperations(notes, new UndoHistory(), new WindowOwner(), silent: true);
        string file = _dir.File(@"work\a.txt");

        Assert.Throws<ArgumentException>(() => files.Rename(file, name));
        Assert.Throws<ArgumentException>(() => files.CreateFolder(_dir.Combine("work"), name));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_pathological_wildcard_query_finishes_quickly()   // CWE-1333
    {
        string file = _dir.File("a.txt");
        var notes = new List<(string, FileNote)> { (file, new FileNote(new string('a', 20_000), DateTime.Now)) };
        var search = SearchLocation.Find(null, "*a*a*a*a*a*a*a*a*a*a*b", SearchFields.Memo, subfolders: true);
        var clock = Stopwatch.StartNew();

        SearchRunner.Run(search, notes, 0, _ => { }, CancellationToken.None);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Files_extracted_from_a_downloaded_zip_keep_the_mark_of_the_web()   // CWE-693
    {
        string zip = _dir.Combine("download.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry(@"inner/macro.docm").Open());
            writer.Write("pretend document");
        }
        const string zone = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.invalid/download.zip\r\n";
        File.WriteAllText(zip + ":Zone.Identifier", zone);

        ZipTools.Extract(zip, _dir.Combine("out"));

        Assert.Equal(zone, File.ReadAllText(_dir.Combine(@"out\inner\macro.docm") + ":Zone.Identifier"));
    }

    [Fact]
    public void A_zip_made_here_extracts_without_a_mark()
    {
        string zip = _dir.Combine("local.zip");
        ZipTools.Create(zip, new[] { _dir.File("a.txt") }, CancellationToken.None);
        ZipTools.Extract(zip, _dir.Combine("out"));
        Assert.Throws<FileNotFoundException>(() => File.ReadAllText(_dir.Combine(@"out\a.txt") + ":Zone.Identifier"));
    }

    [Theory]
    [InlineData("gpt-oss:120b-cloud")]
    [InlineData("deepseek-v3.1:671b-cloud")]
    [InlineData("qwen3-coder:cloud")]
    public void Ollama_cloud_models_are_not_treated_as_staying_on_this_PC(string model)   // CWE-359
    {
        var server = new AiServer { Name = "로컬 Ollama", Url = "http://localhost:11434", Model = model };
        Assert.True(server.HasLocalAddress);
        Assert.False(server.IsLocal);
        var choice = new AiChoice(AiProvider.Custom, server);
        Assert.False(choice.IsLocal);
        Assert.Contains("ollama.com", choice.Destination);
    }

    [Fact]
    public void An_unreadable_AI_settings_file_is_kept_before_defaults_replace_it()   // CWE-755
    {
        string path = _dir.Combine("ai.json");
        File.WriteAllText(path, "{ not json");
        var settings = new JsonAiSettingsStore(path).Load();
        Assert.Equal(AiProvider.Claude, settings.Provider);
        Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public void Settings_are_replaced_whole_without_leaving_a_temporary_file()
    {
        string path = _dir.Combine(@"data\settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { Width = 1234 });
        store.Save(new AppSettings { Width = 999 });
        Assert.Equal(999, store.Load().Width);
        Assert.False(File.Exists(path + ".tmp"));
    }
}
