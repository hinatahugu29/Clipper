using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipMaster.Data;
using ClipMaster.Native;
using ClipMaster.Services;

namespace ClipMaster.UI;

public sealed class PopupRow
{
    public int Number { get; set; }
    public ClipItem? Item { get; init; }
    public Snippet? Snippet { get; init; }

    public string Title => Item?.Title ?? Snippet!.Display;
    public string Meta => Item?.Meta ?? SnippetPreview();
    public BitmapImage? Thumb => Item?.Thumb;
    public bool HasThumb => Item is { IsImage: true } && Item.Thumb != null;
    public bool ShowIcon => !HasThumb;
    public string Icon => Item?.KindIcon ?? "📌";

    private string SnippetPreview()
    {
        var b = (Snippet!.Body ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
        return b.Length > 70 ? b[..70] + "…" : b;
    }
}

public partial class PopupWindow : Window
{
    private readonly Database _db;
    private readonly AppConfig _cfg;
    private bool _snippetMode;
    private bool _hiding;
    private long _showGuardUntil; // 表示直後の一時的な非アクティブ化で閉じてしまわないための猶予
    private bool _forceSearch;    // Ctrl+F 後は数字キーを検索入力として扱う

    public event Action<PopupRow, bool>? Chosen;   // (row, paste)
    public event Action<ClipItem>? DeleteRequested;
    /// <summary>ピン中にフォーカスを失った（貼り付け先が変わり得る）ときに通知する。</summary>
    public event Action? PinnedDeactivated;

    /// <summary>ピン留め中は、フォーカスを失っても貼り付けても閉じない。</summary>
    public bool IsPinned { get; private set; }

    private readonly ImageStore _images;

    public PopupWindow(Database db, AppConfig cfg, ImageStore images)
    {
        InitializeComponent();
        _db = db; _cfg = cfg; _images = images;
        _outsideClickTimer.Tick += (_, _) => OutsideClickTick();
        List.SelectionChanged += (_, _) => { if (PreviewLayer.Visibility == Visibility.Visible) UpdatePreview(); };
    }

    /// <summary>起動時に一度だけ画面外で表示→非表示にして、テンプレート生成コストを先払いする。</summary>
    public void Prewarm()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; Top = -10000;
        ShowActivated = false;
        _showGuardUntil = Environment.TickCount64 + 400;
        Show();
        Refresh();
        UpdateLayout();
        Hide();
        ShowActivated = true;
    }

    public bool IsShown => IsVisible && !_hiding;

