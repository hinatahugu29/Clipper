using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipMaster.Data;

namespace ClipMaster.Services;

/// <summary>クリップボードの変更を履歴(DB/画像ファイル)へ取り込む。</summary>
public sealed class HistoryService
{
    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly ImageStore _images;
    private readonly DispatcherTimer _debounce;
    private long _suppressUntilTicks;
    private bool _capturing;

    public event Action? Changed;
    public event Action<string>? Error;

    public HistoryService(AppConfig cfg, Database db, ImageStore images)
    {
        _cfg = cfg; _db = db; _images = images;
        // Snipping Tool 等が短時間に複数回通知するため、まとめて1回処理
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await CaptureAsync(); };
    }

    /// <summary>自身によるクリップボード書き込み後、一定時間の更新通知を無視する。</summary>
    public void SuppressFor(int ms) => _suppressUntilTicks = Environment.TickCount64 + ms;

    public void OnClipboardUpdated()
    {
        Diag.Log("WM_CLIPBOARDUPDATE");
        if (Environment.TickCount64 < _suppressUntilTicks) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task CaptureAsync()
    {
        if (_capturing) { _debounce.Start(); return; }
        _capturing = true;
        try
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    var ok = TryCapture();
                    Diag.Log($"capture => {ok}");
                    if (ok) Changed?.Invoke();
                    return;
                }
                catch (Exception ex) when (ex is COMException or ExternalException or IOException)
                {
                    // 他アプリがクリップボードを開いている等。少し待って再試行
                    Diag.Log($"retry {attempt}: {ex.GetType().Name} {ex.Message}");
                    await Task.Delay(60 * (attempt + 1));
                }
            }
        }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
        finally { _capturing = false; }
    }

    private bool TryCapture()
    {
        if (IsExcluded()) return false;

        // 画像とテキストが同時にある場合(Excel等)は両方を別項目で保存する。
        // 先にテキストを入れ、画像を後にして画像が先頭(最新)に来るようにする。
        string? text = ReadText();
        byte[]? png = ReadPng();
        bool stored = false;

        if (text != null)
        {
            _db.Upsert(ItemKind.Text, text, ImageStore.Sha256(text), 0, 0, text.Length);
            stored = true;
        }
        if (png != null)
        {
            var (hash, w, h) = _images.Save(png);
            _db.Upsert(ItemKind.Image, null, hash, w, h, png.LongLength);
            stored = true;
        }
        if (stored) Trim();
        return stored;
    }

    /// <summary>テキストを返す。無ければエクスプローラ等のファイルコピーをパス一覧(改行区切り)にする。</summary>
    private string? ReadText()
    {
        string? text = null;
        if (Clipboard.ContainsText()) text = Clipboard.GetText();
        else if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count > 0) text = string.Join(Environment.NewLine, files.Cast<string>());
        }

        if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return null;
        if (text.Length > _cfg.MaxTextChars) { Diag.Log($"text too long ({text.Length}), skipped"); return null; }
        return text;
    }

    /// <summary>パスワードマネージャー等が付ける「履歴に残さない」指定を尊重する。</summary>
    private static bool IsExcluded()
    {
        if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return true;
        if (Clipboard.ContainsData("CanIncludeInClipboardHistory"))
        {
            if (Clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4];
                ms.Position = 0; ms.Read(b, 0, 4);
                if (BitConverter.ToInt32(b, 0) == 0) return true;
            }
        }
        return false;
    }

    /// <summary>PNG形式を優先し、無ければ DIB 系(GetImage)から PNG を生成する。</summary>
    private static byte[]? ReadPng()
    {
        if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream pms && pms.Length > 0)
            return pms.ToArray();

        if (Clipboard.ContainsImage())
        {
            var src = Clipboard.GetImage();
            if (src == null) return null;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        return null;
    }

    private void Trim()
    {
        foreach (var hash in _db.Trim(ItemKind.Text, _cfg.MaxTextItems)) _ = hash;
        foreach (var hash in _db.Trim(ItemKind.Image, _cfg.MaxImageItems)) _images.Delete(hash);
    }

    /// <summary>設定変更後などに上限を再適用する。</summary>
    public void ApplyLimits()
    {
        Trim();
        Changed?.Invoke();
    }

    public void DeleteItem(ClipItem item)
    {
        var freed = _db.Delete(item.Id);
        if (freed != null) _images.Delete(freed);
        Changed?.Invoke();
    }

    public void ClearAll()
    {
        _db.ClearItems();
        _images.CleanupOrphans(new HashSet<string>());
        Changed?.Invoke();
    }
}
