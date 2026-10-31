using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MailStudio.Services;
using MailStudio.Services.Costing;
using MailStudio.Views.Calendar;
using Microsoft.Win32;

namespace MailStudio.Views.Quotes;

/// <summary>Company details for the quotation PDF, calculation parameters and per-material prices / removal rates.</summary>
public sealed class CostSettingsWindow : Window
{
    readonly CostSettings _s = CostEstimator.LoadSettings();
    readonly List<MaterialRow> _rows;

    public sealed class MaterialRow
    {
        public string Group { get; init; } = "";
        public string Name { get; init; } = "";
        public string Display => L.T(Name);
        public double Density { get; init; }
        public double Price { get; set; }
        public double Rough { get; set; }
        public double Finish { get; set; }
    }

    public CostSettingsWindow()
    {
        UiTranslator.SetSkip(this, true);
        Title = L.T("Fiyat ve teklif ayarları");
        Width = 780; Height = 640; MinWidth = 640; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        var tabs = new TabControl { Margin = new Thickness(12, 12, 12, 0) };

        // --- company
        var c = _s.Company;
        var company = Form(out var addCo);
        var name = addCo(L.T("Firma adı"), c.Name, false);
        var address = addCo(L.T("Adres"), c.Address, true);
        var phone = addCo(L.T("Telefon"), c.Phone, false);
        var email = addCo(L.T("E-posta"), c.Email, false);
        var web = addCo(L.T("Web"), c.Web, false);
        var tax = addCo(L.T("Vergi No"), c.TaxId, false);
        var sign = addCo(L.T("İmza / kapanış"), c.Signature, true);
        var logo = new TextBlock { Text = string.IsNullOrEmpty(c.LogoPath) ? L.T("(logo yok)") : c.LogoPath, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var logoBtn = new Button { Content = L.T("Logo seç…"), Height = 28, Padding = new Thickness(10, 0, 10, 0) };
        logoBtn.SetResourceReference(StyleProperty, "RibbonCmd");
        var logoPath = c.LogoPath;
        logoBtn.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "PNG / JPG|*.png;*.jpg;*.jpeg" };
            if (d.ShowDialog(this) == true) { logoPath = d.FileName; logo.Text = d.FileName; }
        };
        var logoPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        logoPanel.Children.Add(logoBtn); logoPanel.Children.Add(logo);
        ((StackPanel)((ScrollViewer)company).Content).Children.Add(logoPanel);
        tabs.Items.Add(new TabItem { Header = L.T("Firma"), Content = company });