    public void ShowAtPixel(int x, int y)
    {
        var swShow = System.Diagnostics.Stopwatch.StartNew();
        _hiding = false;
        _snippetMode = false;
        _forceSearch = false;
        PreviewLayer.Visibility = Visibility.Collapsed;
        _hoverRow = null; _mouseLast = false;
        HistoryTab.IsChecked = true;
        SearchBox.Text = "";
        Refresh();

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x - 24, y - 24, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        // 移動先モニタのDPIで実サイズを求め、作業領域内に収める
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Ceiling(Width * scale), h = (int)Math.Ceiling(Height * scale);
        var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        int nx = Math.Clamp(x - 24, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int ny = Math.Clamp(y - 24, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, nx, ny, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        _showGuardUntil = Environment.TickCount64 + 400;
        Show();
        _mouseWasDown = true; // 呼び出し時点で押されているボタンは無視（離してから次の押下を検知）
        _outsideClickTimer.Start();
        var ok =PasteService.ActivateWindow(hwnd);
        Diag.Log($"popup shown, foreground acquired={ok}, ShowAtPixel total={swShow.ElapsedMilliseconds}ms");
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    public void HidePopup()
    {
        if (_hiding) return;
        _hiding = true;
        SetPinned(false); // 次回の呼び出しは通常動作に戻す
        _outsideClickTimer.Stop();
        Hide();
    }

    // フォーカス喪失(Deactivated)に頼らず、枠外クリックを直接検知する保険。
    // 呼び出し時に前面化できなかった場合など、Deactivated が発火しなくても閉じられる。
    private readonly System.Windows.Threading.DispatcherTimer _outsideClickTimer =
        new() { Interval = TimeSpan.FromMilliseconds(30) };
    private bool _mouseWasDown;

    private void OutsideClickTick()
    {
        bool down = (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0   // 左
                 || (NativeMethods.GetAsyncKeyState(0x02) & 0x8000) != 0   // 右
                 || (NativeMethods.GetAsyncKeyState(0x04) & 0x8000) != 0;  // 中
        bool pressed = down && !_mouseWasDown;
        _mouseWasDown = down;
        if (!pressed || _hiding || !IsVisible || IsPinned || _menuOpen) return;
        if (Environment.TickCount64 < _showGuardUntil) return;

        NativeMethods.GetCursorPos(out var p);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var origin = PointToScreen(new Point(0, 0));
        // 影の余白(Margin=8)を除いた見た目の枠で判定する
        double m = 8 * scale;
        bool inside = p.X >= origin.X + m && p.X <= origin.X + ActualWidth * scale - m
                   && p.Y >= origin.Y + m && p.Y <= origin.Y + ActualHeight * scale - m;
        if (!inside) HidePopup();
    }

    /// <summary>ピン中で背面にあるポップアップを、再びアクティブにする。</summary>
    public void BringToFront()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _showGuardUntil = Environment.TickCount64 + 400;
        PasteService.ActivateWindow(hwnd);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void SetPinned(bool on)
    {
        IsPinned = on;
        PinIcon.Opacity = on ? 1.0 : 0.45;
        PinButton.Background = on ? (Brush)FindResource("Accent") : (Brush)FindResource("Bg2");
    }

    private void PinButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SetPinned(!IsPinned);
        SearchBox.Focus();
        e.Handled = true;
    }

    public void Refresh()
    {
        _hoverRow = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var search = SearchBox.Text;
        var rows = new List<PopupRow>();
        long tDb = 0;
        if (_snippetMode)
        {
            foreach (var s in _db.GetSnippets(search)) rows.Add(new PopupRow { Snippet = s });
        }
        else
        {
            var items = _db.GetItems(search);
            tDb = sw.ElapsedMilliseconds;
            foreach (var i in items)
            {
                if (i.IsImage) { i.ImagePath = _images.ImagePath(i.Hash); i.ThumbPath = _images.ThumbPath(i.Hash); }
                rows.Add(new PopupRow { Item = i });
            }
        }
        for (int n = 0; n < rows.Count; n++) rows[n].Number = n + 1;
        var tRows = sw.ElapsedMilliseconds;
        List.ItemsSource = rows;
        if (rows.Count > 0) List.SelectedIndex = 0;
        if (Diag.Enabled)
        {
            var tBind = sw.ElapsedMilliseconds;
            if (IsVisible) UpdateLayout(); // 行の生成・サムネイル読込をここで確定させて計測
            Diag.Log($"Refresh rows={rows.Count} search={(search.Length > 0)} db={tDb}ms rows={tRows - tDb}ms bind={tBind - tRows}ms layout={sw.ElapsedMilliseconds - tBind}ms total={sw.ElapsedMilliseconds}ms");
        }

        HistoryTab.Content = $"履歴 ({_db.Count(ItemKind.Text) + _db.Count(ItemKind.Image)})";
        SnippetTab.Content = "定型文";
    }

    private void SelectDelta(int delta)
    {
        _mouseLast = false;
        int n = List.Items.Count;
        if (n == 0) return;
        int i = Math.Clamp((List.SelectedIndex < 0 ? 0 : List.SelectedIndex) + delta, 0, n - 1);
        List.SelectedIndex = i;
        List.ScrollIntoView(List.SelectedItem);
    }

    private void Choose(bool paste)
    {
        if (List.SelectedItem is not PopupRow row) return;
        if (!IsPinned) HidePopup();
        Chosen?.Invoke(row, paste);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                if (PreviewLayer.Visibility == Visibility.Visible) PreviewLayer.Visibility = Visibility.Collapsed;
                else HidePopup();
                e.Handled = true; break;
            case Key.Space when SearchBox.Text.Length == 0 && !ctrl:
                TogglePreview(); e.Handled = true; break;
            case var k when IsQuickSelectKey(k, out int idx) && _cfg.NumberQuickSelect && !_forceSearch
                            && SearchBox.Text.Length == 0 && !ctrl && (Keyboard.Modifiers & ModifierKeys.Alt) == 0:
                if (idx < List.Items.Count) { List.SelectedIndex = idx; Choose(!shift); }
                e.Handled = true; break;
            case Key.Down: SelectDelta(1); e.Handled = true; break;
            case Key.Up: SelectDelta(-1); e.Handled = true; break;
            case Key.PageDown: SelectDelta(8); e.Handled = true; break;
            case Key.PageUp: SelectDelta(-8); e.Handled = true; break;
            case Key.Enter: Choose(!shift); e.Handled = true; break;
            case Key.Tab:
                (_snippetMode ? HistoryTab : SnippetTab).IsChecked = true;
                e.Handled = true; break;
            case Key.P when ctrl: SetPinned(!IsPinned); e.Handled = true; break;
            case Key.F when ctrl: _forceSearch = true; SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; break;
            case Key.Delete when ctrl || SearchBox.Text.Length == 0:
                if (!_snippetMode && List.SelectedItem is PopupRow { Item: { } it })
                {
                    int keep = List.SelectedIndex;
                    DeleteRequested?.Invoke(it);
                    Refresh();
                    if (List.Items.Count > 0) List.SelectedIndex = Math.Min(keep, List.Items.Count - 1);
                }
                e.Handled = true; break;
        }
    }

    private static bool IsQuickSelectKey(Key k, out int index)
    {
        index = k switch
        {
            >= Key.D1 and <= Key.D9 => k - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => k - Key.NumPad1,
            _ => -1,
        };
        return index >= 0;
    }

    // プレビュー対象: 最後に動かした入力（マウス or キーボード）を優先する
    private PopupRow? _hoverRow;
    private bool _mouseLast;

    private PopupRow? PreviewTarget() => _mouseLast && _hoverRow != null ? _hoverRow : List.SelectedItem as PopupRow;

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem) dep = VisualTreeHelper.GetParent(dep);
        if ((dep as ListBoxItem)?.Content is not PopupRow row) return;
        if (ReferenceEquals(row, _hoverRow) && _mouseLast) return;
        _hoverRow = row; _mouseLast = true;
        if (PreviewLayer.Visibility == Visibility.Visible) UpdatePreview();
    }

