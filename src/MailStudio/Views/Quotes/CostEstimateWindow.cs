using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Services.Costing;
using MailStudio.Views.Calendar;

namespace MailStudio.Views.Quotes;

/// <summary>
/// Rough machining cost from a STEP model, recalculated live while material, quantity, hour rate, margin and allowance change.
/// "Apply to quote" writes the unit price and the breakdown into the quote (when opened for a quote).
/// </summary>
public sealed class CostEstimateWindow : Window
{
    readonly PartGeometry _geometry;
    readonly CostSettings _settings;
    readonly QuoteItem? _quote;
    readonly double? _stockVolume;
    readonly double[]? _stockSize;
    readonly ComboBox _material = new() { Height = 28, MinWidth = 260 };
    readonly TextBox _qty = Box(80), _rate = Box(80), _margin = Box(60), _allowance = Box(60);
    readonly ComboBox _currency = new() { Height = 28, Width = 80, ItemsSource = new[] { "EUR", "USD", "TRY", "GBP" } };
    readonly ListView _lines = new() { BorderThickness = new Thickness(0), MinHeight = 170 };
    readonly TextBlock _unit = PimUi.Text("", 22, bold: true, brush: "AccentPrimaryBrush");
    readonly TextBlock _total = PimUi.Text("", 13, bold: true);
    readonly TextBlock _stock = PimUi.Text("", 12, brush: "TextSecondaryBrush");
    readonly TextBlock _assumptions = PimUi.Text("", 11.5, brush: "TextMutedBrush");
    readonly TextBlock _error = PimUi.Text("", 12, brush: "StatusDangerBrush");
    CostEstimate? _estimate;

    /// <summary>The estimate the user accepted (null when closed without applying).</summary>
    public CostEstimate? Applied { get; private set; }

    static TextBox Box(double w) => new() { Height = 28, Width = w, HorizontalContentAlignment = HorizontalAlignment.Right };

    public CostEstimateWindow(string title, PartGeometry geometry, QuoteItem? quote = null, double? stockVolumeMm3 = null, double[]? stockSize = null)
    {
        _geometry = geometry; _quote = quote; _stockVolume = stockVolumeMm3; _stockSize = stockSize;
        _settings = CostEstimator.LoadSettings();
        UiTranslator.SetSkip(this, true);
        Title = L.T("Maliyet tahmini") + " – " + title;
        Width = 760; Height = 680; MinWidth = 640; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        var materials = CostEstimator.Materials(_settings);
        _material.ItemsSource = materials;
        _material.SelectedItem = CostEstimator.Match(materials, quote?.Material)
                                 ?? materials.FirstOrDefault(m => m.Name == AppSettings.Shared.LastMaterial) ?? materials[0];
        _qty.Text = (quote?.Quantity is > 0 ? quote.Quantity : 1).ToString(CultureInfo.CurrentCulture);
        _rate.Text = _settings.HourlyRate.ToString("0.##", CultureInfo.CurrentCulture);
        _margin.Text = _settings.MarginPercent.ToString("0.##", CultureInfo.CurrentCulture);
        _allowance.Text = _settings.StockAllowanceMm.ToString("0.##", CultureInfo.CurrentCulture);
        _currency.SelectedItem = quote?.Currency ?? _settings.Currency;
        if (_stockVolume != null) { _allowance.IsEnabled = false; _allowance.ToolTip = L.T("STEP görüntüleyicide oluşturulan stok kullanılıyor."); }

        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };

