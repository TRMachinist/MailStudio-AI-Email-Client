using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailStudio.Services.Costing;

/// <summary>A machinable material: density (exact, nominal) and editable commercial / machining figures.</summary>
public sealed record MaterialSpec(string Group, string Name, double DensityGcm3, double PricePerKg, double RoughCm3PerMin, double FinishCm2PerMin)
{
    /// <summary>Name in the UI language; <see cref="Name"/> stays the fixed key of the user's price overrides.</summary>
    public string Display => L.T(Name);
    public override string ToString() => $"{Display}  ·  {DensityGcm3.ToString("0.00", CultureInfo.CurrentCulture)} g/cm³";
}

/// <summary>Editable cost parameters (saved in cost-settings.json). Prices are examples – the user sets his own.</summary>
public sealed class CostSettings
{
    public string Currency { get; set; } = "EUR";
    /// <summary>Machine hour rate incl. operator (currency per hour).</summary>
    public double HourlyRate { get; set; } = 60;
    /// <summary>Set-up per order (minutes) – spread over the quantity.</summary>
    public double SetupMinutes { get; set; } = 90;
    /// <summary>Loading, deburring, inspection per part (minutes).</summary>
    public double HandlingMinutes { get; set; } = 4;
    /// <summary>Drilling / reaming per detected hole (minutes).</summary>
    public double MinutesPerHole { get; set; } = 0.4;
    /// <summary>Stock allowance on every side of the bounding box (mm).</summary>
    public double StockAllowanceMm { get; set; } = 3;
    /// <summary>Saw cut / offcut loss on the raw material (%).</summary>
    public double MaterialWastePercent { get; set; } = 10;
    /// <summary>Profit / overhead on top of the cost (%).</summary>
    public double MarginPercent { get; set; } = 20;
    /// <summary>Per-material overrides of price / removal rates (key = material name).</summary>
    public Dictionary<string, MaterialOverride> Materials { get; set; } = new();
    public CompanyInfo Company { get; set; } = new();
    public int QuoteValidityDays { get; set; } = 30;
    public string PaymentTerms { get; set; } = "";
    public string DeliveryTerms { get; set; } = "";
}

public sealed class MaterialOverride
{
    public double? PricePerKg { get; set; }
    public double? RoughCm3PerMin { get; set; }
    public double? FinishCm2PerMin { get; set; }
}

/// <summary>Sender details printed on the quotation PDF.</summary>
public sealed class CompanyInfo
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Web { get; set; } = "";
    public string TaxId { get; set; } = "";
    /// <summary>PNG/JPG logo printed top left (optional).</summary>
    public string? LogoPath { get; set; }
    public string Signature { get; set; } = "";
}

