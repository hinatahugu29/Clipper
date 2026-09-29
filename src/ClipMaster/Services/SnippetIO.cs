using System.IO;
using System.Text;
using ClipMaster.Data;

namespace ClipMaster.Services;

/// <summary>定型文の CSV / TXT 入出力。</summary>
public static class SnippetIO
{
    private const string Separator = "=====";
    private static readonly string[] HeaderCategory = { "category", "カテゴリ" };

    // ---- 書き出し ----
    public static string ToCsv(IEnumerable<Snippet> items)
    {
        var sb = new StringBuilder();
        sb.Append("カテゴリ,タイトル,本文\r\n");
        foreach (var s in items)
            sb.Append(Quote(s.Category)).Append(',').Append(Quote(s.Title)).Append(',').Append(Quote(s.Body)).Append("\r\n");
        return sb.ToString();
    }

    public static string ToTxt(IEnumerable<Snippet> items)
    {
        var sb = new StringBuilder();
        foreach (var s in items)
        {
            sb.Append(Separator).Append("\r\n");
            sb.Append("カテゴリ: ").Append(OneLine(s.Category)).Append("\r\n");
            sb.Append("タイトル: ").Append(OneLine(s.Title)).Append("\r\n\r\n");
            sb.Append(s.Body.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n");
        }
        return sb.ToString();
    }

    public static void Export(string path, IEnumerable<Snippet> items)
    {
        var text = IsCsv(path) ? ToCsv(items) : ToTxt(items);
        File.WriteAllText(path, text, new UTF8Encoding(true));
    }

    // ---- 読み込み ----
    public static List<Snippet> Import(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return IsCsv(path)
            ? ParseCsv(text)
            : ParseTxt(text, Path.GetFileNameWithoutExtension(path));
    }

    public static List<Snippet> ParseCsv(string text)
    {
        var rows = ReadCsvRows(text);
        var result = new List<Snippet>();
        int start = 0;
        if (rows.Count > 0 && HeaderCategory.Contains(rows[0][0].Trim(), StringComparer.OrdinalIgnoreCase)) start = 1;
        for (int i = start; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Count < 2) continue;
            var s = new Snippet { Category = r[0].Trim(), Title = r[1].Trim(), Body = r.Count > 2 ? r[2] : "" };
            if (s.Title.Length == 0) s.Title = FirstLine(s.Body);
            if (s.Title.Length == 0) continue;
            result.Add(s);
        }
        return result;
    }

    public static List<Snippet> ParseTxt(string text, string fallbackTitle)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<Snippet>();
        if (!lines.Any(l => l.TrimEnd() == Separator))
        {
            // 区切りなし: ファイル全体を 1 件の定型文として取り込む
            var body = text.Trim('\r', '\n');
            if (body.Length > 0) result.Add(new Snippet { Title = fallbackTitle, Body = body });
            return result;
        }

        int i = 0;
        while (i < lines.Length)
        {
            if (lines[i].TrimEnd() != Separator) { i++; continue; }
            i++;
            string category = "", title = "";
            while (i < lines.Length && lines[i].TrimEnd() != Separator)
            {
                var l = lines[i];
                if (l.StartsWith("カテゴリ:")) { category = l["カテゴリ:".Length..].Trim(); i++; }
                else if (l.StartsWith("タイトル:")) { title = l["タイトル:".Length..].Trim(); i++; }
                else break;
            }
            if (i < lines.Length && lines[i].Length == 0) i++; // ヘッダと本文の間の空行
            var bodyLines = new List<string>();
            while (i < lines.Length && lines[i].TrimEnd() != Separator) bodyLines.Add(lines[i++]);
            while (bodyLines.Count > 0 && bodyLines[^1].Length == 0) bodyLines.RemoveAt(bodyLines.Count - 1);
            var b = string.Join("\r\n", bodyLines);
            if (title.Length == 0) title = FirstLine(b);
            if (title.Length == 0) continue;
            result.Add(new Snippet { Category = category, Title = title, Body = b });
        }
        return result;
    }

    // ---- CSV 下請け ----
    private static bool IsCsv(string path) => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase);

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ");

    private static string FirstLine(string s)
    {
        var line = s.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 40 ? line[..40] : line;
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private static List<List<string>> ReadCsvRows(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
                    row = new List<string>(); cell.Clear(); any = false;
                    break;
                default: cell.Append(c); any = true; break;
            }
        }
        if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
