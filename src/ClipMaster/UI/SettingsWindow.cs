using System.Windows;
using System.Windows.Controls;
using ClipMaster.Services;

namespace ClipMaster.UI;

/// <summary>設定画面。OK で Result に編集後の設定を返す。</summary>
public sealed class SettingsWindow : Window
{
    private readonly TextBox _maxText = new(), _maxImage = new(), _maxChars = new(), _root = new(), _hotkey = new(), _interval = new();
    private readonly CheckBox _quick = new() { Content = "検索欄が空のとき 1〜9 キーで即貼り付け" };
    private readonly CheckBox _dbl = new() { Content = "Ctrl 2回押しで呼び出す" };
    private readonly CheckBox _hk = new() { Content = "ホットキーで呼び出す" };
    private readonly CheckBox _caret = new() { Content = "入力キャレット付近に表示（無ければマウス位置）" };
    private readonly CheckBox _startup = new() { Content = "Windows 起動時に自動起動" };

    public AppConfig? Result { get; private set; }
    private readonly AppConfig _orig;

    public SettingsWindow(AppConfig cfg)
    {
        _orig = cfg;
        Title = "ClipMaster 設定";
        Width = 520; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new System.Windows.Media.FontFamily("Yu Gothic UI, Segoe UI");

        _maxText.Text = cfg.MaxTextItems.ToString();
        _maxImage.Text = cfg.MaxImageItems.ToString();
        _maxChars.Text = cfg.MaxTextChars.ToString();
        _quick.IsChecked = cfg.NumberQuickSelect;
        _root.Text = cfg.StorageRoot;
        _hotkey.Text = cfg.Hotkey;
        _interval.Text = cfg.DoubleCtrlIntervalMs.ToString();
        _dbl.IsChecked = cfg.DoubleCtrlEnabled;
        _hk.IsChecked = cfg.HotkeyEnabled;
        _caret.IsChecked = cfg.PopupAtCaret;
        _startup.IsChecked = cfg.RunAtStartup;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Header("履歴の上限件数"));
        panel.Children.Add(Row("テキスト", _maxText));
        panel.Children.Add(Row("画像", _maxImage));
        panel.Children.Add(Row("テキスト最大文字数（超過分は保存しない）", _maxChars));

        panel.Children.Add(Header("保管先"));
        var browse = new Button { Content = "参照...", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _root.Text, Description = "保管先フォルダを選択" };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) _root.Text = dlg.SelectedPath;
        };
        var rootRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(browse, Dock.Right);
        rootRow.Children.Add(browse);
        rootRow.Children.Add(_root);
        panel.Children.Add(rootRow);

        panel.Children.Add(Header("呼び出し"));
        panel.Children.Add(Pad(_dbl));
        panel.Children.Add(Row("Ctrl 2回押しの間隔(ms)", _interval));
        panel.Children.Add(Pad(_hk));
        panel.Children.Add(Row("ホットキー (例: Ctrl+Shift+V)", _hotkey));
        panel.Children.Add(Pad(_caret));
        panel.Children.Add(Pad(_quick));
        panel.Children.Add(Header("その他"));
        panel.Children.Add(Pad(_startup));

        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        ok.Click += (_, _) => Commit();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private static TextBlock Header(string t) => new()
    {
        Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4)
    };

    private static FrameworkElement Pad(FrameworkElement e) { e.Margin = new Thickness(0, 3, 0, 3); return e; }

    private static FrameworkElement Row(string label, TextBox box)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(box, 1);
        g.Children.Add(box);
        return g;
    }

    private void Commit()
    {
        if (!int.TryParse(_maxText.Text, out var mt) || mt < 1 ||
            !int.TryParse(_maxImage.Text, out var mi) || mi < 1 ||
            !int.TryParse(_interval.Text, out var iv) ||
            !int.TryParse(_maxChars.Text, out var mc) || mc < 1000)
        {
            MessageBox.Show(this, "件数・間隔には 1 以上の整数（最大文字数は 1000 以上）を入力してください。", "ClipMaster", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_hk.IsChecked == true && !HotkeyService.TryParse(_hotkey.Text, out _, out _))
        {
            MessageBox.Show(this, "ホットキーの形式が正しくありません。例: Ctrl+Shift+V / Alt+V", "ClipMaster", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_root.Text))
        {
            MessageBox.Show(this, "保管先を指定してください。", "ClipMaster", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = new AppConfig
        {
            StorageRoot = _root.Text.Trim(),
            MaxTextItems = mt,
            MaxImageItems = mi,
            MaxTextChars = mc,
            NumberQuickSelect = _quick.IsChecked == true,
            DoubleCtrlEnabled = _dbl.IsChecked == true,
            DoubleCtrlIntervalMs = iv,
            HotkeyEnabled = _hk.IsChecked == true,
            Hotkey = _hotkey.Text.Trim(),
            PopupAtCaret = _caret.IsChecked == true,
            RunAtStartup = _startup.IsChecked == true,
            DateFormat = _orig.DateFormat,
            TimeFormat = _orig.TimeFormat,
        };
        DialogResult = true;
    }
}
