using ClipMaster.Data;
using Microsoft.Data.Sqlite;

namespace ClipMaster.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cmtests_" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch { }
    }
}

public class DatabaseTests
{
    private static Database Open(TempDir t)
    {
        var db = new Database();
        db.Open(System.IO.Path.Combine(t.Path, "t.db"));
        return db;
    }

    [Fact]
    public void Upsert_SameContent_MovesToTop_WithoutDuplicating()
    {
        using var t = new TempDir(); using var db = Open(t);
        db.Upsert(ItemKind.Text, "A", "ha", 0, 0, 1);
        db.Upsert(ItemKind.Text, "B", "hb", 0, 0, 1);
        db.Upsert(ItemKind.Text, "A", "ha", 0, 0, 1); // 再コピー

        var items = db.GetItems();
        Assert.Equal(2, items.Count);
        Assert.Equal("A", items[0].Text); // 先頭へ移動
        Assert.Equal("B", items[1].Text);
    }

    [Fact]
    public void Touch_MovesUsedItemToTop()
    {
        using var t = new TempDir(); using var db = Open(t);
        var a = db.Upsert(ItemKind.Text, "A", "ha", 0, 0, 1);
        db.Upsert(ItemKind.Text, "B", "hb", 0, 0, 1);

        db.Touch(a); // A を使用

        var items = db.GetItems();
        Assert.Equal(2, items.Count);
        Assert.Equal("A", items[0].Text);
    }

    [Fact]
    public void Trim_RemovesOldestPerKind_AndReportsFreedImageHashes()
    {
        using var t = new TempDir(); using var db = Open(t);
        for (int i = 0; i < 5; i++) db.Upsert(ItemKind.Image, null, "img" + i, 10, 10, 100);
        for (int i = 0; i < 5; i++) db.Upsert(ItemKind.Text, "t" + i, "txt" + i, 0, 0, 2);

        var freed = db.Trim(ItemKind.Image, 2);

        Assert.Equal(new[] { "img2", "img1", "img0" }, freed); // 新しい順に並べた末尾から削除
        Assert.Equal(2, db.Count(ItemKind.Image));
        Assert.Equal(5, db.Count(ItemKind.Text)); // テキストには影響しない
    }

    [Fact]
    public void Delete_Image_ReturnsHash_ForFileCleanup()
    {
        using var t = new TempDir(); using var db = Open(t);
        var id = db.Upsert(ItemKind.Image, null, "imgX", 1, 1, 1);
        Assert.Equal("imgX", db.Delete(id));
        Assert.Null(db.GetItem(id));

        var tid = db.Upsert(ItemKind.Text, "x", "tx", 0, 0, 1);
        Assert.Null(db.Delete(tid)); // テキストは画像ファイルなし
    }

    [Fact]
    public void List_UsesShortPreview_ButGetItemReturnsFullText()
    {
        using var t = new TempDir(); using var db = Open(t);
        var big = new string('x', 5000);
        var id = db.Upsert(ItemKind.Text, big, "big", 0, 0, big.Length);

        Assert.Equal(300, db.GetItems()[0].Text!.Length); // 一覧は先頭のみ
        Assert.Equal(5000, db.GetItem(id)!.Text!.Length);  // 貼り付け用は全文
        Assert.Equal(5000, db.GetItems()[0].Bytes);        // 文字数は bytes 列
    }

    [Fact]
    public void Search_MatchesTextOnly_AndEscapesWildcards()
    {
        using var t = new TempDir(); using var db = Open(t);
        db.Upsert(ItemKind.Text, "100% done", "a", 0, 0, 1);
        db.Upsert(ItemKind.Text, "1000 done", "b", 0, 0, 1);
        db.Upsert(ItemKind.Image, null, "img", 1, 1, 1);

        var r = db.GetItems("100%");
        Assert.Single(r);
        Assert.Equal("100% done", r[0].Text);
        Assert.All(db.GetItems("done"), i => Assert.False(i.IsImage));
    }

    [Fact]
    public void Snippets_Crud()
    {
        using var t = new TempDir(); using var db = Open(t);
        var id = db.SaveSnippet(new Snippet { Category = "c", Title = "hello", Body = "body {DATE}" });
        Assert.Single(db.GetSnippets());
        db.SaveSnippet(new Snippet { Id = id, Category = "c", Title = "hello2", Body = "b2" });
        Assert.Equal("hello2", db.GetSnippets()[0].Title);
        Assert.Single(db.GetSnippets("b2"));
        db.DeleteSnippet(id);
        Assert.Empty(db.GetSnippets());
    }

    [Fact]
    public void CorruptDatabase_IsSetAside_AndRecreated()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "t.db");
        File.WriteAllText(path, new string('z', 4096)); // DB ではないファイル

        using var db = new Database();
        db.Open(path);
        db.Upsert(ItemKind.Text, "ok", "h", 0, 0, 2);

        Assert.Single(db.GetItems());
        Assert.Single(Directory.GetFiles(t.Path, "t.db.corrupt-*"));
    }

    [Fact]
    public void OldSchemaWithoutPreview_IsMigrated()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "t.db");
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = @"CREATE TABLE items(id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, text TEXT,
                hash TEXT NOT NULL, width INTEGER NOT NULL DEFAULT 0, height INTEGER NOT NULL DEFAULT 0,
                bytes INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL);
                INSERT INTO items(kind,text,hash,bytes,created_at) VALUES(0,'legacy','h',6,1);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var db = new Database();
        db.Open(path);
        Assert.Equal("legacy", db.GetItems()[0].Text); // preview が無くても substr で表示できる
    }
}
