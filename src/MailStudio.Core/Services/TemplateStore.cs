using System.Text.Json;
using MailStudio.Models;

namespace MailStudio.Services;

/// <summary>
/// Persistent storage for email response templates (templates.json).
/// </summary>
public sealed class TemplateStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    readonly string _path;
    readonly object _lock = new();
    List<EmailTemplate> _templates = new();

    public TemplateStore(string? basePath = null)
    {
        _path = Path.Combine(basePath ?? AppPaths.Root, "templates.json");
        Load();
    }

    public IReadOnlyList<EmailTemplate> Templates
    {
        get { lock (_lock) return _templates.ToList(); }
    }

    public EmailTemplate? FindById(string id)
    {
        lock (_lock) return _templates.FirstOrDefault(t => t.Id == id);
    }

    public void Save(EmailTemplate template)
    {
        lock (_lock)
        {
            var idx = _templates.FindIndex(t => t.Id == template.Id);
            template.UpdatedAt = DateTime.UtcNow;
            if (idx >= 0)
            {
                _templates[idx] = template;
            }
            else
            {
                _templates.Add(template);
            }
            Persist();
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _templates.RemoveAll(t => t.Id == id) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    public string ExportJson()
    {
        lock (_lock)
        {
            return JsonSerializer.Serialize(_templates, JsonOptions);
        }
    }

    public int ImportJson(string json)
    {
        var imported = JsonSerializer.Deserialize<List<EmailTemplate>>(json, JsonOptions);
        if (imported == null || imported.Count == 0) return 0;
        lock (_lock)
        {
            int count = 0;
            foreach (var item in imported)
            {
                if (string.IsNullOrWhiteSpace(item.Title)) continue;
                var existing = _templates.FindIndex(t => t.Id == item.Id || (t.Title == item.Title && t.Language == item.Language));
                if (existing >= 0)
                {
                    _templates[existing] = item;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                    _templates.Add(item);
                }
                count++;
            }
            Persist();
            return count;
        }
    }

    public void ResetToDefaults()
    {
        lock (_lock)
        {
            _templates = CreateDefaultTemplates();
            Persist();
        }
    }

    void Load()
    {
        lock (_lock)
        {
            if (File.Exists(_path))
            {
                try
                {
                    var text = JsonFile.ReadOrBackup(_path);
                    var list = JsonSerializer.Deserialize<List<EmailTemplate>>(text, JsonOptions);
                    if (list != null && list.Count > 0)
                    {
                        _templates = list;
                        return;
                    }
                }
                catch
                {
                    // Fall back to defaults on corrupt file
                }
            }

            _templates = CreateDefaultTemplates();
            Persist();
        }
    }

    void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            JsonFile.WriteAtomic(_path, JsonSerializer.Serialize(_templates, JsonOptions));
        }
        catch { }
    }

    public static List<EmailTemplate> CreateDefaultTemplates()
    {
        return new List<EmailTemplate>
        {
            // Turkish default templates
            new()
            {
                Id = "teklif-ektedir-tr",
                Title = "Teklifimiz ektedir",
                Subject = "Teklif: {Konu}",
                Category = "Teklif",
                Language = "tr",
                IsBuiltIn = true,
                Body = "Sayın {Ad} Bey/Hanım,\n\n{ParçaNo} parçasına ait hazırladığımız teklifimiz ve teknik detaylar ekte bilgilerinize sunulmuştur.\n\nTermin ve ticari şartlar teklif dokümanımızda belirtilmiştir. Herhangi bir sorunuz veya revizyon talebiniz olursa lütfen bize bildirin.\n\nİyi çalışmalar dileriz.\n\n{İmza}"
            },
            new()
            {
                Id = "revizyon-alindi-tr",
                Title = "Revizyon alındı",
                Subject = "Revizyon Alındı: {Konu}",
                Category = "İmalat",
                Language = "tr",
                IsBuiltIn = true,
                Body = "Sayın {Ad},\n\n{ParçaNo} parçasına ait güncel revizyon çizim ve teknik verileri tarafımıza ulaşmıştır. Mühendislik ekibimiz tarafından teknik inceleme başlatılmış olup en kısa sürede geri dönüş yapılacaktır.\n\nSaygılarımızla,\n\n{İmza}"
            },
            new()
            {
                Id = "termin-bilgisi-tr",
                Title = "Termin bilgisi",
                Subject = "Termin Güncellemesi: {Konu}",
                Category = "Teslimat",
                Language = "tr",
                IsBuiltIn = true,
                Body = "Merhaba {Ad},\n\n{ParçaNo} siparişinizin imalat durumu planlandığı şekilde devam etmektedir. Tahmini sevk ve teslim tarihimiz: {Tarih}.\n\nBilgilerinize sunar, iyi çalışmalar dileriz.\n\n{İmza}"
            },

            // English default templates
            new()
            {
                Id = "quotation-attached-en",
                Title = "Our quotation is attached",
                Subject = "Quotation: {Subject}",
                Category = "Quote",
                Language = "en",
                IsBuiltIn = true,
                Body = "Dear {FirstName},\n\nPlease find attached our official quotation and technical specifications for {PartNumber}.\n\nLead times and commercial terms are detailed in the attached document. Feel free to reach out if you have any questions or require revisions.\n\nBest regards,\n\n{Signature}"
            },
            new()
            {
                Id = "revision-received-en",
                Title = "Revision received",
                Subject = "Revision Received: {Subject}",
                Category = "Manufacturing",
                Language = "en",
                IsBuiltIn = true,
                Body = "Dear {FirstName},\n\nWe have received the revised engineering drawings and specifications for {PartNumber}. Our team is reviewing the updates and will get back to you promptly.\n\nKind regards,\n\n{Signature}"
            },
            new()
            {
                Id = "delivery-timeline-en",
                Title = "Delivery timeline update",
                Subject = "Delivery Schedule Update: {Subject}",
                Category = "Delivery",
                Language = "en",
                IsBuiltIn = true,
                Body = "Hello {FirstName},\n\nThe manufacturing progress for {PartNumber} is proceeding as scheduled. Estimated dispatch date: {Date}.\n\nPlease let us know if you require any adjustments.\n\nBest regards,\n\n{Signature}"
            }
        };
    }
}
