using System.Text;

namespace MailStudio.Models;

public enum RevisionDiffKind
{
    Added,
    Removed,
    Modified,
    Unchanged
}

public class RevisionDiffItem
{
    public RevisionDiffKind Kind { get; set; }
    /// <summary>Category: "Ölçü", "Tolerans", "Not", "Antet", "Hacim", "Kutu Ölçüleri", "Delik", "Katman", "Hücre", "PMI"</summary>
    public string Category { get; set; } = "";
    /// <summary>Property name: e.g. "Dış Çap", "Kalınlık", "Toplam Hacim", "Week 43!D4"</summary>
    public string Property { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Description { get; set; } = "";
    public string CategoryDisplay => L.T(Category);
    public string PropertyDisplay => L.T(Property);

    public string BadgeText => Kind switch
    {
        RevisionDiffKind.Added => "+ " + L.T("Eklendi"),
        RevisionDiffKind.Removed => "- " + L.T("Silindi"),
        RevisionDiffKind.Modified => "Δ " + L.T("Değişti"),
        _ => L.T("Aynı")
    };
}

public class RevisionCandidate
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public string Revision { get; set; } = "";
    public string? MailSubject { get; set; }
    public string? Sender { get; set; }
    public DateTime? Date { get; set; }
    public uint? Uid { get; set; }
    public string AccountId { get; set; } = "";
    public string Folder { get; set; } = "";
    /// <summary>The mail that carries this revision (null for a file found on disk).</summary>
    public MailHeader? Header { get; set; }
    public AttachmentInfo? Attachment { get; set; }
    public string Display => string.Join("  ·  ", new[] { FileName, Revision.Length > 0 ? "Rev " + Revision : "", MailSubject ?? "", Date?.ToString("g") ?? "" }.Where(x => x.Length > 0));
}

public class RevisionDiffReport
{
    public string PartNumber { get; set; } = "";
    public string FileType { get; set; } = ""; // "PDF", "STEP", "DXF", "Excel"
    public string OldRevision { get; set; } = "";
    public string NewRevision { get; set; } = "";
    public string OldFileName { get; set; } = "";
    public string NewFileName { get; set; } = "";
    public string OldFilePath { get; set; } = "";
    public string NewFilePath { get; set; } = "";
    public List<RevisionDiffItem> Items { get; set; } = new();
    /// <summary>How the files were read (e.g. "OCR: verify numbers on the drawing"); shown above the list.</summary>
    public List<string> Notes { get; set; } = new();

    public int AddedCount => Items.Count(i => i.Kind == RevisionDiffKind.Added);
    public int RemovedCount => Items.Count(i => i.Kind == RevisionDiffKind.Removed);
    public int ModifiedCount => Items.Count(i => i.Kind == RevisionDiffKind.Modified);
    public int TotalChanges => AddedCount + RemovedCount + ModifiedCount;

    /// <summary>
    /// Generates structured bulleted text for pasting into email replies.
    /// </summary>
    public string ToReplyText()
    {
        var sb = new StringBuilder();
        var partTitle = !string.IsNullOrWhiteSpace(PartNumber) ? PartNumber : Path.GetFileNameWithoutExtension(NewFileName);
        var revTitle = !string.IsNullOrWhiteSpace(OldRevision) && !string.IsNullOrWhiteSpace(NewRevision)
            ? $" ({OldRevision} → {NewRevision})"
            : "";
        sb.AppendLine(L.F("Revizyon Değişiklik Özeti: {0}{1}", partTitle, revTitle));
        sb.AppendLine(L.F("Dosya: {0} → {1}", OldFileName, NewFileName));
        sb.AppendLine();

        var modified = Items.Where(i => i.Kind == RevisionDiffKind.Modified).ToList();
        var added = Items.Where(i => i.Kind == RevisionDiffKind.Added).ToList();
        var removed = Items.Where(i => i.Kind == RevisionDiffKind.Removed).ToList();

        if (modified.Count > 0)
        {
            sb.AppendLine(L.T("Değişen Karakteristikler:"));
            foreach (var item in modified)
            {
                var desc = !string.IsNullOrWhiteSpace(item.Description) ? item.Description : $"{item.OldValue} → {item.NewValue}";
                sb.AppendLine($"• [{item.CategoryDisplay}] {item.PropertyDisplay}: {desc}");
            }
            sb.AppendLine();
        }

        if (added.Count > 0)
        {
            sb.AppendLine(L.T("Eklenen Öğeler:"));
            foreach (var item in added)
            {
                var val = !string.IsNullOrWhiteSpace(item.NewValue) ? item.NewValue : item.Description;
                sb.AppendLine($"• [{item.CategoryDisplay}] {item.PropertyDisplay}: {val}");
            }
            sb.AppendLine();
        }

        if (removed.Count > 0)
        {
            sb.AppendLine(L.T("Kaldırılan Öğeler:"));
            foreach (var item in removed)
            {
                var val = !string.IsNullOrWhiteSpace(item.OldValue) ? item.OldValue : item.Description;
                sb.AppendLine($"• [{item.CategoryDisplay}] {item.PropertyDisplay}: {val}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Generates prompt for AI manufacturing impact analysis.
    /// </summary>
    public string ToAiPrompt()
    {
        var sb = new StringBuilder();
        if (L.IsEnglish)
        {
            sb.AppendLine("Analyse the following drawing / model revision changes as a manufacturing engineer:");
            sb.AppendLine(ToReplyText());
            sb.AppendLine();
            sb.AppendLine("Give a short, clear, professional assessment under these headings:");
            sb.AppendLine("1. Process and cycle time: which machining operations and times are affected?");
            sb.AppendLine("2. Tooling and fixturing: is a new tool, fixture or special clamping needed?");
            sb.AppendLine("3. Cost and risk: does cost or manufacturing risk increase? Are tolerance or hole changes critical?");
            sb.AppendLine("4. Recommendation: advice for the shop floor and for quality control.");
            sb.AppendLine("Only use the changes listed above; say so when something cannot be judged from them.");
            return sb.ToString();
        }
        sb.AppendLine("Aşağıdaki teknik resim / model revizyon değişikliklerini bir imalat mühendisi gözüyle analiz et:");
        sb.AppendLine(ToReplyText());
        sb.AppendLine();
        sb.AppendLine("Lütfen şu başlıklar altında kısa, net ve profesyonel bir değerlendirme yap:");
        sb.AppendLine("1. İmalat Yöntemi ve Süresi: İşleme operasyonları ve süreleri nasıl etkilenir?");
        sb.AppendLine("2. Takımlama ve Bağlama: Yeni takım, aparat veya özel fikstür gerekir mi?");
        sb.AppendLine("3. Maliyet ve Risk: Maliyet artışı ve imalat riski var mı? Tolerans/delik değişiklikleri kritik mi?");
        sb.AppendLine("4. Öneri: İmalat atölyesine ve kalite kontrol ekibine özel tavsiyeler.");
        sb.AppendLine("Yalnızca yukarıdaki değişikliklere dayan; onlardan anlaşılamayan bir şey varsa bunu açıkça söyle.");
        return sb.ToString();
    }
}
