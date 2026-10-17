using SafeCraftMcp.Registry;

namespace SafeCraftMcp.Tests;

// A temp state folder and a fake "binary" (just bytes); nothing is executed.
public sealed class BinaryFreezerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("safecraft-freeze-").FullName;
    private readonly string _source;
    private readonly string _state;
    private readonly AppManifest _app = AppRegistry.LoadEmbedded().Find("photocraft")!;

    public BinaryFreezerTests()
    {
        _source = Path.Combine(_root, "photocraft-cli.exe");
        File.WriteAllText(_source, "build one");
        _state = Path.Combine(_root, "state");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void The_Copy_Sits_In_A_Folder_Named_By_Its_Hash_And_Is_Read_Only()
    {
        var frozen = BinaryFreezer.Freeze(_source, _app, _state, pin: null);

        Assert.Equal(Path.Combine(_state, "bin", "photocraft", frozen.Sha256, "photocraft-cli.exe"), frozen.Path);
        Assert.Equal("build one", File.ReadAllText(frozen.Path));
        Assert.True(File.GetAttributes(frozen.Path).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_state, "bin", "photocraft"), "*.tmp"));
    }

    [Fact]
    public void Freezing_Again_Reuses_The_Copy_And_A_New_Build_Gets_A_New_Folder()
    {
        var first = BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        var again = BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        File.WriteAllText(_source, "build two");
        var second = BinaryFreezer.Freeze(_source, _app, _state, pin: null);

        Assert.Equal(first, again);
        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.Equal("build one", File.ReadAllText(first.Path));
    }

    [Fact]
    public void A_Pin_Mismatch_Refuses_And_Leaves_Nothing_Behind()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => BinaryFreezer.Freeze(_source, _app, _state, pin: new string('0', 64)));

        Assert.Contains("pinned", refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_state, "bin", "photocraft")));
    }

    [Fact]
    public void A_Matching_Pin_Freezes()
    {
        var hash = BinaryFreezer.Sha256Of(_source);

        Assert.Equal(hash, BinaryFreezer.Freeze(_source, _app, _state, pin: hash).Sha256);
    }

    [Fact]
    public void A_Tampered_Frozen_Copy_Is_Detected()
    {
        var frozen = BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        File.SetAttributes(frozen.Path, FileAttributes.Normal);
        File.WriteAllText(frozen.Path, "swapped");

        Assert.Throws<InvalidOperationException>(() => BinaryFreezer.Freeze(_source, _app, _state, pin: null));
    }

    [Fact]
    public async Task Two_Gateways_Freezing_At_Once_Get_The_Same_Copy()
    {
        // Big enough that the copies overlap and the rename race really happens.
        File.WriteAllBytes(_source, new byte[30 * 1024 * 1024]);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => BinaryFreezer.Freeze(_source, _app, _state, pin: null))));

        Assert.Single(results.Distinct());
    }

    private static string FolderOf(FrozenBinary copy) => Path.GetDirectoryName(copy.Path)!;

    private static void Age(FrozenBinary copy, int days) =>
        Directory.SetLastWriteTimeUtc(FolderOf(copy), DateTime.UtcNow.AddDays(-days));

    [Fact]
    public void Copies_Unused_For_Two_Weeks_Go_But_Each_Apps_Newest_Stays()
    {
        var old = BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        File.WriteAllText(_source, "build two");
        var recent = BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        File.WriteAllText(_source, "build three");
        var newest = BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        Age(old, 30);
        Age(recent, 3);
        // An app whose only copy is old keeps it.
        var only = BinaryFreezer.Freeze(_source, AppRegistry.LoadEmbedded().Find("vectorcraft")!, _state, pin: null);
        Age(only, 30);

        var deleted = BinaryFreezer.DeleteUnused(_state, TimeSpan.FromDays(14));

        Assert.Equal([FolderOf(old)], deleted);
        Assert.False(Directory.Exists(FolderOf(old)));
        Assert.All([recent, newest, only], copy => Assert.True(File.Exists(copy.Path)));
    }

    [Fact]
    public void Freezing_Marks_An_Old_Copy_As_Used()
    {
        var first = BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        File.WriteAllText(_source, "build two");
        BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        Age(first, 30);
        File.WriteAllText(_source, "build one");

        BinaryFreezer.Freeze(_source, _app, _state, pin: null);

        Assert.Empty(BinaryFreezer.DeleteUnused(_state, TimeSpan.FromDays(14)));
    }

    [Fact]
    public void A_Copy_In_Use_Stays_Read_Only()
    {
        var old = BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        File.WriteAllText(_source, "build two");
        BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        Age(old, 30);

        IReadOnlyList<string> deleted;
        // Held open without delete sharing, as Windows holds a running exe.
        using (new FileStream(old.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            deleted = BinaryFreezer.DeleteUnused(_state, TimeSpan.FromDays(14));

        Assert.Empty(deleted);
        Assert.True(File.GetAttributes(old.Path).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Only_Hash_Named_Folders_Are_Touched()
    {
        BinaryFreezer.Freeze(_source, _app, _state, pin: null);
        var stray = Directory.CreateDirectory(Path.Combine(_state, "bin", "photocraft", "notes")).FullName;
        Directory.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddDays(-30));

        Assert.Empty(BinaryFreezer.DeleteUnused(_state, TimeSpan.FromDays(14)));
        Assert.True(Directory.Exists(stray));
    }
}
