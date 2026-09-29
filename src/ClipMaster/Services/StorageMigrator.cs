using System.IO;

namespace ClipMaster.Services;

public static class StorageMigrator
{
    /// <summary>旧保管先のDB・画像・サムネイルを新保管先へコピー→旧側を削除。DBは閉じた状態で呼ぶこと。</summary>
    public static void Move(string oldRoot, string newRoot)
    {
        if (string.Equals(Path.GetFullPath(oldRoot), Path.GetFullPath(newRoot), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(newRoot);

        foreach (var name in new[] { "clipmaster.db", "clipmaster.db-wal", "clipmaster.db-shm" })
        {
            var src = Path.Combine(oldRoot, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(newRoot, name), true);
        }
        foreach (var sub in new[] { "images", "thumbs" })
        {
            var s = Path.Combine(oldRoot, sub);
            if (!Directory.Exists(s)) continue;
            var d = Path.Combine(newRoot, sub);
            Directory.CreateDirectory(d);
            foreach (var f in Directory.EnumerateFiles(s))
                File.Copy(f, Path.Combine(d, Path.GetFileName(f)), true);
        }

        // コピー成功後に旧データを削除（失敗しても致命的ではない）
        foreach (var name in new[] { "clipmaster.db", "clipmaster.db-wal", "clipmaster.db-shm" })
            try { File.Delete(Path.Combine(oldRoot, name)); } catch { }
        foreach (var sub in new[] { "images", "thumbs" })
            try { Directory.Delete(Path.Combine(oldRoot, sub), true); } catch { }
    }
}
