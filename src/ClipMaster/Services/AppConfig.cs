using System.IO;
using System.Text.Json;

namespace ClipMaster.Services;

public sealed class AppConfig
{
    public string StorageRoot { get; set; } = DefaultRoot;
    public int MaxTextItems { get; set; } = 200;
    public int MaxImageItems { get; set; } = 50;
    public int MaxTextChars { get; set; } = 1_000_000; // これを超えるテキストは履歴に保存しない
    public bool NumberQuickSelect { get; set; } = true; // 検索欄が空のとき 1〜9 で即貼り付け
    public bool DoubleCtrlEnabled { get; set; } = true;
    public int DoubleCtrlIntervalMs { get; set; } = 300;
    public bool HotkeyEnabled { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Shift+V";
    public bool PopupAtCaret { get; set; } = false;
    public bool RunAtStartup { get; set; } = false;
    public string DateFormat { get; set; } = "yyyy/MM/dd";
    public string TimeFormat { get; set; } = "HH:mm:ss";

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    // CLIPMASTER_HOME はテスト・ポータブル運用用の上書き
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("CLIPMASTER_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipMaster");

    // config.json は保管先を変更しても見つかるよう固定位置に置く
    public static string ConfigPath => Path.Combine(DefaultRoot, "config.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (cfg != null) { cfg.Normalize(); return cfg; }
            }
        }
        catch { /* 破損時は既定値 */ }
        return new AppConfig();
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(DefaultRoot);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(StorageRoot)) StorageRoot = DefaultRoot;
        MaxTextItems = Math.Clamp(MaxTextItems, 1, 100000);
        MaxImageItems = Math.Clamp(MaxImageItems, 1, 100000);
        MaxTextChars = Math.Clamp(MaxTextChars, 1000, 100_000_000);
        DoubleCtrlIntervalMs = Math.Clamp(DoubleCtrlIntervalMs, 100, 1000);
    }

    public string DbPath => Path.Combine(StorageRoot, "clipmaster.db");
    public string ImagesDir => Path.Combine(StorageRoot, "images");
    public string ThumbsDir => Path.Combine(StorageRoot, "thumbs");
}
