using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MailStudio;

/// <summary>
/// User-interface localization. Source strings are written in Turkish; <see cref="T"/> returns the English text from the
/// embedded dictionary (i18n/en.json) when the English language is active. Unknown strings are returned unchanged.
/// </summary>
public static class L
{
    static Dictionary<string, string>? _en;
    static string _lang = Resolve(null);

    /// <summary>Product name shown in window titles and the About texts.</summary>
    public const string ProductName = "MailStudio for TRMachinist";

    /// <summary>Active UI language: "tr" or "en".</summary>
    public static string Language => _lang;
    public static bool IsEnglish => _lang == "en";
    public static CultureInfo Culture => IsEnglish ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Resolves a stored setting ("auto", "tr", "en") to a concrete language.</summary>
    public static string Resolve(string? setting)
    {
        var s = (setting ?? "auto").Trim().ToLowerInvariant();
        var env = Environment.GetEnvironmentVariable("MAILSTUDIO_LANG");
        if (!string.IsNullOrWhiteSpace(env)) s = env.Trim().ToLowerInvariant();
        if (s is "tr" or "en") return s;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
    }

    public static void Init(string? setting)
    {
        _lang = Resolve(setting);
        Environment.SetEnvironmentVariable("MAILSTUDIO_LANG", _lang);   // child processes (technical worker) follow the UI
    }

    static Dictionary<string, string> En
    {
        get
        {
            if (_en != null) return _en;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var s = typeof(L).Assembly.GetManifestResourceStream("MailStudio.i18n.en.json");
                if (s != null)
                    foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new())
                        if (!string.IsNullOrEmpty(kv.Value)) d[kv.Key] = kv.Value;
            }
            catch { }
            return _en = d;
        }
    }

    /// <summary>Patterns for texts built from parts (mostly produced by the technical worker).</summary>
    static readonly (System.Text.RegularExpressions.Regex Re, string En)[] Patterns =
    {
        (new(@"^Ağırlık merkezi ([XYZ])$"), "Center of mass $1"),
        (new(@"^Koordinat ([XYZ])$"), "Coordinate $1"),
        (new(@"^Nesne (\d+) ↔ (\d+)$"), "Object $1 ↔ $2"),
        (new(@"^Nesne (\d+)$"), "Object $1"),
        (new(@"^Bileşen (\d+)$"), "Component $1"),
        (new(@"^(.+) · Gövde (\d+)$"), "$1 · Body $2"),
        (new(@"^(\d+) kayıt grafik sunum içeriyor\. Grafik sunumu olmayan semantik kayıtlar listede kalır\.$"), "$1 records include a graphical presentation. Semantic records without one stay in the list."),
        (new(@"^DWG okunamadı\. (.*)$", System.Text.RegularExpressions.RegexOptions.Singleline), "Could not read DWG. $1"),
    };

    /// <summary>Translates a UI string.</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsEnglish) return text ?? "";
        if (En.TryGetValue(text, out var v)) return v;
        foreach (var (re, en) in Patterns) if (re.IsMatch(text)) return re.Replace(text, en);
        return text;
    }

    static readonly System.Text.RegularExpressions.Regex JsonValue = new(@"(?<=:)""((?:[^""\\]|\\.)*)""", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Translates the string values of a JSON text (worker replies). Keys, numbers and arrays are untouched.</summary>
    public static string TJson(string json)
    {
        if (!IsEnglish) return json;
        return JsonValue.Replace(json, m =>
        {
            var raw = m.Groups[1].Value;
            if (raw.Length == 0 || raw.Length > 400) return m.Value;
            string s;
            try { s = JsonSerializer.Deserialize<string>(m.Value) ?? ""; } catch { return m.Value; }
            var t = T(s);
            return ReferenceEquals(t, s) || t == s ? m.Value : JsonSerializer.Serialize(t);
        });
    }

    /// <summary>Translates a composite format string and formats it with the arguments.</summary>
    public static string F(string format, params object?[] args)
    {
        var f = T(format);
        try { return string.Format(Culture, f, args); }
        catch (FormatException) { return string.Format(Culture, format, args); }
    }

    /// <summary>True if a dictionary entry exists (used by tests).</summary>
    public static bool Has(string text) => En.ContainsKey(text);
    public static IReadOnlyDictionary<string, string> Dictionary => En;
}
