using System.Windows;
using System.Windows.Controls;
using ClipMaster.Data;
using ClipMaster.Services;

namespace ClipMaster.UI;

/// <summary>定型文の登録・編集・削除。</summary>
public sealed class SnippetEditorWindow : Window
{
    private readonly Database _db;
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(Snippet.Display) };
    private readonly TextBox _category = new(), _title = new(), _body = new()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 160
    };
    private Snippet _current = new();

    public SnippetEditorWindow(Database db)
    {
        _db = db;
        Title = "定型文の編集";
        Width = 760; Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new System.Windows.Media.FontFamily("Yu Gothic UI, Segoe UI");

        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel();
        var newBtn = new Button { Content = "新規", Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(8, 3, 8, 3) };
        newBtn.Click += (_, _) => { _list.SelectedItem = null; Load(new Snippet()); _title.Focus(); };
        DockPanel.SetDock(newBtn, Dock.Top);
        left.Children.Add(newBtn);
        var ioPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var impBtn = new Button { Content = "インポート", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
        var expBtn = new Button { Content = "エクスポート", Padding = new Thickness(8, 3, 8, 3) };
        impBtn.Click += (_, _) => Import();
        expBtn.Click += (_, _) => Export();
        ioPanel.Children.Add(impBtn);
        ioPanel.Children.Add(expBtn);
        DockPanel.SetDock(ioPanel, Dock.Bottom);
        left.Children.Add(ioPanel);
        left.Children.Add(_list);
        root.Children.Add(left);

        var right = new Grid { Margin = new Thickness(12, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.Children.Add(Labeled("カテゴリ（例: 業務連絡）", _category, 0));
        right.Children.Add(Labeled("タイトル", _title, 1));
        var hint = new TextBlock
        {
            Text = "本文（{DATE} 日付 / {TIME} 時刻 が貼り付け時に展開されます）",
            Margin = new Thickness(0, 8, 0, 2)
        };
        Grid.SetRow(hint, 2);
        right.Children.Add(hint);
        Grid.SetRow(_body, 3);
        right.Children.Add(_body);

        var save = new Button { Content = "保存", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var del = new Button { Content = "削除", Width = 80 };
        save.Click += (_, _) => Save();
        del.Click += (_, _) => Delete();
        var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bp.Children.Add(save);
        bp.Children.Add(del);
        Grid.SetRow(bp, 4);
        right.Children.Add(bp);

        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;

        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is Snippet s) Load(s); };
        Reload();
    }

    private static FrameworkElement Labeled(string label, TextBox box, int row)
    {
        var p = new StackPanel { Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 0) };
        p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) });
        p.Children.Add(box);
        Grid.SetRow(p, row);
        return p;
    }

    private void Reload()
    {
        _list.ItemsSource = _db.GetSnippets();
    }

    private void Load(Snippet s)
    {
        _current = s;
        _category.Text = s.Category;
        _title.Text = s.Title;
        _body.Text = s.Body;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_title.Text))
        {
            MessageBox.Show(this, "タイトルを入力してください。", "ClipMaster", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _current.Category = _category.Text.Trim();
        _current.Title = _title.Text.Trim();
        _current.Body = _body.Text;
        var id = _db.SaveSnippet(_current);
        Reload();
        _list.SelectedItem = ((IEnumerable<Snippet>)_list.ItemsSource).FirstOrDefault(x => x.Id == id);
    }

    private const string FileFilter = "CSV (*.csv)|*.csv|テキスト (*.txt)|*.txt";

    private void Export()
    {
        var items = _db.GetSnippets();
        if (items.Count == 0)
        {
            MessageBox.Show(this, "エクスポートする定型文がありません。", "ClipMaster", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = FileFilter, FileName = "snippets", DefaultExt = ".csv" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            SnippetIO.Export(dlg.FileName, items);
            MessageBox.Show(this, $"{items.Count} 件をエクスポートしました。", "ClipMaster", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "エクスポートに失敗しました。\n" + ex.Message, "ClipMaster", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = FileFilter };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var existing = _db.GetSnippets()
                .Select(s => (s.Category, s.Title, s.Body)).ToHashSet();
            int added = 0, skipped = 0;
            foreach (var s in SnippetIO.Import(dlg.FileName))
            {
                if (!existing.Add((s.Category, s.Title, s.Body))) { skipped++; continue; }
                _db.SaveSnippet(s);
                added++;
            }
            Reload();
            MessageBox.Show(this, $"{added} 件を追加しました（重複 {skipped} 件はスキップ）。", "ClipMaster",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "インポートに失敗しました。\n" + ex.Message, "ClipMaster", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Delete()
    {
        if (_current.Id == 0) return;
        if (MessageBox.Show(this, $"「{_current.Title}」を削除しますか？", "ClipMaster",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _db.DeleteSnippet(_current.Id);
        Load(new Snippet());
        Reload();
    }
}
