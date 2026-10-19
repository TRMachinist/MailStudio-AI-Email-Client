using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MimeKit;

namespace MailStudio.Services;

/// <summary>A contact saved by the user (Outlook "Contacts"). Mail-collected addresses are not stored here.</summary>
public sealed class Contact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Email2 { get; set; } = "";
    public string Email3 { get; set; } = "";
    public string Company { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string Notes { get; set; } = "";

    [JsonIgnore] public string DisplayName => string.Join(' ', new[] { FirstName.Trim(), LastName.Trim() }.Where(s => s.Length > 0));
    [JsonIgnore] public IEnumerable<string> Emails => new[] { Email, Email2, Email3 }.Select(e => e.Trim()).Where(e => e.Contains('@'));

    /// <summary>Splits a display name into first / last name (last word is the last name).</summary>
    public void SetName(string? full)
    {
        var parts = (full ?? "").Trim().Trim('"', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        FirstName = parts.Length <= 1 ? string.Join(' ', parts) : string.Join(' ', parts[..^1]);
        LastName = parts.Length <= 1 ? "" : parts[^1];
    }
}

/// <summary>A contact group (Outlook "distribution list"): picking it fills in every member.</summary>
public sealed class ContactGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Members as "Name &lt;address&gt;" or plain addresses.</summary>
    public List<string> Members { get; set; } = new();

    [JsonIgnore] public string MembersText => string.Join("; ", Members);
}

/// <summary>
/// The address book the user maintains: contacts and groups, saved in %AppData%\MailStudio\contacts.json.
/// Import/export in the formats Outlook, Gmail and phones use (CSV and vCard).
/// </summary>
public sealed class ContactStore
{
    sealed class Data
    {
        public List<Contact> Contacts { get; set; } = new();
        public List<ContactGroup> Groups { get; set; } = new();
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly string _path;
    Data _data = new();
    public event Action? Changed;

    public ContactStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.Root, "contacts.json");
        try { if (File.Exists(_path)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? new(); } catch { _data = new(); }
    }

    public IReadOnlyList<Contact> Contacts => _data.Contacts;
    public IReadOnlyList<ContactGroup> Groups => _data.Groups;

    public Contact? FindByEmail(string? address) =>
        string.IsNullOrWhiteSpace(address) ? null : _data.Contacts.FirstOrDefault(c => c.Emails.Any(e => e.Equals(address.Trim(), StringComparison.OrdinalIgnoreCase)));

    public void Save(Contact c)
    {
        var i = _data.Contacts.FindIndex(x => x.Id == c.Id);
        if (i >= 0) _data.Contacts[i] = c; else _data.Contacts.Add(c);
        Write();
    }

    public void Save(ContactGroup g)
    {
        var i = _data.Groups.FindIndex(x => x.Id == g.Id);
        if (i >= 0) _data.Groups[i] = g; else _data.Groups.Add(g);
        Write();
    }

    public void Delete(Contact c) { _data.Contacts.RemoveAll(x => x.Id == c.Id); Write(); }
    public void Delete(ContactGroup g) { _data.Groups.RemoveAll(x => x.Id == g.Id); Write(); }

    /// <summary>Adds (or completes) a contact from a mail address; returns the stored contact.</summary>
    public Contact AddFromMail(string? name, string address)
    {
        var c = FindByEmail(address);
        if (c == null) { c = new Contact { Email = address.Trim() }; c.SetName(name); _data.Contacts.Add(c); }
        else if (c.DisplayName.Length == 0) c.SetName(name);
        Write();
        return c;
    }

