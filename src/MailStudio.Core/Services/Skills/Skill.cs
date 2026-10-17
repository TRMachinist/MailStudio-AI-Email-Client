using System.Text;
using MailStudio.Models;

namespace MailStudio.Services.Skills;

/// <summary>What a skill produces. The app decides what may happen with it; the AI never sends mail.</summary>
public enum SkillOutput
{
    /// <summary>Free text in the AI pane (analysis, checklist, summary).</summary>
    Note,
    /// <summary>A reply body; the user opens it as a reply and sends it himself.</summary>
    Reply,
    /// <summary>Cell edits for an attached Excel file, applied to a copy after a preview.</summary>
    Excel,
    /// <summary>Flag / mark read / move the mail.</summary>
    Organize,
}

/// <summary>
/// A skill is a folder with a SKILL.md file (same layout as Claude / Agent Skills): YAML front matter with name and
/// description, followed by Markdown instructions. MailStudio-specific options live under a <c>mailstudio:</c> key so
/// the same file also works as a plain Claude/Codex skill.
/// </summary>
public sealed class Skill
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Instructions { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FilePath => Path.Combine(Folder, "SKILL.md");
    public bool BuiltIn { get; init; }
    public bool Enabled { get; set; } = true;

    public string TitleEn { get; init; } = "";
    public string TitleTr { get; init; } = "";
    public string Title => (L.IsEnglish ? TitleEn : TitleTr) is { Length: > 0 } t ? t : (TitleEn.Length > 0 ? TitleEn : Name);

    /// <summary>mail, pdf, step, dxf, xlsx, docx, pptx, csv, text, any</summary>
    public IReadOnlyList<string> Inputs { get; init; } = new[] { "mail" };
    /// <summary>button, new-mail</summary>
    public IReadOnlyList<string> Run { get; init; } = new[] { "button" };
    public IReadOnlyList<FormatCondition> When { get; init; } = Array.Empty<FormatCondition>();
    public SkillOutput Output { get; init; } = SkillOutput.Note;
    public string? Error { get; init; }

    public bool RunsOnButton => Run.Contains("button");
    public bool RunsOnNewMail => Run.Contains("new-mail");
    public bool WantsAttachments => Inputs.Any(i => i != "mail");
    public string InputsText => string.Join(", ", Inputs);
    public string RunText => string.Join(", ", Run.Select(r => r == "new-mail" ? L.T("yeni mail") : L.T("düğme")));
    public string OutputText => Output switch
    {
        SkillOutput.Reply => L.T("cevap taslağı"),
        SkillOutput.Excel => L.T("Excel düzenleme"),
        SkillOutput.Organize => L.T("maili düzenle"),
        _ => L.T("not"),
    };

    static readonly Dictionary<string, string[]> Extensions = new()
    {
        ["pdf"] = new[] { ".pdf" },
        ["step"] = new[] { ".step", ".stp" },
        ["dxf"] = new[] { ".dxf", ".dwg" },
        ["xlsx"] = new[] { ".xlsx", ".xlsm" },
        ["docx"] = new[] { ".docx" },
        ["pptx"] = new[] { ".pptx" },
        ["csv"] = new[] { ".csv" },
        ["text"] = new[] { ".txt", ".nc", ".tap", ".mpf", ".spf", ".gcode", ".iso", ".json", ".xml", ".md", ".log" },
    };

    /// <summary>Is this attachment something the skill wants to read?</summary>
    public bool Accepts(string fileName)
    {
        if (Inputs.Contains("any")) return AttachmentExtractor.CanExtract(fileName);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return Inputs.Any(i => Extensions.TryGetValue(i, out var list) && list.Contains(ext));
    }

    /// <summary>Does the skill apply to this mail (its 'when' conditions and wanted attachment types)?</summary>
    public bool AppliesTo(MailHeader h, IEnumerable<string>? attachmentNames)
    {
        if (When.Count > 0 && !FormatRuleEngine.Matches(new FormatRule { Conditions = When.ToList() }, h)) return false;
        if (!WantsAttachments) return true;
        // A skill that reads attachments shows up only when a matching file is attached – unless its own 'when'
        // conditions already chose the mail and it can also work from the mail text.
        if (When.Count > 0 && Inputs.Contains("mail")) return true;
        return attachmentNames?.Any(Accepts) ?? h.HasAttachments;
    }

    // ------------------------------------------------------------------ parsing

    public static Skill Load(string folder, bool builtIn)
    {
        var id = Path.GetFileName(folder.TrimEnd('\\', '/'));
        try { return Parse(File.ReadAllText(Path.Combine(folder, "SKILL.md")), id, folder, builtIn); }
        catch (Exception ex) { return new Skill { Id = id, Name = id, Folder = folder, BuiltIn = builtIn, Error = ex.Message, Enabled = false }; }
    }

    public static Skill Parse(string text, string id, string folder = "", bool builtIn = false)
    {
        text = text.Replace("\r\n", "\n").TrimStart('﻿');
        if (!text.StartsWith("---\n")) throw new FormatException(L.T("SKILL.md '---' ile başlayan bir ön bölüm içermeli."));
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) throw new FormatException(L.T("SKILL.md ön bölümü '---' ile kapanmıyor."));
        var yaml = MiniYaml.Parse(text[4..end]);
        var body = text[(end + 4)..].TrimStart('-').Trim();
        var ms = yaml.TryGetValue("mailstudio", out var m) && m is Dictionary<string, object> d ? d : new Dictionary<string, object>();
        static string S(Dictionary<string, object> map, string key) => map.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
        IReadOnlyList<string> List(string key, string[] fallback) =>
            ms.TryGetValue(key, out var v) ? v switch
            {
                List<object> l => l.Select(x => x.ToString()!.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray(),
                string s when s.Length > 0 => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToArray(),
                _ => fallback
            } : fallback;

        var name = S(yaml, "name");
        if (name.Length == 0) throw new FormatException(L.T("SKILL.md içinde 'name' alanı yok."));
        var when = new List<FormatCondition>();
        if (ms.TryGetValue("when", out var w) && w is Dictionary<string, object> wd)
            foreach (var (k, v) in wd)
            {
                var field = k.ToLowerInvariant() switch { "has_attachment" or "attachments" => "attachment", var f => f };
                if (FormatRuleEngine.Fields.All(f => f.Field != field)) throw new FormatException(L.F("Bilinmeyen 'when' koşulu: {0}", k));
                var value = v?.ToString() ?? "";
                if (field is "unread" or "flagged" or "attachment" && value.Equals("false", StringComparison.OrdinalIgnoreCase)) continue;
                when.Add(new FormatCondition { Field = field, Value = value });
            }
        var output = S(ms, "output").ToLowerInvariant() switch
        {
            "" or "note" => SkillOutput.Note,
            "reply" or "draft" => SkillOutput.Reply,
            "excel" or "xlsx" => SkillOutput.Excel,
            "organize" or "organise" => SkillOutput.Organize,
            var o => throw new FormatException(L.F("Bilinmeyen 'output' türü: {0} (note, reply, excel, organize)", o)),
        };
        var inputs = List("inputs", new[] { "mail" });
        var run = List("run", new[] { "button" });
        foreach (var i in inputs) if (i != "mail" && i != "any" && !Extensions.ContainsKey(i)) throw new FormatException(L.F("Bilinmeyen giriş türü: {0}", i));
        foreach (var r in run) if (r is not ("button" or "new-mail")) throw new FormatException(L.F("Bilinmeyen 'run' değeri: {0} (button, new-mail)", r));
        return new Skill
        {
            Id = id, Name = name, Description = S(yaml, "description"), Instructions = body, Folder = folder, BuiltIn = builtIn,
            TitleEn = S(ms, "title"), TitleTr = S(ms, "title_tr"),
            Inputs = inputs, Run = run, When = when, Output = output,
        };
    }

    /// <summary>A new skill file (used by "New skill" and by "Create with AI").</summary>
    public static string Template(string name, string title, string description, string instructions,
        string inputs = "mail", string run = "button", string output = "note") =>
        new StringBuilder()
            .Append("---\n")
            .Append($"name: {name}\n")
            .Append($"description: {MiniYaml.Quote(description)}\n")
            .Append("mailstudio:\n")
            .Append($"  title: {MiniYaml.Quote(title)}\n")
            .Append($"  inputs: [{inputs}]\n")
            .Append($"  run: [{run}]\n")
            .Append($"  output: {output}\n")
            .Append("---\n\n")
            .Append(instructions.Trim()).Append('\n')
            .ToString();

    public static string Slug(string text)
    {
        var map = new Dictionary<char, char> { ['ç'] = 'c', ['ğ'] = 'g', ['ı'] = 'i', ['ö'] = 'o', ['ş'] = 's', ['ü'] = 'u' };
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            var ch = map.TryGetValue(c, out var r) ? r : c;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "skill" : s.Length > 60 ? s[..60].Trim('-') : s;
    }
}