    private void TogglePreview()
    {
        if (PreviewLayer.Visibility == Visibility.Visible) { PreviewLayer.Visibility = Visibility.Collapsed; return; }
        if (PreviewTarget() == null) return;
        PreviewLayer.Visibility = Visibility.Visible;
        UpdatePreview();
    }

    /// <summary>選択中の項目を大きく表示する。画像は原寸から、テキストは全文（先頭2万文字まで）。</summary>
    private void UpdatePreview()
    {
        if (PreviewTarget() is not { } row) { PreviewLayer.Visibility = Visibility.Collapsed; return; }

        if (row.Item is { IsImage: true } img)
        {
            var bmp = LoadPreviewImage(img.Hash);
            PreviewImage.Source = bmp;
            PreviewImage.Visibility = bmp != null ? Visibility.Visible : Visibility.Collapsed;
            PreviewTextScroll.Visibility = bmp != null ? Visibility.Collapsed : Visibility.Visible;
            if (bmp == null) PreviewText.Text = "画像ファイルを読み込めません（保管先にアクセスできない可能性があります）。";
            PreviewInfo.Text = $"{img.Width}×{img.Height}  {ClipItem.FormatBytes(img.Bytes)}";
            return;
        }

        var text = row.Item != null ? (_db.GetItem(row.Item.Id)?.Text ?? "") : row.Snippet!.Body;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewTextScroll.Visibility = Visibility.Visible;
        PreviewTextScroll.ScrollToTop();
        PreviewText.Text = text.Length > 20000 ? text[..20000] + "\n…（以降省略）" : text;
        PreviewInfo.Text = $"{text.Length}文字";
    }

