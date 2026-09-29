namespace ClipMaster.Services;

public static class SnippetExpander
{
    public static string Expand(string body, AppConfig cfg)
    {
        var now = DateTime.Now;
        return body
            .Replace("{DATE}", now.ToString(cfg.DateFormat), StringComparison.OrdinalIgnoreCase)
            .Replace("{TIME}", now.ToString(cfg.TimeFormat), StringComparison.OrdinalIgnoreCase);
    }
}
