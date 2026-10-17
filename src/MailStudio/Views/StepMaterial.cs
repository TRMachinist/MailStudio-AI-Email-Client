using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>Material selection, part and stock weight; looking perpendicular to a face.</summary>
public sealed partial class StepAttachmentWindow
{
    public sealed record Material(string Group, string Name, double DensityGcm3)
    {
        public override string ToString() => $"{Name}  ·  {DensityGcm3.ToString("0.00", CultureInfo.CurrentCulture)} g/cm³";
    }

    /// <summary>Common aerospace / manufacturing materials; densities are nominal (room temperature) values.</summary>
    public static readonly Material[] Materials =
    {
        new(L.T("Alüminyum"), "Al 7075-T6 / T7351", 2.81), new(L.T("Alüminyum"), "Al 7050-T7451", 2.83), new(L.T("Alüminyum"), "Al 2024-T3 / T351", 2.78),
        new(L.T("Alüminyum"), "Al 6061-T6", 2.70), new(L.T("Alüminyum"), "Al 6082-T6", 2.70), new(L.T("Alüminyum"), "Al 5083-H111", 2.66),
        new(L.T("Titanyum"), "Ti-6Al-4V (Grade 5)", 4.43), new(L.T("Titanyum"), "Ti Grade 2", 4.51),
        new(L.T("Çelik"), L.T("Yapı çeliği S235 / St37"), 7.85), new(L.T("Çelik"), "C45 / 1045", 7.85), new(L.T("Çelik"), "4140 / 42CrMo4", 7.85), new(L.T("Çelik"), "4340", 7.85),
        new(L.T("Çelik"), L.T("Takım çeliği 1.2379 (D2)"), 7.70), new(L.T("Çelik"), L.T("Takım çeliği 1.2344 (H13)"), 7.80),
        new(L.T("Paslanmaz"), "304 / 1.4301", 7.93), new(L.T("Paslanmaz"), "316L / 1.4404", 7.98), new(L.T("Paslanmaz"), "17-4PH / 1.4542", 7.80), new(L.T("Paslanmaz"), "15-5PH", 7.80), new(L.T("Paslanmaz"), "420 / 1.4021", 7.74),
        new(L.T("Nikel"), "Inconel 718", 8.19), new(L.T("Nikel"), "Inconel 625", 8.44),
        new(L.T("Bakır"), L.T("Pirinç CuZn39Pb3"), 8.47), new(L.T("Bakır"), L.T("Bakır Cu-ETP"), 8.94), new(L.T("Bakır"), L.T("Bronz CuSn8"), 8.80),
        new(L.T("Döküm"), L.T("Pik döküm GG25"), 7.20), new(L.T("Döküm"), L.T("Sfero GGG40"), 7.10),
        new(L.T("Plastik"), "POM (Delrin)", 1.41), new(L.T("Plastik"), "PA6 (Naylon)", 1.14), new(L.T("Plastik"), "PEEK", 1.32), new(L.T("Plastik"), "PTFE (Teflon)", 2.20), new(L.T("Plastik"), "PE-HD", 0.95),
    };

    readonly ComboBox _material = new() { Margin = new Thickness(9, 2, 9, 4), ItemsSource = Materials, ToolTip = L.T("Hammadde: parça ve stok ağırlığı bu yoğunlukla hesaplanır") };
    readonly TextBlock _massInfo = new() { Margin = new Thickness(9, 0, 9, 8), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    double? _partVolumeMm3, _stockVolumeMm3;

    FrameworkElement MaterialPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = L.T("Hammadde"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(9, 4, 9, 0) });
        panel.Children.Add(_material);
        panel.Children.Add(_massInfo);
        var saved = AppSettings.Shared.LastMaterial;
        _material.SelectedItem = Materials.FirstOrDefault(m => m.Name == saved) ?? Materials[0];
        _material.SelectionChanged += (_, _) =>
        {
            if (_material.SelectedItem is Material m) { AppSettings.Shared.LastMaterial = m.Name; try { AppSettings.Shared.Save(); } catch { } }
            UpdateMass();
        };
        UpdateMass();
        return panel;
    }

    void SetPartVolume(JsonElement r)
    {
        _partVolumeMm3 = r.TryGetProperty("part_volume_mm3", out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0 ? v.GetDouble() : null;
        UpdateMass();
    }

    void SetStockVolume(double? mm3) { _stockVolumeMm3 = mm3; UpdateMass(); }

    static string Kg(double mm3, double density) { var kg = mm3 / 1000.0 * density / 1000.0; return kg < 1 ? $"{kg * 1000:0.#} g" : $"{kg:0.###} kg"; }

    void UpdateMass()
    {
        if (_material.SelectedItem is not Material m) { _massInfo.Text = ""; return; }
        var lines = new List<string>();
        lines.Add(_partVolumeMm3 is { } part ? L.F("Parça: {0}   ({1:0.###} cm³)", Kg(part, m.DensityGcm3), part / 1000) : L.T("Parça: hacim hesaplanamadı (kapalı katı bulunamadı)"));
        if (_stockVolumeMm3 is { } stock)
        {
            lines.Add(L.F("Stok: {0}   ({1:0.###} cm³)", Kg(stock, m.DensityGcm3), stock / 1000));
            if (_partVolumeMm3 is { } p && stock > 0) lines.Add(L.F("Talaş: {0}   · kullanım oranı %{1:0.#}", Kg(Math.Max(0, stock - p), m.DensityGcm3), p / stock * 100));
        }
        else lines.Add(L.T("Stok: önizleme yok (Bounding Box / Stok sekmesinden kutu veya silindir oluşturun)"));
        _massInfo.Text = string.Join("\n", lines);
    }

    bool _lookFlipped;
    async Task LookAtSelectedFaceAsync()
    {
        var face = _selection.LastOrDefault(s => s.Kind == "face");
        if (face == null) { _status.Text = L.T("Önce bir yüz seçin (Seçim: Yüz veya Otomatik), sonra Yüzeye dik bak."); return; }
        var info = await _worker.CallAsync("inspect", face.Payload, _life.Token);
        if (_closed) return;
        if (!info.TryGetProperty("normal", out var n)) { _status.Text = L.T("Bu yüzde tek bir normal yok (eğri yüz); düzlemsel yüz seçin veya başka bir nokta deneyin."); return; }
        var v = n.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var normal = new Vector3D(v[0], v[1], v[2]);
        var current = _eye; current.Normalize(); normal.Normalize();
        _lookFlipped = Vector3D.DotProduct(current, normal) > .999 && !_lookFlipped ? true : false;
        LookAlong(_lookFlipped ? -normal : normal);
        _status.Text = L.F("{0} yüzeyine dik bakılıyor", face.Name) + (_lookFlipped ? L.T(" (arka taraftan).") : ".");
    }
}
