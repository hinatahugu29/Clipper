using System.IO;
using Microsoft.Data.Sqlite;

namespace ClipMaster.Data;

public sealed class Database : IDisposable
{
    private SqliteConnection? _conn;
    private readonly object _lock = new();

    public void Open(string path)
    {
        try { OpenCore(path); }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            // DB 破損: 退避して作り直す（履歴は失われるが起動不能を避ける）
            lock (_lock) Close();
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(path, path + ".corrupt-" + stamp, true);
            foreach (var ext in new[] { "-wal", "-shm" }) { try { File.Delete(path + ext); } catch { } }
            OpenCore(path);
        }
    }

    private void OpenCore(string path)
    {
        lock (_lock)
        {
            Close();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _conn = new SqliteConnection($"Data Source={path}");
            _conn.Open();
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA synchronous=NORMAL;");
            Exec(@"CREATE TABLE IF NOT EXISTS items(
                     id INTEGER PRIMARY KEY AUTOINCREMENT,
                     kind INTEGER NOT NULL,
                     text TEXT,
                     preview TEXT,
                     hash TEXT NOT NULL,
                     width INTEGER NOT NULL DEFAULT 0,
                     height INTEGER NOT NULL DEFAULT 0,
                     bytes INTEGER NOT NULL DEFAULT 0,
                     created_at INTEGER NOT NULL);
                   CREATE UNIQUE INDEX IF NOT EXISTS ix_items_kind_hash ON items(kind, hash);
                   CREATE INDEX IF NOT EXISTS ix_items_created ON items(created_at DESC);
                   CREATE TABLE IF NOT EXISTS snippets(
                     id INTEGER PRIMARY KEY AUTOINCREMENT,
                     category TEXT NOT NULL DEFAULT '',
                     title TEXT NOT NULL,
                     body TEXT NOT NULL);");
            if (!HasColumn("items", "preview")) Exec("ALTER TABLE items ADD COLUMN preview TEXT");
        }
    }

    private bool HasColumn(string table, string column)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public void Close()
    {
        lock (_lock)
        {
            _conn?.Dispose();
            _conn = null;
            SqliteConnection.ClearAllPools();
        }
    }

    public void Dispose() => Close();

    private void Exec(string sql, params (string, object?)[] ps)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private const int PreviewChars = 300;
    // 一覧用: 長文を丸ごと読まないよう preview 列（無ければ先頭のみ）を使う。全文は GetItem で取得する。
    private const string ListCols = "id,kind,COALESCE(preview,substr(text,1,300)),hash,width,height,bytes,created_at";
    private const string FullCols = "id,kind,text,hash,width,height,bytes,created_at";

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>同一(kind,hash)があれば先頭へ移動、無ければ追加。</summary>
    public long Upsert(ItemKind kind, string? text, string hash, int w, int h, long bytes)
    {
        lock (_lock)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"INSERT INTO items(kind,text,preview,hash,width,height,bytes,created_at)
                                VALUES($k,$t,$p,$h,$w,$ht,$b,$c)
                                ON CONFLICT(kind,hash) DO UPDATE SET created_at=excluded.created_at
                                RETURNING id;";
            cmd.Parameters.AddWithValue("$k", (int)kind);
            cmd.Parameters.AddWithValue("$t", (object?)text ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", (object?)(text == null ? null : text.Length > PreviewChars ? text[..PreviewChars] : text) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$w", w);
            cmd.Parameters.AddWithValue("$ht", h);
            cmd.Parameters.AddWithValue("$b", bytes);
            // 同一ミリ秒でも順序が崩れないよう単調増加させる
            cmd.Parameters.AddWithValue("$c", NextStamp());
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>使用した項目を先頭へ移動する（created_at を「最後に使った時刻」として更新）。</summary>
    public void Touch(long id)
    {
        lock (_lock)
        {
            Exec("UPDATE items SET created_at=$c WHERE id=$i",
                 ("$c", NextStamp()), ("$i", id));
        }
    }

    private long _lastStamp;
    private long NextStamp()
    {
        var n = NowMs();
        if (n <= _lastStamp) n = _lastStamp + 1;
        _lastStamp = n;
        return n;
    }

    public List<ClipItem> GetItems(string? search = null, int limit = 1000)
    {
        lock (_lock)
        {
            var list = new List<ClipItem>();
            using var cmd = _conn!.CreateCommand();
            var where = "";
            if (!string.IsNullOrWhiteSpace(search))
            {
                // テキストのみ検索対象（画像は検索中は非表示）
                where = "WHERE kind=0 AND text LIKE $q ESCAPE '\\'";
                cmd.Parameters.AddWithValue("$q", "%" + Escape(search.Trim()) + "%");
            }
            cmd.CommandText = $"SELECT {ListCols} FROM items {where} ORDER BY created_at DESC LIMIT {limit}";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(Read(r));
            return list;
        }
    }

    public ClipItem? GetItem(long id)
    {
        lock (_lock)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT " + FullCols + " FROM items WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static ClipItem Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Kind = (ItemKind)r.GetInt32(1),
        Text = r.IsDBNull(2) ? null : r.GetString(2),
        Hash = r.GetString(3),
        Width = r.GetInt32(4),
        Height = r.GetInt32(5),
        Bytes = r.GetInt64(6),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)).LocalDateTime,
    };

    public int Count(ItemKind kind)
    {
        lock (_lock)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM items WHERE kind=$k";
            cmd.Parameters.AddWithValue("$k", (int)kind);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>削除し、画像の場合は他から参照されなくなったハッシュを返す。</summary>
    public string? Delete(long id)
    {
        lock (_lock)
        {
            var item = GetItem(id);
            if (item == null) return null;
            Exec("DELETE FROM items WHERE id=$i", ("$i", id));
            return item.IsImage && !HashReferenced(item.Hash) ? item.Hash : null;
        }
    }

    private bool HashReferenced(string hash)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE kind=1 AND hash=$h";
        cmd.Parameters.AddWithValue("$h", hash);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>種別ごとの上限を超えた古い履歴を削除。解放された画像ハッシュを返す。</summary>
    public List<string> Trim(ItemKind kind, int max)
    {
        lock (_lock)
        {
            var freed = new List<string>();
            var victims = new List<(long id, string hash)>();
            using (var cmd = _conn!.CreateCommand())
            {
                cmd.CommandText = "SELECT id,hash FROM items WHERE kind=$k ORDER BY created_at DESC LIMIT -1 OFFSET $o";
                cmd.Parameters.AddWithValue("$k", (int)kind);
                cmd.Parameters.AddWithValue("$o", max);
                using var r = cmd.ExecuteReader();
                while (r.Read()) victims.Add((r.GetInt64(0), r.GetString(1)));
            }
            foreach (var (id, hash) in victims)
            {
                Exec("DELETE FROM items WHERE id=$i", ("$i", id));
                if (kind == ItemKind.Image && !HashReferenced(hash)) freed.Add(hash);
            }
            return freed;
        }
    }

    public void ClearItems()
    {
        lock (_lock) Exec("DELETE FROM items");
    }

    public HashSet<string> AllImageHashes()
    {
        lock (_lock)
        {
            var set = new HashSet<string>();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT hash FROM items WHERE kind=1";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    // ---- スニペット ----
    public List<Snippet> GetSnippets(string? search = null)
    {
        lock (_lock)
        {
            var list = new List<Snippet>();
            using var cmd = _conn!.CreateCommand();
            var where = "";
            if (!string.IsNullOrWhiteSpace(search))
            {
                where = "WHERE title LIKE $q ESCAPE '\\' OR body LIKE $q ESCAPE '\\' OR category LIKE $q ESCAPE '\\'";
                cmd.Parameters.AddWithValue("$q", "%" + Escape(search.Trim()) + "%");
            }
            cmd.CommandText = $"SELECT id,category,title,body FROM snippets {where} ORDER BY category, title";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Snippet { Id = r.GetInt64(0), Category = r.GetString(1), Title = r.GetString(2), Body = r.GetString(3) });
            return list;
        }
    }

    public long SaveSnippet(Snippet s)
    {
        lock (_lock)
        {
            if (s.Id == 0)
            {
                using var cmd = _conn!.CreateCommand();
                cmd.CommandText = "INSERT INTO snippets(category,title,body) VALUES($c,$t,$b) RETURNING id";
                cmd.Parameters.AddWithValue("$c", s.Category);
                cmd.Parameters.AddWithValue("$t", s.Title);
                cmd.Parameters.AddWithValue("$b", s.Body);
                return (long)cmd.ExecuteScalar()!;
            }
            Exec("UPDATE snippets SET category=$c,title=$t,body=$b WHERE id=$i",
                ("$c", s.Category), ("$t", s.Title), ("$b", s.Body), ("$i", s.Id));
            return s.Id;
        }
    }

    public void DeleteSnippet(long id)
    {
        lock (_lock) Exec("DELETE FROM snippets WHERE id=$i", ("$i", id));
    }
}