        // header: what was read from the model
        var head = new StackPanel();
        head.Children.Add(PimUi.Text(title, 15, bold: true));
        head.Children.Add(PimUi.Text(L.F("Model: {0:0.##} × {1:0.##} × {2:0.##} mm  ·  hacim {3:0.###} cm³  ·  yüzey {4:0.#} cm²  ·  {5} delik",
            geometry.SizeX, geometry.SizeY, geometry.SizeZ, geometry.VolumeMm3 / 1000, geometry.SurfaceMm2 / 100, geometry.HoleCount), 12, brush: "TextSecondaryBrush"));
        head.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);

        // inputs
        var inputs = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        void Field(string label, FrameworkElement c)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 6) };
            sp.Children.Add(PimUi.Text(label, 11.5, brush: "TextSecondaryBrush"));
            sp.Children.Add(c);
            inputs.Children.Add(sp);
        }
        Field(L.T("Malzeme"), _material);
        Field(L.T("Adet"), _qty);
        Field(L.T("Saat ücreti"), _rate);
        Field(L.T("Para birimi"), _currency);
        Field(L.T("Kâr %"), _margin);
        Field(L.T("Stok payı (mm)"), _allowance);
        DockPanel.SetDock(inputs, Dock.Top); root.Children.Add(inputs);

        // footer
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Button Btn(string text, string style, RoutedEventHandler click)
        {
            var b = new Button { Content = text, MinWidth = 96, Height = 30, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0) };
            b.SetResourceReference(StyleProperty, style); b.Click += click; buttons.Children.Add(b); return b;
        }
        Btn(L.T("Ayarlar…"), "RibbonCmd", (_, _) =>
        {
            var w = new CostSettingsWindow { Owner = this };
            if (w.ShowDialog() == true) { var fresh = CostEstimator.LoadSettings(); CopySettings(fresh); Recalc(); }
        });
        Btn(L.T("Panoya kopyala"), "RibbonCmd", (_, _) => { if (_estimate != null) try { Clipboard.SetText(_estimate.ToText()); } catch { } });
        if (quote != null)
        {
            var apply = Btn(L.T("Teklife uygula"), "RibbonPrimaryCmd", (_, _) => { if (_estimate != null) { Applied = _estimate; DialogResult = true; } });
            apply.IsDefault = true;
        }
        Btn(L.T("Kapat"), "RibbonCmd", (_, _) => Close()).IsCancel = true;
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        // result
        var result = new StackPanel();
        result.Children.Add(_unit);
        result.Children.Add(_total);
        result.Children.Add(_stock);
        result.Children.Add(_error);
        result.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(result, Dock.Top); root.Children.Add(result);
        DockPanel.SetDock(_assumptions, Dock.Bottom); _assumptions.TextWrapping = TextWrapping.Wrap; _assumptions.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_assumptions);

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = L.T("Maliyet kalemi"), DisplayMemberBinding = new Binding(nameof(CostLine.Label)), Width = 230 });
        view.Columns.Add(new GridViewColumn { Header = L.T("Hesaplama"), DisplayMemberBinding = new Binding(nameof(CostLine.Detail)), Width = 320 });
        view.Columns.Add(new GridViewColumn { Header = L.T("Tutar"), DisplayMemberBinding = new Binding("AmountText"), Width = 110 });
        _lines.View = view;
        root.Children.Add(_lines);

        Content = root;
        foreach (var tb in new[] { _qty, _rate, _margin, _allowance }) tb.TextChanged += (_, _) => Recalc();
        _material.SelectionChanged += (_, _) => Recalc();
        _currency.SelectionChanged += (_, _) => Recalc();
        Recalc();
    }

    void CopySettings(CostSettings s)
    {
        _settings.MinutesPerHole = s.MinutesPerHole; _settings.SetupMinutes = s.SetupMinutes; _settings.HandlingMinutes = s.HandlingMinutes;
        _settings.MaterialWastePercent = s.MaterialWastePercent; _settings.Materials = s.Materials; _settings.Company = s.Company;
        var sel = (_material.SelectedItem as MaterialSpec)?.Name;
        var list = CostEstimator.Materials(_settings);
        _material.ItemsSource = list;
        _material.SelectedItem = list.FirstOrDefault(m => m.Name == sel) ?? list[0];
    }

    static bool Num(TextBox t, out double v) => double.TryParse(t.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out v) && v >= 0;

    void Recalc()
    {
        if (!IsInitialized && _lines.View == null) return;
        _error.Text = "";
        if (_material.SelectedItem is not MaterialSpec m) return;
        if (!Num(_qty, out var q) || q < 1 || !Num(_rate, out var rate) || !Num(_margin, out var margin) || !Num(_allowance, out var allowance))
        { _error.Text = L.T("Adet, saat ücreti, kâr ve stok payı sayı olmalı."); return; }
        _settings.HourlyRate = rate; _settings.MarginPercent = margin; _settings.StockAllowanceMm = allowance;
        _settings.Currency = _currency.SelectedItem as string ?? _settings.Currency;
        try
        {
            _estimate = CostEstimator.Estimate(_geometry, m, (int)q, _settings, _stockVolume, _stockSize);
            _unit.Text = L.F("Birim fiyat: {0}", _estimate.Money(_estimate.UnitPrice));
            _total.Text = L.F("{0} adet toplam: {1}   ·   birim maliyet {2}   ·   çevrim ≈ {3:0.#} dk/adet",
                _estimate.Quantity, _estimate.Money(_estimate.TotalPrice), _estimate.Money(_estimate.UnitCost), _estimate.CycleMinutes);
            _stock.Text = (_estimate.StockX > 0 ? L.F("Stok {0:0.#} × {1:0.#} × {2:0.#} mm  ·  ", _estimate.StockX, _estimate.StockY, _estimate.StockZ) : "")
                + L.F("stok {0:0.###} kg  ·  parça {1:0.###} kg  ·  talaş {2:0.#} cm³", _estimate.StockMassKg, _estimate.PartMassKg, _estimate.RemovedCm3);
            var est = _estimate;
            _lines.ItemsSource = est.Lines.Select(l => new { l.Label, l.Detail, AmountText = est.Money(l.Amount) }).ToList();
            _assumptions.Text = string.Join("\n", est.Assumptions);
        }
        catch (Exception ex) { _estimate = null; _error.Text = ex.Message; _lines.ItemsSource = null; }
    }
}
