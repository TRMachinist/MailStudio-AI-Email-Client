using System.Text.Json.Serialization;

namespace MailStudio.Models;

public enum QuoteStatus
{
    New = 0,        // Yeni
    InProgress = 1, // Hazırlanıyor
    Sent = 2,       // Gönderildi
    Won = 3,        // Kazanıldı
    Lost = 4        // Kaybedildi
}

public class QuoteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Customer { get; set; } = "";
    public string Contact { get; set; } = "";
    public string? ContactEmail { get; set; }
    public string PartNumber { get; set; } = "";
    public string Revision { get; set; } = "";
    public int Quantity { get; set; }
    public string Material { get; set; } = "";
    public DateTime? RequestedDelivery { get; set; }
    public DateTime? Deadline { get; set; }
    public QuoteStatus Status { get; set; } = QuoteStatus.New;
    public string Notes { get; set; } = "";
    public string? SourceMail { get; set; }        // AccountId|Folder|Uid
    public string? SourceSubject { get; set; }
    public string? SourceMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public bool IsDeadlineOverdue =>
        Deadline.HasValue && Deadline.Value.Date < DateTime.Today && Status is QuoteStatus.New or QuoteStatus.InProgress;

    [JsonIgnore]
    public bool IsDeadlineToday =>
        Deadline.HasValue && Deadline.Value.Date == DateTime.Today && Status is QuoteStatus.New or QuoteStatus.InProgress;

    [JsonIgnore]
    public bool IsDeadlineApproaching =>
        Deadline.HasValue && Deadline.Value.Date > DateTime.Today && Deadline.Value.Date <= DateTime.Today.AddDays(2) && Status is QuoteStatus.New or QuoteStatus.InProgress;

    [JsonIgnore]
    public string StatusDisplayName => Status switch
    {
        QuoteStatus.New => L.T("Yeni"),
        QuoteStatus.InProgress => L.T("Hazırlanıyor"),
        QuoteStatus.Sent => L.T("Gönderildi"),
        QuoteStatus.Won => L.T("Kazanıldı"),
        QuoteStatus.Lost => L.T("Kaybedildi"),
        _ => Status.ToString()
    };

    [JsonIgnore]
    public string QuantityDisplay => Quantity > 0 ? Quantity.ToString("N0", L.Culture) + " " + L.T("adet") : "-";

    [JsonIgnore]
    public string DeadlineDisplay => Deadline.HasValue ? Deadline.Value.ToString("d MMM yyyy", L.Culture) : "-";

    [JsonIgnore]
    public string RequestedDeliveryDisplay => RequestedDelivery.HasValue ? RequestedDelivery.Value.ToString("d MMM yyyy", L.Culture) : "-";

    public QuoteItem Clone() => (QuoteItem)MemberwiseClone();
}