/// <summary>The small YAML subset used in SKILL.md front matter: maps (indentation), scalars, [a, b] and "- item" lists.</summary>
public static class MiniYaml
{
    public static Dictionary<string, object> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
        int i = 0;
        return Map(lines, ref i, 0);
    }

    static int Indent(string l) => l.Length - l.TrimStart(' ').Length;

    static Dictionary<string, object> Map(List<string> lines, ref int i, int indent)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (i < lines.Count)
        {
            var line = lines[i]; var ind = Indent(line);
            if (ind < indent) break;
            var t = line.Trim();
            var colon = t.IndexOf(':');
            if (colon <= 0) throw new FormatException(L.F("YAML satırı anlaşılamadı: {0}", t));
            var key = t[..colon].Trim().Trim('"', '\''); var rest = t[(colon + 1)..].Trim();
            if (rest.StartsWith('#')) rest = "";
            i++;
            if (rest.Length == 0)
            {
                if (i < lines.Count && Indent(lines[i]) > ind)
                {
                    if (lines[i].TrimStart().StartsWith("- ")) map[key] = Items(lines, ref i, Indent(lines[i]));
                    else map[key] = Map(lines, ref i, Indent(lines[i]));
                }
                else map[key] = "";
            }
            else if (rest.StartsWith('[') && rest.IndexOf(']') is var close && close > 0 && (close == rest.Length - 1 || rest[(close + 1)..].TrimStart().StartsWith('#')))
                map[key] = rest[1..close].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (object)Scalar(x.Trim())).ToList();
            else if (rest is "|" or ">" or "|-" or ">-")
            {
                var sb = new StringBuilder();
                while (i < lines.Count && Indent(lines[i]) > ind) { sb.Append(lines[i].Trim()).Append(rest.StartsWith('|') ? "\n" : " "); i++; }
                map[key] = sb.ToString().Trim();
            }
            else map[key] = Scalar(rest);
        }
        return map;
    }

    static List<object> Items(List<string> lines, ref int i, int indent)
    {
        var list = new List<object>();
        while (i < lines.Count && Indent(lines[i]) == indent && lines[i].TrimStart().StartsWith("- "))
        { list.Add(Scalar(lines[i].Trim()[2..].Trim())); i++; }
        return list;
    }

    static string Scalar(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\''))
        {
            // Quoted value, possibly followed by a "# comment".
            char q = s[0]; int end = -1;
            for (int k = 1; k < s.Length; k++)
            {
                if (q == '"' && s[k] == '\\') { k++; continue; }
                if (s[k] != q) continue;
                if (q == '\'' && k + 1 < s.Length && s[k + 1] == '\'') { k++; continue; }
                end = k; break;
            }
            if (end > 0 && (end == s.Length - 1 || s[(end + 1)..].TrimStart().StartsWith('#')))
            {
                var inner = s[1..end];
                return q == '"' ? inner.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\") : inner.Replace("''", "'");
            }
        }
        var hash = s.IndexOf(" #", StringComparison.Ordinal);
        return hash > 0 ? s[..hash].Trim() : s;
    }

    public static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
}
