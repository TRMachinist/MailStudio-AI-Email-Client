using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MailStudio.Services.Ai;

/// <summary>
/// Offline AI used only by the demo mailbox (`--demo`): streams canned, clearly labelled answers so the AI
/// features can be tried without installing Ollama or signing in to Claude / Codex / Gemini.
/// </summary>
public sealed class DemoAiProvider : IAiProvider
{
    public string Name => "Demo AI";
    public string Badge => L.IsEnglish ? "offline demo answers" : "çevrimdışı demo yanıtları";
    public bool SupportsMcp => false;
    public Task<IReadOnlyList<string>> GetModelsAsync() => Task.FromResult<IReadOnlyList<string>>(new[] { "demo" });

    public async IAsyncEnumerable<string> StreamAsync(string prompt, string? model, [EnumeratorCancellation] CancellationToken ct, string? mcpServerExe = null)
    {
        foreach (Match w in Regex.Matches(Answer(prompt).Replace("**", "").Replace("_(", "(").Replace(")_", ")").Replace("*", ""), @"\S+\s*"))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(55, ct);
            yield return w.Value;
        }
    }

    static string Answer(string prompt)
    {
        var en = L.IsEnglish;
        var subject = Regex.Match(prompt, @"^(?:Subject|Konu): (.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        var p = prompt.ToLowerInvariant();
        bool Has(params string[] k) => k.Any(x => p.Contains(x));
        if (Has("3-5 bullet", "3-5 madde"))
            return en
                ? $"**Summary – {subject}**\n\n• Customer asks for a quotation of **120 pcs** flange NW-1042 rev B (S235JR, galvanized).\n• Delivery requested for **week 46**.\n• Tolerances per drawing, general ISO 2768-mK.\n• A **3.1 material certificate** is required.\n\n**Action for you:** send the quotation including price, lead time and certificate cost.\n\n_(Demo AI – connect Ollama, Claude, Codex or Gemini for real answers.)_"
                : $"**Özet – {subject}**\n\n• Müşteri NW-1042 rev B flanştan **120 adet** teklif istiyor (S235JR, galvaniz).\n• İstenen teslim: **46. hafta**.\n• Toleranslar resme göre, genel ISO 2768-mK.\n• **3.1 malzeme sertifikası** gerekli.\n\n**Sizden beklenen:** fiyat, termin ve sertifika bedelini içeren teklif.\n\n_(Demo AI – gerçek yanıtlar için Ollama, Claude, Codex veya Gemini bağlayın.)_";
        if (Has("reply draft", "cevap taslağı", "write the email", "e-postayı yaz"))
            return en
                ? "Hello Anna,\n\nthank you for your request. We can supply 120 pcs of flange NW-1042 rev B in S235JR, galvanized, including a 3.1 material certificate.\n\nUnit price: EUR 38.50\nLead time: 3 weeks after order (delivery in week 46 is possible)\n\nOur detailed quotation will follow by tomorrow.\n\nBest regards"
                : "Merhaba Anna,\n\ntalebiniz için teşekkürler. NW-1042 rev B flanştan 120 adet, S235JR galvaniz ve 3.1 malzeme sertifikası dahil tedarik edebiliriz.\n\nBirim fiyat: 38,50 EUR\nTermin: siparişten sonra 3 hafta (46. hafta teslim mümkün)\n\nAyrıntılı teklifimiz yarın iletilecektir.\n\nSaygılarımla";
        if (Has("tasks, owners", "yapılacak işleri"))
            return en
                ? "1. Prepare quotation for 120 pcs NW-1042 — **you** — today\n2. Check galvanizing capacity — **production** — this week\n3. Request 3.1 certificate cost — **purchasing** — before quotation"
                : "1. 120 adet NW-1042 teklifini hazırla — **siz** — bugün\n2. Galvaniz kapasitesini kontrol et — **üretim** — bu hafta\n3. 3.1 sertifika bedelini sor — **satın alma** — tekliften önce";
        if (Has("translate", "çevir"))
            return en ? "(Demo AI) Translation is available with a real AI provider." : "(Demo AI) Çeviri gerçek bir yapay zeka sağlayıcısıyla çalışır.";
        if (Has("<sayfa_metni>"))
            return en
                ? "The drawing shows **flange DN100 PN16** (NW-1042), material **S235JR**:\n\n• Outer diameter Ø220, bore Ø110\n• 8× Ø18 holes on PCD 180\n• Thickness 24 mm, surface Ra 3.2\n\nSource: page 1. Verify values against the drawing."
                : "Resim **DN100 PN16 flanşı** (NW-1042), malzeme **S235JR** gösteriyor:\n\n• Dış çap Ø220, delik Ø110\n• Ø180 bölüm dairesinde 8× Ø18 delik\n• Kalınlık 24 mm, yüzey Ra 3.2\n\nKaynak: sayfa 1. Değerleri resimde doğrulayın.";
        return en
            ? "I am the **Demo AI** of MailStudio. In the real app this pane talks to **Ollama** (local), **Claude Code**, **Codex** or **Gemini CLI** — and with mailbox access it can search your mail and leave drafts (it never sends).\n\nTry *Summarize* or *Reply draft* on the selected mail."
            : "Ben MailStudio'nun **Demo AI**'ıyım. Gerçek kullanımda bu panel **Ollama** (yerel), **Claude Code**, **Codex** veya **Gemini CLI** ile konuşur; posta kutusu erişimiyle maillerinizde arayıp taslak bırakabilir (asla göndermez).\n\nSeçili mailde *Özetle* veya *Cevap taslağı* deneyin.";
    }
}

