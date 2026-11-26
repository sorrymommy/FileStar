using FileStar.Tests.Support;

namespace FileStar.Tests.Services;

public class LocationsTests
{
    [Theory]
    [InlineData("내 PC")]
    [InlineData(" 내PC ")]
    [InlineData("This PC")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public void This_PC_is_recognized_however_it_is_typed(string typed)
    {
        Assert.True(Locations.IsThisPc(typed));
        Assert.Equal(Locations.ThisPc, Locations.Canonical(typed));
    }

    [Fact]
    public void Folders_are_normalized_and_searches_kept_as_they_are()
    {
        Assert.Equal(@"C:\Work", Locations.Canonical(@"C:\Work\"));
        Assert.Equal(@"C:\", Locations.Canonical("C:"));
        string search = SearchLocation.Tag("예산").Path;
        Assert.Equal(search, Locations.Canonical(search));
        Assert.True(Locations.IsVirtual(search));
    }

    [Theory]
    [InlineData(@"C:\Work", @"C:\Work", true)]
    [InlineData(@"C:\Work\Sub\a.txt", @"C:\work\", true)]
    [InlineData(@"C:\Workshop", @"C:\Work", false)]
    public void Same_or_under_compares_whole_path_segments(string path, string folder, bool expected) =>
        Assert.Equal(expected, Locations.IsSameOrUnder(path, folder));

    [Theory]
    [InlineData(@"\\NAS", true, true, @"\\NAS")]
    [InlineData(@"\\NAS\", true, true, @"\\NAS")]
    [InlineData(@"\\NAS\share\docs", true, false, @"\\NAS")]
    [InlineData(@"\\NAS/share", true, false, @"\\NAS/share")]
    [InlineData(@"\\wsl.localhost\Ubuntu", true, false, @"\\wsl.localhost")]
    [InlineData(@"\\?\C:\Work", false, false, null)]
    [InlineData(@"\\.\pipe\x", false, false, null)]
    [InlineData(@"C:\Work", false, false, null)]
    public void Network_paths_and_their_computer_are_recognized(string path, bool unc, bool server, string? serverOf)
    {
        Assert.Equal(unc, Locations.IsUnc(path));
        Assert.Equal(server, Locations.IsServer(path));
        Assert.Equal(serverOf, Locations.ServerOf(path)?.TrimEnd('\\'));
    }

    [Fact]
    public void Above_a_shared_folder_is_its_computer_and_above_that_nothing()
    {
        Assert.Equal(@"\\NAS\share", Locations.ParentOf(@"\\NAS\share\docs"));
        Assert.Equal(@"\\NAS", Locations.ParentOf(@"\\NAS\share"));
        Assert.Null(Locations.ParentOf(@"\\NAS"));
        Assert.Null(Locations.ParentOf(@"C:\"));
        Assert.Equal(@"C:\Work", Locations.ParentOf(@"C:\Work\Sub"));
        Assert.Null(Locations.ParentOf(@"\\wsl.localhost\Ubuntu"));   // "\\wsl.localhost" lists nothing
        Assert.Equal(@"\\wsl.localhost\Ubuntu", Locations.ParentOf(@"\\wsl.localhost\Ubuntu\home"));
        Assert.Equal(@"\\NAS", Locations.Canonical(@"\\NAS\"));
        Assert.True(Locations.IsFolder(@"\\NAS"));
    }

    [Fact]
    public void Wsl_paths_are_told_apart_from_other_network_paths()
    {
        Assert.True(Locations.IsWsl(@"\\wsl.localhost\Ubuntu\home"));
        Assert.True(Locations.IsWsl(@"\\WSL$\Ubuntu"));
        Assert.False(Locations.IsWsl(@"\\NAS\share"));
        Assert.False(Locations.IsWsl(@"C:\wsl.localhost"));
    }

    [Fact]
    public void A_shared_folder_is_named_by_its_share_not_its_whole_path()
    {
        Assert.Equal("share", Locations.ItemName(new System.IO.DirectoryInfo(@"\\NAS\share")));
        Assert.Equal("docs", Locations.ItemName(new System.IO.DirectoryInfo(@"\\NAS\share\docs")));
        Assert.Equal("Sub", Locations.ItemName(new System.IO.DirectoryInfo(@"C:\Work\Sub")));
    }

    [Fact]
    public void Child_name_is_only_given_for_direct_children()
    {
        Assert.Equal("Sub", Locations.ChildNameOf(@"C:\Work\Sub", @"C:\Work"));
        Assert.Null(Locations.ChildNameOf(@"C:\Work\Sub\Deeper", @"C:\Work"));
    }

    [Fact]
    public void New_folder_names_skip_taken_ones()
    {
        using var dir = new TempDir();
        Assert.Equal("새 폴더", Locations.NewFolderName(dir.Path));
        dir.Folder("새 폴더");
        dir.File("새 폴더 (2)");
        Assert.Equal("새 폴더 (3)", Locations.NewFolderName(dir.Path));
    }

    [Theory]
    [InlineData(0, "0 바이트")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void Sizes_read_like_Explorer(long bytes, string expected) => Assert.Equal(expected, SizeFormat.Bytes(bytes));

    [Fact]
    public void List_sizes_round_up_to_whole_KB() => Assert.Equal("1 KB", SizeFormat.KB(1));
}
