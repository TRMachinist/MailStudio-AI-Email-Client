using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MailStudio.Services;
using MailStudio.Services.Skills;

namespace MailStudio.Views;

/// <summary>Preview of an AI Excel edit plan. Nothing is written until the user saves a NEW file.</summary>
public sealed class ExcelPlanWindow : Window
{
    public string? SavedPath { get; private set; }

    public ExcelPlanWindow(string sourcePath, ExcelEditPlan plan)
    {
        Title = L.T("Excel değişiklikleri – önizleme"); Width = 860; Height = 560; MinWidth = 640; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");
        var changes = ExcelEditApplier.Preview(sourcePath, plan, out var warnings);

        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        head.Children.Add(new TextBlock { Text = Path.GetFileName(sourcePath), FontSize = 16, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock { Text = plan.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var numbers = changes.Count(c => c.NumberChanged);
        head.Children.Add(new TextBlock
        {
            Text = L.F("{0} değişiklik", changes.Count) + (numbers > 0 ? " · " + L.F("{0} hücrede sayı/tarih değişiyor – lütfen tek tek kontrol edin.", numbers) : "")
                   + (warnings.Count > 0 ? "\n" + string.Join("\n", warnings) : ""),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8), Foreground = numbers > 0 || warnings.Count > 0 ? Brushes.DarkOrange : ThemeManager.Brush("TextSecondaryBrush")
        });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        bar.Children.Add(new TextBlock { Text = L.T("Ek dosyası değişmez; yeni bir kopya kaydedilir."), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Foreground = ThemeManager.Brush("TextSecondaryBrush") });
        var save = new Button { Content = L.T("Yeni dosya olarak kaydet..."), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true, IsEnabled = changes.Count > 0 };
        var cancel = new Button { Content = L.T("Vazgeç"), Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        bar.Children.Add(save); bar.Children.Add(cancel);

        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, ItemsSource = changes, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("Çalışma sayfası"), Binding = new System.Windows.Data.Binding("Sheet") });
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("Hücre"), Binding = new System.Windows.Data.Binding("Cell") });
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("İşlem"), Binding = new System.Windows.Data.Binding("Operation") });
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("Önce"), Binding = new System.Windows.Data.Binding("Before"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("Sonra"), Binding = new System.Windows.Data.Binding("After"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.LoadingRow += (_, e) => { if (e.Row.Item is ExcelChange { NumberChanged: true }) e.Row.Background = new SolidColorBrush(Color.FromArgb(60, 255, 165, 0)); };
        root.Children.Add(grid);

        save.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileNameWithoutExtension(sourcePath) + L.T(" (güncel)") + Path.GetExtension(sourcePath),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Filter = "Excel|*.xlsx;*.xlsm",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                ExcelEditApplier.Apply(sourcePath, plan, dlg.FileName, out _);
                SavedPath = dlg.FileName; DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
    }
}
