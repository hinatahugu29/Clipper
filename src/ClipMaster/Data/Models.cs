using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace ClipMaster.Data;

public enum ItemKind { Text = 0, Image = 1 }

public sealed class ClipItem
{
    public long Id { get; set; }
    public ItemKind Kind { get; set; }
    public string? Text { get; set; }
    public string Hash { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bytes { get; set; }
    public DateTime CreatedAt { get; set; }

    // 表示用
    public string? ImagePath { get; set; }
    public string? ThumbPath { get; set; }

    public bool IsImage => Kind == ItemKind.Image;

    public string Title
    {
        get
        {
            if (IsImage) return $"画像 ({Width}×{Height})";
            var t = (Text ?? "").Replace("\r", "");
            var lines = t.Split('\n', StringSplitOptions.None);
            var head = string.Join(" ⏎ ", lines.Take(2));
            return head.Length > 120 ? head[..120] + "…" : head;
        }
    }

    public string Meta => IsImage
        ? $"{CreatedAt:HH:mm:ss} | {FormatBytes(Bytes)}"
        : $"{CreatedAt:HH:mm:ss} | {Bytes}文字"; // テキストは bytes 列に文字数を保存している

    public string KindIcon => IsImage ? "🖼" : "📝";

    private static readonly Dictionary<string, BitmapImage> ThumbCache = new();
    public static void ClearThumbCache() => ThumbCache.Clear();

    private BitmapImage? _thumb;
    private bool _thumbTried;
    public BitmapImage? Thumb
    {
        get
        {
            if (!IsImage || _thumbTried) return _thumb;
            if (ThumbCache.TryGetValue(Hash, out var cached)) { _thumbTried = true; return _thumb = cached; }
            _thumbTried = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (ThumbPath != null && File.Exists(ThumbPath))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad; // ファイルをロックしない
                    bi.UriSource = new Uri(ThumbPath);
                    bi.DecodePixelWidth = 160;
                    bi.EndInit();
                    bi.Freeze();
                    _thumb = bi;
                    if (ThumbCache.Count > 300) ThumbCache.Clear();
                    ThumbCache[Hash] = bi;
                }
            }
            catch { /* 保管先アクセス不能などは空表示 */ }
            if (sw.ElapsedMilliseconds >= 5) ClipMaster.Services.Diag.Log($"thumb load {sw.ElapsedMilliseconds}ms");
            return _thumb;
        }
    }

    public static string FormatBytes(long b) =>
        b < 1024 ? $"{b}B" : b < 1024 * 1024 ? $"{b / 1024.0:0.#}KB" : $"{b / 1024.0 / 1024.0:0.#}MB";
}

public sealed class Snippet
{
    public long Id { get; set; }
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";

    public string Display => string.IsNullOrEmpty(Category) ? Title : $"[{Category}] {Title}";
}
