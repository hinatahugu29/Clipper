using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using ClipMaster.Data;
using ClipMaster.Native;
using ClipMaster.Services;
using ClipMaster.UI;
using Forms = System.Windows.Forms;

namespace ClipMaster;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppConfig _cfg = new();
    private Database _db = new();
    private ImageStore _images = null!;
    private MessageWindow _msg = null!;
    private HistoryService _history = null!;
    private HotkeyService _hotkey = null!;
    private PasteService _paste = null!;
    private PopupWindow _popup = null!;
    private Forms.NotifyIcon _tray = null!;
    private SnippetEditorWindow? _snippetEditor;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\ClipMaster.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("ClipMaster はすでに起動しています（タスクトレイを確認してください）。", "ClipMaster");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log(ex.Exception);
            ex.Handled = true; // 常駐アプリは例外で落とさない
        };

        _cfg = AppConfig.Load();
        if (!OpenStorageInteractive())
        {
            _mutex.ReleaseMutex();
            Shutdown();
            return;
        }

        _msg = new MessageWindow();
        _history = new HistoryService(_cfg, _db, _images);
        _history.Error += m => Log(new Exception(m));
        _msg.ClipboardUpdated += _history.OnClipboardUpdated;

        _paste = new PasteService(_images, _history, _msg.Handle);

        _popup = new PopupWindow(_db, _cfg, _images);
        _popup.Chosen += OnChosen;
        _popup.DeleteRequested += it => _history.DeleteItem(it);
        _popup.Prewarm();

        _hotkey = new HotkeyService(_msg);
        _hotkey.Triggered += OnTriggered;
        _hotkey.Apply(_cfg);

        BuildTray();

        if (_cfg.HotkeyEnabled && !_hotkey.HotkeyRegistered)
            _tray.ShowBalloonTip(4000, "ClipMaster", $"ホットキー {_cfg.Hotkey} を登録できませんでした（他のアプリが使用中の可能性）。", Forms.ToolTipIcon.Warning);
    }

    // 保管先を開けず一時的に既定の保管先で動いているとき、本来の設定値（config.json には本来の値を書き戻す）
    private string? _configuredRoot;

    /// <summary>保管先を開く。失敗時（ネットワークドライブ未接続など）は再試行/一時的に既定で起動/終了を選ばせる。</summary>
    private bool OpenStorageInteractive()
    {
        while (true)
        {
            try { OpenStorage(); return true; }
            catch (Exception ex)
            {
                Log(ex);
                var ans = MessageBox.Show(
                    $"保管先を開けませんでした。\n{_cfg.StorageRoot}\n\n{ex.Message}\n\n" +
                    "「はい」: 再試行\n「いいえ」: 今回だけ既定の保管先で起動（設定は変更しません）\n「キャンセル」: 終了",
                    "ClipMaster", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (ans == MessageBoxResult.Yes) continue;
                if (ans == MessageBoxResult.Cancel) return false;
                _configuredRoot = _cfg.StorageRoot;
                _cfg.StorageRoot = AppConfig.DefaultRoot;
                try { OpenStorage(); return true; }
                catch (Exception ex2)
                {
                    Log(ex2);
                    MessageBox.Show("既定の保管先も開けませんでした。\n" + ex2.Message, "ClipMaster", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
        }
    }

    private void SaveConfig()
    {
        var active = _cfg.StorageRoot;
        if (_configuredRoot != null) _cfg.StorageRoot = _configuredRoot; // 一時的な既定保管先を設定として保存しない
        _cfg.Save();
        _cfg.StorageRoot = active;
    }

    private void OpenStorage()
    {
        ClipItem.ClearThumbCache();
        Directory.CreateDirectory(_cfg.StorageRoot);
        _db.Open(_cfg.DbPath);
        _images ??= new ImageStore(_cfg); // 設定(_cfg)を参照するので保管先変更後も同一インスタンスで追従
        _images.CleanupOrphans(_db.AllImageHashes()); // 孤児ファイルの掃除
    }

    // ---- 呼び出し ----
    private void OnTriggered()
    {
        if (_popup.IsShown) { _popup.HidePopup(); return; }
        _paste.RememberForeground();
        var (x, y) = _cfg.PopupAtCaret ? CaretOrCursor() : Cursor();
        _popup.ShowAtPixel(x, y);
    }

    private static (int, int) Cursor()
    {
        NativeMethods.GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    private static (int, int) CaretOrCursor()
    {
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            uint tid = NativeMethods.GetWindowThreadProcessId(fg, out _);
            var info = new NativeMethods.GUITHREADINFO { cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            if (NativeMethods.GetGUIThreadInfo(tid, ref info) && info.hwndCaret != IntPtr.Zero)
            {
                var pt = new NativeMethods.POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
                if (NativeMethods.ClientToScreen(info.hwndCaret, ref pt)) return (pt.X, pt.Y + 4);
            }
        }
        catch { }
        return Cursor();
    }

    private async void OnChosen(PopupRow row, bool paste)
    {
        try
        {
            if (row.Item != null)
            {
                var full = row.Item.IsImage ? row.Item : _db.GetItem(row.Item.Id) ?? row.Item; // 一覧は先頭プレビューのみ
                await _paste.SendAsync(full, paste);
            }
            else if (row.Snippet != null)
                await _paste.SendTextAsync(SnippetExpander.Expand(row.Snippet.Body, _cfg), paste);
        }
        catch (Exception ex) { Log(ex); }
    }

    // ---- トレイ ----
    private void BuildTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "ClipMaster",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        var m = _tray.ContextMenuStrip;
        m.Items.Add("表示", null, (_, _) => Dispatcher.Invoke(OnTriggered));
        m.Items.Add("定型文編集", null, (_, _) => Dispatcher.Invoke(OpenSnippetEditor));
        m.Items.Add("履歴全消去", null, (_, _) => Dispatcher.Invoke(ClearHistory));
        m.Items.Add("設定", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        m.Items.Add(new Forms.ToolStripSeparator());
        m.Items.Add("終了", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(OnTriggered); };
    }

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(59, 120, 196));
            using var paper = new SolidBrush(Color.FromArgb(240, 240, 242));
            g.FillRectangle(body, 5, 5, 22, 25);
            g.FillRectangle(paper, 8, 10, 16, 17);
            g.FillRectangle(new SolidBrush(Color.FromArgb(90, 90, 96)), 11, 2, 10, 6);
            using var pen = new Pen(Color.FromArgb(120, 120, 128), 2);
            g.DrawLine(pen, 10, 15, 22, 15);
            g.DrawLine(pen, 10, 19, 22, 19);
            g.DrawLine(pen, 10, 23, 18, 23);
        }
        var h = bmp.GetHicon();
        return Icon.FromHandle(h);
    }

    private void OpenSnippetEditor()
    {
        if (_snippetEditor is { IsVisible: true }) { _snippetEditor.Activate(); return; }
        _snippetEditor = new SnippetEditorWindow(_db);
        _snippetEditor.Show();
        _snippetEditor.Activate();
    }

    private void ClearHistory()
    {
        if (MessageBox.Show("履歴（テキスト・画像）をすべて削除します。定型文は残ります。よろしいですか？",
                "ClipMaster", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _history.ClearAll();
    }

    private void OpenSettings()
    {
        var shown = _cfg.Clone();
        if (_configuredRoot != null) shown.StorageRoot = _configuredRoot; // 一時的な既定保管先ではなく設定値を表示
        var win = new SettingsWindow(shown);
        if (win.ShowDialog() != true || win.Result == null) return;
        var n = win.Result;

        var rootChanged = !string.Equals(Path.GetFullPath(n.StorageRoot), Path.GetFullPath(shown.StorageRoot), StringComparison.OrdinalIgnoreCase);
        if (rootChanged)
        {
            var ans = MessageBox.Show(
                $"保管先を変更します。\n\n{_cfg.StorageRoot}\n→ {n.StorageRoot}\n\n既存の履歴・画像を新しい保管先へ移動しますか？\n" +
                "「はい」: 移動する  /  「いいえ」: 移動せず新しい場所を空から開始  /  「キャンセル」: 変更しない",
                "ClipMaster", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Cancel) return;
            var old = _cfg.StorageRoot;
            try
            {
                _db.Close();
                if (ans == MessageBoxResult.Yes) StorageMigrator.Move(old, n.StorageRoot);
                _cfg.StorageRoot = n.StorageRoot;
                _configuredRoot = null;
                OpenStorage();
            }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show($"保管先の変更に失敗しました。元の保管先を使用します。\n{ex.Message}", "ClipMaster",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                _cfg.StorageRoot = old; // 変更前を維持
                try { OpenStorage(); } catch (Exception ex2) { Log(ex2); }
            }
        }

        _cfg.MaxTextItems = n.MaxTextItems;
        _cfg.MaxImageItems = n.MaxImageItems;
        _cfg.MaxTextChars = n.MaxTextChars;
        _cfg.NumberQuickSelect = n.NumberQuickSelect;
        _cfg.DoubleCtrlEnabled = n.DoubleCtrlEnabled;
        _cfg.DoubleCtrlIntervalMs = n.DoubleCtrlIntervalMs;
        _cfg.HotkeyEnabled = n.HotkeyEnabled;
        _cfg.Hotkey = n.Hotkey;
        _cfg.PopupAtCaret = n.PopupAtCaret;
        _cfg.RunAtStartup = n.RunAtStartup;
        SaveConfig();

        _hotkey.Apply(_cfg);
        SetStartup(_cfg.RunAtStartup);
        _history.ApplyLimits();
        _popup.Refresh();
    }

    private static void SetStartup(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (on) key.SetValue("ClipMaster", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("ClipMaster", false);
        }
        catch { }
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _hotkey.Dispose();
        _msg.Dispose();
        _db.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }

    private static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DefaultRoot);
            File.AppendAllText(Path.Combine(AppConfig.DefaultRoot, "error.log"), $"[{DateTime.Now:s}] {ex}\n");
        }
        catch { }
    }
}
