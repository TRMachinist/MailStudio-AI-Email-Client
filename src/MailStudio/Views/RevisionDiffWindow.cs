using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>
/// Revision comparison window: displays side-by-side engineering differences
/// across PDF technical drawings, STEP 3D models, DXF CAD, and Excel schedules.
/// </summary>
public sealed class RevisionDiffWindow : Window
{
    readonly RevisionDiffReport _report;
    readonly Action<string>? _onAddToReply;
    readonly Func<string, CancellationToken, Task<string>>? _aiRunner;

    readonly ListView _diffList = new();
    readonly TextBlock _statSummary = new();
    readonly Border _aiPanel = new();
    readonly TextBlock _aiResponse = new();
    readonly Button _aiBtn = new();
    readonly Button _addReplyBtn = new();
    readonly ComboBox _categoryFilter = new();
    readonly TextBlock _statusText = new();

    public RevisionDiffWindow(
        RevisionDiffReport report,
        Action<string>? onAddToReply = null,
        Func<string, CancellationToken, Task<string>>? aiRunner = null)
    {
        _report = report;
        _onAddToReply = onAddToReply;
        _aiRunner = aiRunner;

        var part = !string.IsNullOrWhiteSpace(report.PartNumber) ? report.PartNumber : Path.GetFileNameWithoutExtension(report.NewFileName);
        Title = $"{part} — {L.T("Revizyon Karşılaştırma")}" +
                (!string.IsNullOrWhiteSpace(report.OldRevision) && !string.IsNullOrWhiteSpace(report.NewRevision)
                    ? $" ({report.OldRevision} → {report.NewRevision})"
                    : "");

        Width = 1000;
        Height = 680;
        MinWidth = 750;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");

        var mainGrid = new Grid { Margin = new Thickness(16) };
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Filter & Stats
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Diff list
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // AI Panel
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Action Bar

        // 1. Header
        var header = BuildHeader();
        Grid.SetRow(header, 0);
        mainGrid.Children.Add(header);

        // 2. Stats & Filter Bar
        var filterBar = BuildFilterBar();
        Grid.SetRow(filterBar, 1);
        mainGrid.Children.Add(filterBar);

        // 3. Diff List
        var listHost = BuildDiffList();
        Grid.SetRow(listHost, 2);
        mainGrid.Children.Add(listHost);

        // 4. AI Analysis Panel (initially collapsed)
        var aiHost = BuildAiPanel();
        Grid.SetRow(aiHost, 3);
        mainGrid.Children.Add(aiHost);

        // 5. Action Bar
        var actionBar = BuildActionBar();
        Grid.SetRow(actionBar, 4);
        mainGrid.Children.Add(actionBar);

        Content = mainGrid;
        UiTranslator.Apply(this);
        ApplyFilter();
    }

    FrameworkElement BuildHeader()
    {
        var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };

