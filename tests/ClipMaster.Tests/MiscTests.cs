using ClipMaster.Services;

namespace ClipMaster.Tests;

public class HotkeyParseTests
{
    [Theory]
    [InlineData("Ctrl+Shift+V", true)]
    [InlineData("ctrl + alt + F1", true)]
    [InlineData("Alt+V", true)]
    [InlineData("V", false)]          // 修飾キーなしは不可
    [InlineData("Ctrl+", false)]
    [InlineData("Ctrl+Foo", false)]
    [InlineData("", false)]
    public void TryParse(string s, bool expected) =>
        Assert.Equal(expected, HotkeyService.TryParse(s, out _, out _));
}

public class SnippetExpanderTests
{
    [Fact]
    public void Expands_DateAndTime_CaseInsensitive()
    {
        var cfg = new AppConfig { DateFormat = "yyyy", TimeFormat = "HH" };
        var r = SnippetExpander.Expand("{DATE}/{time}/{X}", cfg);
        Assert.Equal($"{DateTime.Now:yyyy}/{DateTime.Now:HH}/{{X}}", r);
    }
}

public class StorageMigratorTests
{
    [Fact]
    public void Move_CopiesAllData_AndRemovesOldSide()
    {
        using var a = new TempDir(); using var b = new TempDir();
        File.WriteAllText(Path.Combine(a.Path, "clipmaster.db"), "db");
        Directory.CreateDirectory(Path.Combine(a.Path, "images"));
        File.WriteAllText(Path.Combine(a.Path, "images", "x.png"), "img");
        Directory.CreateDirectory(Path.Combine(a.Path, "thumbs"));
        File.WriteAllText(Path.Combine(a.Path, "thumbs", "x.png"), "th");

        StorageMigrator.Move(a.Path, b.Path);

        Assert.True(File.Exists(Path.Combine(b.Path, "clipmaster.db")));
        Assert.True(File.Exists(Path.Combine(b.Path, "images", "x.png")));
        Assert.True(File.Exists(Path.Combine(b.Path, "thumbs", "x.png")));
        Assert.False(File.Exists(Path.Combine(a.Path, "clipmaster.db")));
        Assert.False(Directory.Exists(Path.Combine(a.Path, "images")));
    }

    [Fact]
    public void Move_ToSameFolder_IsNoOp()
    {
        using var a = new TempDir();
        File.WriteAllText(Path.Combine(a.Path, "clipmaster.db"), "db");
        StorageMigrator.Move(a.Path, a.Path);
        Assert.True(File.Exists(Path.Combine(a.Path, "clipmaster.db")));
    }
}

public class ImageStoreTests
{
    [Fact]
    public void CleanupOrphans_RemovesUnreferencedFilesOnly()
    {
        using var t = new TempDir();
        var cfg = new AppConfig { StorageRoot = t.Path };
        var store = new ImageStore(cfg);
        Directory.CreateDirectory(cfg.ImagesDir); Directory.CreateDirectory(cfg.ThumbsDir);
        File.WriteAllText(store.ImagePath("keep"), "1"); File.WriteAllText(store.ThumbPath("keep"), "1");
        File.WriteAllText(store.ImagePath("orphan"), "1"); File.WriteAllText(store.ThumbPath("orphan"), "1");
        File.WriteAllText(store.ImagePath("half") + ".tmp", "1"); // 書き込み途中の残骸

        store.CleanupOrphans(new HashSet<string> { "keep" });

        Assert.True(File.Exists(store.ImagePath("keep")));
        Assert.True(File.Exists(store.ThumbPath("keep")));
        Assert.False(File.Exists(store.ImagePath("orphan")));
        Assert.False(File.Exists(store.ThumbPath("orphan")));
        Assert.False(File.Exists(store.ImagePath("half") + ".tmp"));
    }

    [Fact]
    public void Sha256_IsStable()
    {
        Assert.Equal(ImageStore.Sha256("a"), ImageStore.Sha256("a"));
        Assert.NotEqual(ImageStore.Sha256("a"), ImageStore.Sha256("b"));
    }
}
