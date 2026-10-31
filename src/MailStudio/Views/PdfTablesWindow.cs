using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MailStudio.Services;
using MailStudio.Views.Calendar;
using Microsoft.Win32;

namespace MailStudio.Views;

/// <summary>Tables found in a PDF (parts lists, schedules, price lists): preview, pick, export to Excel.</summary>
public sealed class PdfTablesWindow : Window
{
    readonly string _pdf;
    readonly Func<string, object, CancellationToken, Task<JsonElement>> _call;
    readonly CancellationTokenSource _life = new();
    readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    readonly DataGrid _grid = new() { IsReadOnly = true, AutoGenerateColumns = true, HeadersVisibility = DataGridHeadersVisibility.All, CanUserSortColumns = false };
    readonly TextBlock _status = PimUi.Text("", 12, brush: "TextSecondaryBrush");
    readonly Button _export = new() { Height = 30, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 8, 0) };

    public List<PdfTable> Tables { get; private set; } = new();
    public Task Ready => _ready.Task;
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PdfTablesWindow(string pdf, Func<string, object, CancellationToken, Task<JsonElement>> call)
    {
        _pdf = pdf; _call = call;
        UiTranslator.SetSkip(this, true);
        Title = L.T("PDF tablosu → Excel") + " – " + System.IO.Path.GetFileName(pdf);
        Width = 1100; Height = 700; MinWidth = 760; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        var left = new DockPanel { Width = 270, Margin = new Thickness(12, 12, 6, 0) };
        var head = PimUi.Text(L.T("Bulunan tablolar"), 13, bold: true); head.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(head, Dock.Top); left.Children.Add(head);
        var hint = PimUi.Text(L.T("İşaretli tablolar Excel'de ayrı sayfalara yazılır."), 11.5, brush: "TextMutedBrush");
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(hint, Dock.Bottom); left.Children.Add(hint);
        left.Children.Add(_list);
        _list.SelectionChanged += (_, _) => Preview();

        var right = new Border { Child = _grid, Margin = new Thickness(6, 12, 12, 0), BorderThickness = new Thickness(1) };
        right.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(left); Grid.SetColumn(right, 1); body.Children.Add(right);

        var foot = new DockPanel { Margin = new Thickness(12, 10, 12, 12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _export.Content = L.T("Excel'e aktar…"); _export.IsEnabled = false;
        _export.SetResourceReference(StyleProperty, "RibbonPrimaryCmd"); _export.Click += (_, _) => Export(null);
        var close = new Button { Content = L.T("Kapat"), MinWidth = 90, Height = 30, IsCancel = true };
        close.SetResourceReference(StyleProperty, "RibbonCmd"); close.Click += (_, _) => Close();
        buttons.Children.Add(_export); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right); foot.Children.Add(buttons);
        _status.VerticalAlignment = VerticalAlignment.Center; _status.TextWrapping = TextWrapping.Wrap; foot.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(foot, Dock.Bottom); root.Children.Add(foot);
        root.Children.Add(body);
        Content = root;
        Closed += (_, _) => _life.Cancel();
        Loaded += async (_, _) => await FindAsync();
    }

    async Task FindAsync()
    {
        _status.Text = L.T("Sayfalardaki tablolar aranıyor… (taranmış sayfalarda OCR birkaç saniye sürebilir)");
        try
        {
            var r = await _call("pdf_tables", new { path = _pdf }, _life.Token);
            Tables = PdfTable.FromWorker(r);
            foreach (var t in Tables)
            {
                var cb = new CheckBox { IsChecked = true, Content = t.Title + (t.Review ? "  · OCR" : ""), Tag = t, Margin = new Thickness(2, 3, 2, 3) };
                _list.Items.Add(cb);
            }
            if (Tables.Count > 0) _list.SelectedIndex = 0;
            _export.IsEnabled = Tables.Count > 0;
            _status.Text = Tables.Count == 0 ? L.T("Bu PDF'te tablo bulunamadı (en az üç satır ve iki sütun aranır).")
                : L.F("{0} tablo bulundu.", Tables.Count) + (Tables.Any(t => t.Review) ? " " + L.T("OCR ile okunan tablolar tahmindir; sayıları PDF'te doğrulayın.") : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = L.T("Tablolar okunamadı: ") + ex.Message; }
        finally { _ready.TrySetResult(); }
    }

    void Preview()
    {
        if (_list.SelectedItem is not CheckBox { Tag: PdfTable t }) return;
        var dt = new DataTable();
        var header = t.HasHeader;
        var names = new HashSet<string>();
        for (var c = 0; c < t.Columns; c++)
        {
            var name = header && c < t.Rows[0].Count && t.Rows[0][c].Trim().Length > 0 ? t.Rows[0][c].Trim() : L.F("Sütun {0}", c + 1);
            while (!names.Add(name)) name += " ";
            dt.Columns.Add(name.Replace(".", "·").Replace("/", "∕").Replace("[", "(").Replace("]", ")"));
        }
        foreach (var row in t.Rows.Skip(header ? 1 : 0))
            dt.Rows.Add(Enumerable.Range(0, t.Columns).Select(c => (object)(c < row.Count ? row[c] : "")).ToArray());
        _grid.ItemsSource = dt.DefaultView;
    }

    /// <summary>Writes the checked tables (dest null = ask). Returns the path or null.</summary>
    public string? Export(string? dest)
    {
        var chosen = _list.Items.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (PdfTable)c.Tag).ToList();
        if (chosen.Count == 0) { _status.Text = L.T("Önce en az bir tablo işaretleyin."); return null; }
        var ask = dest == null;
        if (ask)
        {
            var d = new SaveFileDialog { Filter = "Excel|*.xlsx", FileName = System.IO.Path.GetFileNameWithoutExtension(_pdf) + " " + L.T("tablolar") + ".xlsx" };
            if (d.ShowDialog(this) != true) return null;
            dest = d.FileName;
        }
        try
        {
            PdfTableExport.ToExcel(dest!, chosen, System.IO.Path.GetFileName(_pdf));
            _status.Text = L.F("{0} tablo Excel'e yazıldı: {1}", chosen.Count, dest);
            if (ask && MessageBox.Show(this, L.T("Excel dosyası açılsın mı?"), L.ProductName, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                try { Process.Start(new ProcessStartInfo(dest!) { UseShellExecute = true }); } catch { }
            return dest;
        }
        catch (Exception ex) { _status.Text = L.T("Excel yazılamadı: ") + ex.Message; return null; }
    }
}