    void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json), new UTF8Encoding(false));
        MailStudio.Services.SafeFile.Move(tmp, _path, true);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ import

    /// <summary>Imports a .csv (Outlook, Gmail, Turkish Outlook headers) or .vcf file. Existing addresses are updated, not duplicated.</summary>
    public (int added, int updated) Import(string path)
    {
        var list = Path.GetExtension(path).Equals(".vcf", StringComparison.OrdinalIgnoreCase) ? ParseVCard(ReadText(path)) : ParseCsv(ReadText(path));
        int added = 0, updated = 0;
        foreach (var c in list)
        {
            var existing = c.Emails.Select(FindByEmail).FirstOrDefault(x => x != null);
            if (existing == null) { _data.Contacts.Add(c); added++; continue; }
            foreach (var p in typeof(Contact).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.Name != nameof(Contact.Id)))
                if (string.IsNullOrWhiteSpace((string?)p.GetValue(existing)) && !string.IsNullOrWhiteSpace((string?)p.GetValue(c))) p.SetValue(existing, p.GetValue(c));
            updated++;
        }
        if (added + updated > 0) Write();
        return (added, updated);
    }

    static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1254).GetString(bytes);   // Turkish Outlook exports ANSI
        }
    }

    public static List<Contact> ParseCsv(string text)
    {
        var rows = CsvRows(text);
        var result = new List<Contact>();
        if (rows.Count < 2) return result;
        var head = rows[0].Select(Norm).ToArray();
        int Col(params string[] names) { foreach (var n in names) { var i = Array.IndexOf(head, Norm(n)); if (i >= 0) return i; } return -1; }
        int first = Col("First Name", "Given Name", "Ad", "Adı"), last = Col("Last Name", "Family Name", "Soyadı", "Soyad"),
            name = Col("Name", "Display Name", "Full Name", "Ad Soyad", "Görünen Ad"),
            mail = Col("E-mail Address", "E-mail 1 - Value", "Email Address", "Email", "E-posta Adresi", "E-posta"),
            mail2 = Col("E-mail 2 Address", "E-mail 2 - Value", "E-posta 2 Adresi"),
            mail3 = Col("E-mail 3 Address", "E-mail 3 - Value", "E-posta 3 Adresi"),
            company = Col("Company", "Organization 1 - Name", "Organization Name", "Şirket"),
            title = Col("Job Title", "Organization 1 - Title", "İş Unvanı", "Unvan"),
            phone = Col("Business Phone", "Phone 1 - Value", "Phone", "İş Telefonu", "Telefon"),
            mobile = Col("Mobile Phone", "Phone 2 - Value", "Cep Telefonu", "Mobil Telefon"),
            notes = Col("Notes", "Notlar");
        foreach (var r in rows.Skip(1))
        {
            string V(int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
            var c = new Contact
            {
                FirstName = V(first), LastName = V(last), Email = V(mail), Email2 = V(mail2), Email3 = V(mail3),
                Company = V(company), JobTitle = V(title), Phone = V(phone), Mobile = V(mobile), Notes = V(notes),
            };
            if (c.DisplayName.Length == 0 && V(name).Length > 0) c.SetName(V(name));
            if (c.Emails.Any() || c.DisplayName.Length > 0) result.Add(c);
        }
        return result;
    }

    static string Norm(string s) => new string(s.Trim().Trim('"').ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    static List<List<string>> CsvRows(string text)
    {
        var firstLine = text.Split('\n')[0];
        var sep = firstLine.Count(ch => ch == ';') > firstLine.Count(ch => ch == ',') ? ';' : ',';
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Any(c => c.Length > 0)) rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(ch);
        }
        row.Add(cell.ToString());
        if (row.Any(c => c.Length > 0)) rows.Add(row);
        return rows;
    }

    public static List<Contact> ParseVCard(string text)
    {
        // unfold continuation lines (RFC 6350)
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && lines.Count > 0) lines[^1] += raw[1..]; else lines.Add(raw);
        var result = new List<Contact>(); Contact? c = null;
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':'); if (colon <= 0) continue;
            var key = line[..colon]; var value = Unescape(line[(colon + 1)..]);
            var name = key.Split(';')[0].Split('.').Last().ToUpperInvariant();
            var ps = key.ToUpperInvariant();
            if (name == "BEGIN" && value.Equals("VCARD", StringComparison.OrdinalIgnoreCase)) { c = new Contact(); continue; }
            if (c == null) continue;
            switch (name)
            {
                case "END": if (c.Emails.Any() || c.DisplayName.Length > 0) result.Add(c); c = null; break;
                case "N":
                    var n = value.Split(';');
                    c.LastName = n.ElementAtOrDefault(0)?.Trim() ?? ""; c.FirstName = string.Join(' ', new[] { n.ElementAtOrDefault(1), n.ElementAtOrDefault(2) }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                    break;
                case "FN": if (c.DisplayName.Length == 0) c.SetName(value); break;
                case "EMAIL": if (c.Email.Length == 0) c.Email = value; else if (c.Email2.Length == 0) c.Email2 = value; else if (c.Email3.Length == 0) c.Email3 = value; break;
                case "TEL": if (ps.Contains("CELL")) c.Mobile = value; else if (c.Phone.Length == 0) c.Phone = value; else if (c.Mobile.Length == 0) c.Mobile = value; break;
                case "ORG": c.Company = value.Split(';')[0]; break;
                case "TITLE": c.JobTitle = value; break;
                case "NOTE": c.Notes = value; break;
            }
        }
        return result;
    }

    static string Unescape(string v) => v.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
    static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\n", "\\n").Replace(",", "\\,").Replace(";", "\\;");

    // ------------------------------------------------------------------ export

    public void ExportCsv(string path)
    {
        string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("First Name,Last Name,E-mail Address,E-mail 2 Address,E-mail 3 Address,Company,Job Title,Business Phone,Mobile Phone,Notes");
        foreach (var c in _data.Contacts)
            sb.AppendLine(string.Join(',', new[] { c.FirstName, c.LastName, c.Email, c.Email2, c.Email3, c.Company, c.JobTitle, c.Phone, c.Mobile, c.Notes }.Select(Q)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // BOM: Excel opens Turkish characters correctly
    }

    public void ExportVCard(string path)
    {
        var sb = new StringBuilder();
        foreach (var c in _data.Contacts)
        {
            sb.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");
            sb.Append($"N:{Escape(c.LastName)};{Escape(c.FirstName)};;;\r\nFN:{Escape(c.DisplayName.Length > 0 ? c.DisplayName : c.Email)}\r\n");
            foreach (var e in c.Emails) sb.Append($"EMAIL;TYPE=INTERNET:{e}\r\n");
            if (c.Phone.Length > 0) sb.Append($"TEL;TYPE=WORK:{Escape(c.Phone)}\r\n");
            if (c.Mobile.Length > 0) sb.Append($"TEL;TYPE=CELL:{Escape(c.Mobile)}\r\n");
            if (c.Company.Length > 0) sb.Append($"ORG:{Escape(c.Company)}\r\n");
            if (c.JobTitle.Length > 0) sb.Append($"TITLE:{Escape(c.JobTitle)}\r\n");
            if (c.Notes.Length > 0) sb.Append($"NOTE:{Escape(c.Notes)}\r\n");
            sb.Append("END:VCARD\r\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Splits an address field ("A &lt;a@x&gt;; b@y") into (name, address) pairs.</summary>
    public static List<(string Name, string Address)> SplitAddresses(string? field)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(field)) return list;
        if (InternetAddressList.TryParse(field.Replace(';', ','), out var parsed))
            foreach (var mb in parsed.Mailboxes) list.Add((MailService.FixTurkish(mb.Name?.Trim('"', ' ', '\'')) ?? "", mb.Address));
        else
            foreach (var part in field.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (part.Contains('@')) list.Add(("", part.Trim().Trim('<', '>')));
        return list;
    }
}
