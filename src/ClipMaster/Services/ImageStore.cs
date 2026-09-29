using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace ClipMaster.Services;

/// <summary>原寸PNG(ハッシュ名)とサムネイルのファイル管理。</summary>
public sealed class ImageStore
{
    private readonly AppConfig _cfg;
    public ImageStore(AppConfig cfg) => _cfg = cfg;

    public string ImagePath(string hash) => Path.Combine(_cfg.ImagesDir, hash + ".png");
    public string ThumbPath(string hash) => Path.Combine(_cfg.ThumbsDir, hash + ".png");

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256(string text) => Sha256(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>PNGバイト列を保存しサムネイルを生成。既存なら再書き込みしない。</summary>
    public (string hash, int w, int h) Save(byte[] png)
    {
        var hash = Sha256(png);
        Directory.CreateDirectory(_cfg.ImagesDir);
        Directory.CreateDirectory(_cfg.ThumbsDir);

        int w, h;
        using (var ms = new MemoryStream(png))
        {
            var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            w = frame.PixelWidth; h = frame.PixelHeight;
        }

        var path = ImagePath(hash);
        if (!File.Exists(path)) WriteAtomic(path, png);

        var tpath = ThumbPath(hash);
        if (!File.Exists(tpath)) WriteAtomic(tpath, MakeThumb(png, w));
        return (hash, w, h);
    }

    private static byte[] MakeThumb(byte[] png, int width)
    {
        using var ms = new MemoryStream(png);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        if (width > 320) bi.DecodePixelWidth = 320; // 高DPI用に少し大きめ
        bi.EndInit();
        bi.Freeze();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bi));
        using var outMs = new MemoryStream();
        enc.Save(outMs);
        return outMs.ToArray();
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, true);
    }

    public void Delete(string hash)
    {
        TryDelete(ImagePath(hash));
        TryDelete(ThumbPath(hash));
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    /// <summary>DBに参照されないファイルを削除。</summary>
    public void CleanupOrphans(HashSet<string> referenced)
    {
        foreach (var dir in new[] { _cfg.ImagesDir, _cfg.ThumbsDir })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || !referenced.Contains(name))
                        TryDelete(f);
                }
            }
            catch { }
        }
    }

    public byte[]? LoadPng(string hash)
    {
        try
        {
            var p = ImagePath(hash);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        catch { return null; }
    }
}