        var titleStack = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock
        {
            Text = "⚖️",
            FontSize = 20,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var title = new TextBlock
        {
            Text = L.F("Revizyon Fark Analizi: {0}", !string.IsNullOrWhiteSpace(_report.PartNumber) ? _report.PartNumber : _report.NewFileName),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeManager.Brush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var typeBadge = new Border
        {
            Background = ThemeManager.Brush("AccentSubtleBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = _report.FileType,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeManager.Brush("AccentPrimaryBrush")
            }
        };

        titleRow.Children.Add(icon);
        titleRow.Children.Add(title);
        titleRow.Children.Add(typeBadge);
        titleStack.Children.Add(titleRow);

        var subRow = new TextBlock
        {
            Text = L.F("Eski: {0} ({1})   ➔   Yeni: {2} ({3})",
                _report.OldFileName, !string.IsNullOrWhiteSpace(_report.OldRevision) ? _report.OldRevision : "-",
                _report.NewFileName, !string.IsNullOrWhiteSpace(_report.NewRevision) ? _report.NewRevision : "-"),
            FontSize = 12,
            Foreground = ThemeManager.Brush("TextSecondaryBrush"),
            Margin = new Thickness(28, 4, 0, 0)
        };
        titleStack.Children.Add(subRow);

        panel.Children.Add(titleStack);
        return panel;
    }

    FrameworkElement BuildFilterBar()
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

        _statSummary.VerticalAlignment = VerticalAlignment.Center;
        _statSummary.FontSize = 12.5;
        _statSummary.FontWeight = FontWeights.Medium;
        _statSummary.Text = L.F("Toplam {0} değişiklik: {1} Değişen, {2} Eklenen, {3} Silinen",
            _report.Items.Count, _report.ModifiedCount, _report.AddedCount, _report.RemovedCount);
        DockPanel.SetDock(_statSummary, Dock.Left);
        dock.Children.Add(_statSummary);

        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rightStack.Children.Add(new TextBlock
        {
            Text = L.T("Kategori:"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = ThemeManager.Brush("TextSecondaryBrush")
        });

        _categoryFilter.Width = 140;
        _categoryFilter.Items.Add(L.T("Tümü"));
        var categories = _report.Items.Select(i => i.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c);
        foreach (var c in categories) _categoryFilter.Items.Add(L.T(c));
        _categoryFilter.SelectedIndex = 0;
        _categoryFilter.SelectionChanged += (_, _) => ApplyFilter();
        rightStack.Children.Add(_categoryFilter);

        dock.Children.Add(rightStack);
        return dock;
    }

    FrameworkElement BuildDiffList()
    {
        var grid = new GridView();

        var colKind = new GridViewColumn
        {
            Header = L.T("Durum"),
            Width = 90,
            CellTemplate = CreateBadgeTemplate()
        };
        grid.Columns.Add(colKind);

        grid.Columns.Add(new GridViewColumn
        {
            Header = L.T("Kategori"),
            Width = 110,
            DisplayMemberBinding = new Binding("CategoryDisplay")
        });

        grid.Columns.Add(new GridViewColumn
        {
            Header = L.T("Özellik / Alan"),
            Width = 170,
            DisplayMemberBinding = new Binding("Property")
        });

        grid.Columns.Add(new GridViewColumn
        {
            Header = L.T("Eski Değer"),
            Width = 130,
            DisplayMemberBinding = new Binding("OldValue")
        });

        grid.Columns.Add(new GridViewColumn
        {
            Header = L.T("Yeni Değer"),
            Width = 130,
            DisplayMemberBinding = new Binding("NewValue")
        });

        grid.Columns.Add(new GridViewColumn
        {
            Header = L.T("Açıklama"),
            Width = 320,
            DisplayMemberBinding = new Binding("Description")
        });

        _diffList.View = grid;
        _diffList.Background = ThemeManager.Brush("BgSurfaceSecondaryBrush");
        _diffList.BorderBrush = ThemeManager.Brush("BorderSubtleBrush");
        _diffList.BorderThickness = new Thickness(1);
        _diffList.Foreground = ThemeManager.Brush("TextPrimaryBrush");

        return _diffList;
    }

    DataTemplate CreateBadgeTemplate()
    {
        var dt = new DataTemplate();
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        factory.SetValue(Border.PaddingProperty, new Thickness(5, 1, 5, 1));
        factory.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);

        var tb = new FrameworkElementFactory(typeof(TextBlock));
        tb.SetBinding(TextBlock.TextProperty, new Binding("BadgeText"));
        tb.SetValue(TextBlock.FontSizeProperty, 11.0);
        tb.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);