/// <summary>Geometry of one part as read from its STEP model (exact B-Rep values from the technical worker).</summary>
public sealed record PartGeometry(double SizeX, double SizeY, double SizeZ, double VolumeMm3, double SurfaceMm2, int HoleCount)
{
    /// <summary>Reads the result of the worker operation <c>extract_step</c>.</summary>
    public static PartGeometry FromExtract(JsonElement e)
    {
        var size = new double[3];
        if (e.TryGetProperty("overall_size_xyz_mm", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() == 3)
            for (int i = 0; i < 3; i++) size[i] = s[i].GetDouble();
        double D(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var holes = 0;
        if (e.TryGetProperty("bodies", out var bodies) && bodies.ValueKind == JsonValueKind.Array)
            foreach (var b in bodies.EnumerateArray())
                if (b.TryGetProperty("holes", out var hs) && hs.ValueKind == JsonValueKind.Array)
                    foreach (var h in hs.EnumerateArray())
                        if (h.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number) holes += c.GetInt32();
        return new PartGeometry(size[0], size[1], size[2], D("total_volume_mm3"), D("total_surface_mm2"), holes);
    }
}

public sealed record CostLine(string Label, string Detail, double Amount);

/// <summary>Result of a rough cost estimate. Every figure is shown with how it was calculated.</summary>
public sealed class CostEstimate
{
    public required MaterialSpec Material { get; init; }
    public required int Quantity { get; init; }
    public required string Currency { get; init; }
    public double StockX { get; init; }
    public double StockY { get; init; }
    public double StockZ { get; init; }
    public double StockVolumeMm3 { get; init; }
    public double PartVolumeMm3 { get; init; }
    public double PartMassKg { get; init; }
    public double StockMassKg { get; init; }
    public double RemovedCm3 { get; init; }
    public double CycleMinutes { get; init; }
    public double UnitCost { get; init; }
    public double UnitPrice { get; init; }
    public double TotalPrice => Math.Round(UnitPrice * Quantity, 2);
    public List<CostLine> Lines { get; } = new();
    public List<string> Assumptions { get; } = new();

    public string Money(double v) => CostEstimator.Money(v, Currency);

    /// <summary>Plain-text breakdown (stored with the quote, pasted into notes).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.F("Maliyet tahmini – {0}, {1} adet", Material.Display, Quantity));
        sb.AppendLine(StockX > 0
            ? L.F("Stok: {0:0.#} × {1:0.#} × {2:0.#} mm  ·  {3:0.###} kg (parça {4:0.###} kg)", StockX, StockY, StockZ, StockMassKg, PartMassKg)
            : L.F("Stok: {0:0.###} kg (parça {1:0.###} kg)", StockMassKg, PartMassKg));
        foreach (var l in Lines) sb.AppendLine($"• {l.Label}: {Money(l.Amount)}   ({l.Detail})");
        sb.AppendLine(L.F("Birim maliyet: {0}  ·  Birim fiyat: {1}  ·  Toplam: {2}", Money(UnitCost), Money(UnitPrice), Money(TotalPrice)));
        foreach (var a in Assumptions) sb.AppendLine("– " + a);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Rough machining cost from a STEP model: raw stock (bounding box + allowance) × material price, roughing time from the
/// removed volume, finishing time from the surface area, drilling per hole, handling per part and the set-up spread over
/// the quantity. It is a first estimate for a quotation – every value and assumption is listed so the user can check it.
/// </summary>
public static class CostEstimator
{
    /// <summary>Common aerospace / manufacturing materials. Densities are nominal; prices and rates are editable examples.</summary>
    public static readonly MaterialSpec[] DefaultMaterials =
    {
        new(L.T("Alüminyum"), "Al 7075-T6 / T7351", 2.81, 7.5, 60, 40), new(L.T("Alüminyum"), "Al 7050-T7451", 2.83, 8.5, 60, 40),
        new(L.T("Alüminyum"), "Al 2024-T3 / T351", 2.78, 7.0, 55, 40), new(L.T("Alüminyum"), "Al 6061-T6", 2.70, 5.0, 70, 45),
        new(L.T("Alüminyum"), "Al 6082-T6", 2.70, 4.8, 70, 45), new(L.T("Alüminyum"), "Al 5083-H111", 2.66, 5.5, 60, 40),
        new(L.T("Titanyum"), "Ti-6Al-4V (Grade 5)", 4.43, 35, 6, 8), new(L.T("Titanyum"), "Ti Grade 2", 4.51, 28, 8, 10),
        new(L.T("Çelik"), "Yapı çeliği S235 / St37", 7.85, 1.4, 18, 20), new(L.T("Çelik"), "C45 / 1045", 7.85, 1.8, 16, 18),
        new(L.T("Çelik"), "4140 / 42CrMo4", 7.85, 2.4, 14, 16), new(L.T("Çelik"), "4340", 7.85, 3.0, 12, 15),
        new(L.T("Çelik"), "Takım çeliği 1.2379 (D2)", 7.70, 6.0, 8, 12), new(L.T("Çelik"), "Takım çeliği 1.2344 (H13)", 7.80, 6.5, 9, 12),
        new(L.T("Paslanmaz"), "304 / 1.4301", 7.93, 4.5, 10, 14), new(L.T("Paslanmaz"), "316L / 1.4404", 7.98, 6.0, 9, 13),
        new(L.T("Paslanmaz"), "17-4PH / 1.4542", 7.80, 9.0, 8, 12), new(L.T("Paslanmaz"), "15-5PH", 7.80, 10, 8, 12), new(L.T("Paslanmaz"), "420 / 1.4021", 7.74, 4.0, 10, 14),
        new(L.T("Nikel"), "Inconel 718", 8.19, 45, 3, 5), new(L.T("Nikel"), "Inconel 625", 8.44, 50, 3, 5),
        new(L.T("Bakır"), "Pirinç CuZn39Pb3", 8.47, 8.0, 45, 40), new(L.T("Bakır"), "Bakır Cu-ETP", 8.94, 11, 30, 30), new(L.T("Bakır"), "Bronz CuSn8", 8.80, 14, 25, 25),
        new(L.T("Döküm"), "Pik döküm GG25", 7.20, 1.5, 20, 20), new(L.T("Döküm"), "Sfero GGG40", 7.10, 1.8, 18, 18),
        new(L.T("Plastik"), "POM (Delrin)", 1.41, 6.0, 90, 60), new(L.T("Plastik"), "PA6 (Naylon)", 1.14, 5.0, 90, 60), new(L.T("Plastik"), "PEEK", 1.32, 90, 50, 40),
        new(L.T("Plastik"), "PTFE (Teflon)", 2.20, 20, 80, 60), new(L.T("Plastik"), "PE-HD", 0.95, 4.0, 100, 70),
    };

    static string SettingsPath => Path.Combine(AppPaths.Root, "cost-settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static CostSettings LoadSettings(string? path = null)
    {
        path ??= SettingsPath;
        var text = JsonFile.ReadOrBackup(path);
        if (text.TrimStart().StartsWith('[')) return new CostSettings();   // missing or damaged file
        try { return JsonSerializer.Deserialize<CostSettings>(text, Json) ?? new CostSettings(); }
        catch (JsonException) { return new CostSettings(); }
    }

    public static void SaveSettings(CostSettings s, string? path = null) => JsonFile.WriteAtomic(path ?? SettingsPath, JsonSerializer.Serialize(s, Json));

    /// <summary>The material list with the user's price / rate overrides applied.</summary>
    public static IReadOnlyList<MaterialSpec> Materials(CostSettings s) =>
        DefaultMaterials.Select(m => s.Materials.TryGetValue(m.Name, out var o)
            ? m with { PricePerKg = o.PricePerKg ?? m.PricePerKg, RoughCm3PerMin = o.RoughCm3PerMin ?? m.RoughCm3PerMin, FinishCm2PerMin = o.FinishCm2PerMin ?? m.FinishCm2PerMin }
            : m).ToList();

    /// <summary>Best match for a free-text material from a mail or quote ("7075", "Ti6Al4V", "S235JR", "1.4404"…).</summary>
    public static MaterialSpec? Match(IEnumerable<MaterialSpec> list, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        static string N(string x) => new string(x.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var t = N(text);
        MaterialSpec? best = null; var bestLen = 0;
        foreach (var m in list)
            foreach (var token in (m.Name + " " + m.Display).Split(new[] { ' ', '/', '(', ')', '·' }, StringSplitOptions.RemoveEmptyEntries).Select(N).Where(x => x.Length >= 3))
                if (t.Contains(token) && token.Length > bestLen) { best = m; bestLen = token.Length; }
        if (best == null && (t.Contains("ti6al4v") || t.Contains("grade5"))) best = list.FirstOrDefault(m => m.Name.StartsWith("Ti-6Al-4V"));
        if (best == null && t.Contains("s235")) best = list.FirstOrDefault(m => m.Name.Contains("S235"));
        return best;
    }

    /// <param name="stockVolumeMm3">Optional exact stock volume (e.g. the cylinder/box built in the STEP viewer); otherwise bounding box + allowance.</param>
    public static CostEstimate Estimate(PartGeometry g, MaterialSpec m, int quantity, CostSettings s, double? stockVolumeMm3 = null, double[]? stockSize = null)
    {
        if (g.VolumeMm3 <= 0) throw new InvalidOperationException(L.T("Parçanın hacmi okunamadı (kapalı katı bulunamadı); maliyet hesaplanamaz."));
        quantity = Math.Max(1, quantity);
        var a = Math.Max(0, s.StockAllowanceMm);
        var sx = stockSize?[0] ?? g.SizeX + 2 * a; var sy = stockSize?[1] ?? g.SizeY + 2 * a; var sz = stockSize?[2] ?? g.SizeZ + 2 * a;
        var stockMm3 = stockVolumeMm3 ?? sx * sy * sz;
        if (stockMm3 < g.VolumeMm3) stockMm3 = g.VolumeMm3;
        if (stockVolumeMm3 != null && stockSize == null) sx = sy = sz = 0;   // custom stock (e.g. a cylinder) – no box size to show
        var partKg = g.VolumeMm3 / 1e6 * m.DensityGcm3;
        var stockKg = stockMm3 / 1e6 * m.DensityGcm3;
        var removedCm3 = Math.Max(0, stockMm3 - g.VolumeMm3) / 1000;
        var surfaceCm2 = g.SurfaceMm2 / 100;

        var rough = m.RoughCm3PerMin > 0 ? removedCm3 / m.RoughCm3PerMin : 0;
        var finish = m.FinishCm2PerMin > 0 ? surfaceCm2 / m.FinishCm2PerMin : 0;
        var drill = g.HoleCount * s.MinutesPerHole;
        var cycle = rough + finish + drill + s.HandlingMinutes;
        var perMin = s.HourlyRate / 60.0;

        var material = stockKg * m.PricePerKg * (1 + s.MaterialWastePercent / 100);
        var machining = cycle * perMin;
        var setup = s.SetupMinutes * perMin / quantity;
        var unitCost = material + machining + setup;
        var unitPrice = Math.Round(unitCost * (1 + s.MarginPercent / 100), 2);

        var est = new CostEstimate
        {
            Material = m, Quantity = quantity, Currency = s.Currency,
            StockX = sx, StockY = sy, StockZ = sz, StockVolumeMm3 = stockMm3, PartVolumeMm3 = g.VolumeMm3,
            PartMassKg = partKg, StockMassKg = stockKg, RemovedCm3 = removedCm3, CycleMinutes = cycle,
            UnitCost = Math.Round(unitCost, 2), UnitPrice = unitPrice,
        };
        string N(double v, string f = "0.##") => v.ToString(f, CultureInfo.CurrentCulture);
        est.Lines.Add(new(L.T("Hammadde"), L.F("{0} kg × {1}/kg + %{2} fire", N(stockKg, "0.###"), Money(m.PricePerKg, s.Currency), N(s.MaterialWastePercent)), Math.Round(material, 2)));
        est.Lines.Add(new(L.T("Kaba işleme"), L.F("{0} cm³ talaş ÷ {1} cm³/dk = {2} dk", N(removedCm3, "0.#"), N(m.RoughCm3PerMin), N(rough, "0.#")), Math.Round(rough * perMin, 2)));
        est.Lines.Add(new(L.T("Finiş"), L.F("{0} cm² yüzey ÷ {1} cm²/dk = {2} dk", N(surfaceCm2, "0.#"), N(m.FinishCm2PerMin), N(finish, "0.#")), Math.Round(finish * perMin, 2)));
        if (g.HoleCount > 0) est.Lines.Add(new(L.T("Delme"), L.F("{0} delik × {1} dk", g.HoleCount, N(s.MinutesPerHole)), Math.Round(drill * perMin, 2)));
        est.Lines.Add(new(L.T("Bağlama / çapak / kontrol"), L.F("{0} dk", N(s.HandlingMinutes)), Math.Round(s.HandlingMinutes * perMin, 2)));
        est.Lines.Add(new(L.T("Hazırlık (adede bölünmüş)"), L.F("{0} dk ÷ {1} adet", N(s.SetupMinutes), quantity), Math.Round(setup, 2)));
        est.Assumptions.Add(L.F("Saat ücreti {0}/saat, kâr payı %{1}.", Money(s.HourlyRate, s.Currency), N(s.MarginPercent)));
        est.Assumptions.Add(stockVolumeMm3 != null ? L.T("Stok: STEP görüntüleyicide oluşturulan stok.") : L.F("Stok: dış ölçü kutusu + her yüzde {0} mm pay.", N(a)));
        est.Assumptions.Add(L.T("Kaba tahmindir; ısıl işlem, kaplama, fikstür ve özel takımlar dahil değildir."));
        return est;
    }

    public static string Money(double v, string currency)
    {
        var sym = currency switch { "EUR" => "€", "USD" => "$", "TRY" => "₺", "GBP" => "£", _ => currency + " " };
        var s = v.ToString("#,0.00", CultureInfo.CurrentCulture);
        return currency is "EUR" or "USD" or "GBP" ? sym + s : s + " " + sym.Trim();
    }
}
