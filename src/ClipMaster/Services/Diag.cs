using System.IO;

namespace ClipMaster.Services;

/// <summary>環境変数 CLIPMASTER_DEBUG=1 のときだけ診断ログを書く。</summary>
public static class Diag
{
    private static readonly bool On = Environment.GetEnvironmentVariable("CLIPMASTER_DEBUG") == "1";

    public static void Log(string msg)
    {
        if (!On) return;
        try
        {
            Directory.CreateDirectory(AppConfig.DefaultRoot);
            File.AppendAllText(Path.Combine(AppConfig.DefaultRoot, "debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }
}