        // --- calculation
        var calc = Form(out var addCalc);
        string N(double v) => v.ToString("0.##", CultureInfo.CurrentCulture);
        var currency = new ComboBox { Height = 28, Width = 100, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "EUR", "USD", "TRY", "GBP" }, SelectedItem = _s.Currency };
        ((StackPanel)((ScrollViewer)calc).Content).Children.Add(PimUi.Text(L.T("Para birimi"), 11.5, brush: "TextSecondaryBrush"));
        ((StackPanel)((ScrollViewer)calc).Content).Children.Add(currency);
        var rate = addCalc(L.T("Saat ücreti (makine + operatör)"), N(_s.HourlyRate), false);
        var setup = addCalc(L.T("Hazırlık süresi / sipariş (dk)"), N(_s.SetupMinutes), false);
        var handling = addCalc(L.T("Bağlama, çapak, kontrol / parça (dk)"), N(_s.HandlingMinutes), false);
        var hole = addCalc(L.T("Delik başına süre (dk)"), N(_s.MinutesPerHole), false);
        var allowance = addCalc(L.T("Stok payı her yüzde (mm)"), N(_s.StockAllowanceMm), false);
        var waste = addCalc(L.T("Hammadde firesi (%)"), N(_s.MaterialWastePercent), false);
        var margin = addCalc(L.T("Kâr payı (%)"), N(_s.MarginPercent), false);
        var validity = addCalc(L.T("Teklif geçerliliği (gün)"), _s.QuoteValidityDays.ToString(CultureInfo.CurrentCulture), false);
        var payment = addCalc(L.T("Ödeme şartları"), _s.PaymentTerms, true);
        var delivery = addCalc(L.T("Teslim şartları"), _s.DeliveryTerms, true);
        tabs.Items.Add(new TabItem { Header = L.T("Hesap"), Content = calc });

        // --- materials
        _rows = CostEstimator.Materials(_s).Select(m => new MaterialRow { Group = m.Group, Name = m.Name, Density = m.DensityGcm3, Price = m.PricePerKg, Rough = m.RoughCm3PerMin, Finish = m.FinishCm2PerMin }).ToList();
        var grid = new DataGrid { ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(8) };
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("Grup"), Binding = new Binding(nameof(MaterialRow.Group)), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("Malzeme"), Binding = new Binding(nameof(MaterialRow.Display)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "g/cm³", Binding = new Binding(nameof(MaterialRow.Density)) { StringFormat = "0.00" }, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("Fiyat / kg"), Binding = new Binding(nameof(MaterialRow.Price)) });
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("Kaba cm³/dk"), Binding = new Binding(nameof(MaterialRow.Rough)) });
        grid.Columns.Add(new DataGridTextColumn { Header = L.T("Finiş cm²/dk"), Binding = new Binding(nameof(MaterialRow.Finish)) });
        var matPanel = new DockPanel();
        var hint = PimUi.Text(L.T("Fiyatlar ve talaş debileri örnek değerlerdir; kendi tedarikçi fiyatlarınızı ve makine değerlerinizi girin."), 11.5, brush: "TextMutedBrush");
        hint.Margin = new Thickness(8, 8, 8, 0); hint.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(hint, Dock.Top); matPanel.Children.Add(hint); matPanel.Children.Add(grid);
        tabs.Items.Add(new TabItem { Header = L.T("Malzemeler"), Content = matPanel });

        // --- buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        var save = new Button { Content = L.T("Kaydet"), MinWidth = 90, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        save.SetResourceReference(StyleProperty, "RibbonPrimaryCmd");
        var cancel = new Button { Content = L.T("İptal"), MinWidth = 80, Height = 30, IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "RibbonCmd");
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        save.Click += (_, _) =>
        {
            static bool D(TextBox t, out double v) => double.TryParse(t.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out v) && v >= 0;
            if (!D(rate, out var r) || !D(setup, out var su) || !D(handling, out var h) || !D(hole, out var ho) || !D(allowance, out var a)
                || !D(waste, out var w) || !D(margin, out var mg) || !int.TryParse(validity.Text.Trim(), out var vd) || vd < 0)
            { MessageBox.Show(this, L.T("Hesap sekmesindeki değerler sayı olmalı."), L.ProductName); tabs.SelectedIndex = 1; return; }
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            _s.Company = new CompanyInfo { Name = name.Text.Trim(), Address = address.Text.Trim(), Phone = phone.Text.Trim(), Email = email.Text.Trim(), Web = web.Text.Trim(), TaxId = tax.Text.Trim(), Signature = sign.Text.Trim(), LogoPath = logoPath };
            _s.Currency = currency.SelectedItem as string ?? "EUR";
            _s.HourlyRate = r; _s.SetupMinutes = su; _s.HandlingMinutes = h; _s.MinutesPerHole = ho; _s.StockAllowanceMm = a; _s.MaterialWastePercent = w; _s.MarginPercent = mg;
            _s.QuoteValidityDays = vd; _s.PaymentTerms = payment.Text.Trim(); _s.DeliveryTerms = delivery.Text.Trim();
            _s.Materials = new();
            foreach (var row in _rows)
            {
                var def = CostEstimator.DefaultMaterials.First(m => m.Name == row.Name);
                if (row.Price != def.PricePerKg || row.Rough != def.RoughCm3PerMin || row.Finish != def.FinishCm2PerMin)
                    _s.Materials[row.Name] = new MaterialOverride
                    {
                        PricePerKg = row.Price != def.PricePerKg ? row.Price : null,
                        RoughCm3PerMin = row.Rough != def.RoughCm3PerMin ? row.Rough : null,
                        FinishCm2PerMin = row.Finish != def.FinishCm2PerMin ? row.Finish : null
                    };
            }
            try { CostEstimator.SaveSettings(_s); DialogResult = true; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, L.ProductName); }
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    static FrameworkElement Form(out Func<string, string, bool, TextBox> add)
    {
        var panel = new StackPanel { Margin = new Thickness(14) };
        add = (label, value, multi) =>
        {
            panel.Children.Add(PimUi.Text(label, 11.5, brush: "TextSecondaryBrush"));
            var tb = new TextBox { Text = value, Margin = new Thickness(0, 2, 0, 8), Height = multi ? 60 : 28, AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap };
            panel.Children.Add(tb);
            return tb;
        };
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