    private System.Windows.Media.Imaging.BitmapImage? LoadPreviewImage(string hash)
    {
        try
        {
            var path = _images.ImagePath(hash);
            if (!System.IO.File.Exists(path)) return null;
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.DecodePixelWidth = 1000; // 巨大画像でもメモリを抑える
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 中（要素が未生成）は何もしない
        if (SnippetTab == null || List == null || SearchBox == null || _db == null) return;
        _snippetMode = SnippetTab.IsChecked == true;
        Refresh();
        SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsVisible) return;
        Refresh();
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem) dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListBoxItem lbi)
        {
            lbi.IsSelected = true;
            Choose(true);
        }
    }

    // ---- 右クリックメニュー（履歴の行） ----
    private bool _menuOpen;

    private void List_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem) dep = VisualTreeHelper.GetParent(dep);
        if (dep is not ListBoxItem { Content: PopupRow row } lbi) return;
        lbi.IsSelected = true;
        e.Handled = true;

        var menu = new ContextMenu();
        var del = new MenuItem { Header = "削除" };
        del.Click += (_, _) =>
        {
            int keep = List.SelectedIndex;
            if (row.Item != null) DeleteRequested?.Invoke(row.Item);
            else if (row.Snippet != null) _db.DeleteSnippet(row.Snippet.Id);
            Refresh();
            if (List.Items.Count > 0) List.SelectedIndex = Math.Min(keep, List.Items.Count - 1);
        };
        if (row.Item is { } item)
        {
            var reg = new MenuItem { Header = "定型文に登録", IsEnabled = !item.IsImage };
            reg.Click += (_, _) => RegisterAsSnippet(item);
            menu.Items.Add(reg);
        }
        menu.Items.Add(del);
        // メニュー操作中は「枠外クリック」「フォーカス喪失」でポップアップを閉じない
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; SearchBox.Focus(); };
        menu.PlacementTarget = lbi;
        menu.IsOpen = true;
    }

    private void RegisterAsSnippet(ClipItem item)
    {
        var text = _db.GetItem(item.Id)?.Text ?? item.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) { Flash("空の内容は登録できません"); return; }
        if (_db.GetSnippets().Any(s => s.Body == text)) { Flash("同じ内容の定型文がすでにあります"); return; }

        var first = text.Replace("\r", "").Split('\n').First(l => l.Trim().Length > 0).Trim();
        var title = first.Length > 40 ? first[..40] : first;
        _db.SaveSnippet(new Snippet { Title = title, Body = text });
        Flash($"定型文に登録しました: {title}");
    }

    private string? _hintOriginal;
    private System.Windows.Threading.DispatcherTimer? _flashTimer;

    private void Flash(string message)
    {
        _hintOriginal ??= HintText.Text;
        HintText.Text = "✔ " + message;
        _flashTimer?.Stop();
        _flashTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _flashTimer.Tick += (_, _) => { _flashTimer!.Stop(); HintText.Text = _hintOriginal; _hintOriginal = null; };
        _flashTimer.Start();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_menuOpen) return;
        Diag.Log($"popup deactivated; fg=0x{NativeMethods.GetForegroundWindow():X} guard={Environment.TickCount64 < _showGuardUntil}");
        if (Environment.TickCount64 < _showGuardUntil)
        {
            // 表示処理の最中に奪われた場合は閉じずに前面化をやり直す
            Dispatcher.BeginInvoke(() =>
            {
                if (_hiding || !IsVisible) return;
                var h = new WindowInteropHelper(this).Handle;
                PasteService.ActivateWindow(h);
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }, System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        if (IsPinned)
        {
            PinnedDeactivated?.Invoke();
            return;
        }
        HidePopup();
    }
}