        factory.AppendChild(tb);
        dt.VisualTree = factory;
        return dt;
    }

    FrameworkElement BuildAiPanel()
    {
        _aiPanel.Visibility = Visibility.Collapsed;
        _aiPanel.Margin = new Thickness(0, 10, 0, 10);
        _aiPanel.Padding = new Thickness(12);
        _aiPanel.Background = ThemeManager.Brush("BgSurfaceSecondaryBrush");
        _aiPanel.BorderBrush = ThemeManager.Brush("AccentPrimaryBrush");
        _aiPanel.BorderThickness = new Thickness(1);
        _aiPanel.CornerRadius = new CornerRadius(4);

        var stack = new StackPanel();
        var hdr = new TextBlock
        {
            Text = "🤖 " + L.T("Yapay Zekâ İmalat Etkisi Analizi"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = ThemeManager.Brush("AccentPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        stack.Children.Add(hdr);

        _aiResponse.TextWrapping = TextWrapping.Wrap;
        _aiResponse.FontSize = 12;
        _aiResponse.LineHeight = 18;
        _aiResponse.Foreground = ThemeManager.Brush("TextPrimaryBrush");
        stack.Children.Add(_aiResponse);

        _aiPanel.Child = stack;
        return _aiPanel;
    }

    FrameworkElement BuildActionBar()
    {
        var dock = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };

        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.FontSize = 11.5;
        _statusText.Foreground = ThemeManager.Brush("TextMutedBrush");
        DockPanel.SetDock(_statusText, Dock.Left);
        dock.Children.Add(_statusText);

        var btnStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        _aiBtn.Content = L.T("İmalat Etkisini Sor (AI)");
        _aiBtn.Style = (Style)FindResource("FlatButton");
        _aiBtn.Padding = new Thickness(14, 6, 14, 6);
        _aiBtn.Margin = new Thickness(0, 0, 8, 0);
        _aiBtn.Click += async (_, _) => await RunAiAnalysisAsync();
        btnStack.Children.Add(_aiBtn);

        _addReplyBtn.Content = L.T("Farkı Yanıta Ekle");
        _addReplyBtn.Style = (Style)FindResource("PrimaryButton");
        _addReplyBtn.Padding = new Thickness(14, 6, 14, 6);
        _addReplyBtn.Margin = new Thickness(0, 0, 8, 0);
        _addReplyBtn.Click += OnAddToReplyClick;
        btnStack.Children.Add(_addReplyBtn);

        var closeBtn = new Button
        {
            Content = L.T("Kapat"),
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(14, 6, 14, 6),
            IsCancel = true
        };
        closeBtn.Click += (_, _) => Close();
        btnStack.Children.Add(closeBtn);

        dock.Children.Add(btnStack);
        return dock;
    }

    void ApplyFilter()
    {
        var sel = _categoryFilter.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(sel) || sel == L.T("Tümü"))
        {
            _diffList.ItemsSource = _report.Items;
        }
        else
        {
            _diffList.ItemsSource = _report.Items.Where(i => string.Equals(L.T(i.Category), sel, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    void OnAddToReplyClick(object sender, RoutedEventArgs e)
    {
        var text = _report.ToReplyText();
        Clipboard.SetText(text);

        if (_onAddToReply != null)
        {
            _onAddToReply(text);
            _statusText.Text = L.T("Fark özeti yanıta eklendi ve panoya kopyalandı.");
        }
        else
        {
            _statusText.Text = L.T("Fark özeti panoya kopyalandı. Yanıt taslağına yapıştırabilirsiniz.");
        }
    }

    async Task RunAiAnalysisAsync()
    {
        if (_aiRunner == null)
        {
            _aiPanel.Visibility = Visibility.Visible;
            _aiResponse.Text = L.T("Yapay zekâ sağlayıcısı yapılandırılmamış. Ayarlar > Yapay Zekâ menüsünden bir model seçebilirsiniz.");
            return;
        }

        _aiBtn.IsEnabled = false;
        _statusText.Text = L.T("Yapay zekâ imalat etkisini analiz ediyor...");
        _aiPanel.Visibility = Visibility.Visible;
        _aiResponse.Text = L.T("Analiz hazırlanıyor, lütfen bekleyin...");

        try
        {
            var prompt = _report.ToAiPrompt();
            var response = await _aiRunner(prompt, CancellationToken.None);
            _aiResponse.Text = !string.IsNullOrWhiteSpace(response) ? response.Trim() : L.T("Analiz sonucu alınamadı.");
            _statusText.Text = L.T("Yapay zekâ analizi tamamlandı.");
        }
        catch (Exception ex)
        {
            _aiResponse.Text = L.T("Hata oluştu: ") + ex.Message;
            _statusText.Text = L.T("Analiz başarısız.");
        }
        finally
        {
            _aiBtn.IsEnabled = true;
        }
    }
}
