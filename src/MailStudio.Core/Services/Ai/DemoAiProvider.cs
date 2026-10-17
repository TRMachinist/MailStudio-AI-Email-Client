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
        if (Has("<skill-instructions>")) return SkillAnswer(p, en);
        if (Has("you are an expert at writing mailstudio skills")) return GeneratedSkill;
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

    const string GeneratedSkill = "---\nname: supplier-delay-check\ndescription: \"Finds delivery delays in supplier mails and lists the affected orders.\"\nmailstudio:\n  title: \"Supplier delay check\"\n  title_tr: \"Tedarikçi gecikme kontrolü\"\n  inputs: [mail, xlsx]\n  run: [button, new-mail]\n  when:\n    text: \"delay;late;postpone;gecikme\"\n  output: note\n---\n\n# Supplier delay check\n\n1. List every order or part number with its new date and the old date if mentioned.\n2. Explain the impact on our own deliveries.\n3. End with the questions to ask the supplier. Never invent dates.\n";

    /// <summary>Canned skill results. Numbers come from the demo files (flange NW-1042, bracket rev C, week-43 schedule).</summary>
    static string SkillAnswer(string p, bool en)
    {
        const string tail = "\n\n(Demo AI – connect Ollama, Claude, Codex or Gemini for real answers.)";
        const string tailTr = "\n\n(Demo AI – gerçek yanıtlar için Ollama, Claude, Codex veya Gemini bağlayın.)";
        if (p.Contains("\"operations\""))
            return """
                {"file":"delivery-schedule-wk43.xlsx","summary":"Bracket rev C (CA-2201) moves to 2026-10-30 because of a material delay.","operations":[
                 {"op":"set","sheet":"Week 43","cell":"G4","value":"2026-10-30"},
                 {"op":"set","sheet":"Week 43","cell":"H4","value":"Delayed - material"},
                 {"op":"fill","sheet":"Week 43","cell":"A4:H4","color":"#FFF2CC"},
                 {"op":"comment","sheet":"Week 43","cell":"G4","text":"Moved from 2026-10-27 (mail from planning, demo)"}]}
                """;
        if (p.Contains("\"move_to\""))
            return en ? """{"flag":true,"read":null,"move_to":null,"reason":"Customer quotation with a delivery deadline (demo)."}"""
                      : """{"flag":true,"read":null,"move_to":null,"reason":"Termin içeren müşteri teklif isteği (demo)."}""";
        if (p.Contains("raw stock & weight"))
            return en
                ? "STEP estimate – BRACKET REV C\n\n• Part envelope: 120 × 80 × 70 mm\n• Part volume 145.1 cm³ → aluminium 7075 (2.81 g/cm³): 0.41 kg · steel (7.85 g/cm³): 1.14 kg\n• Raw stock (3 mm per side): 126 × 86 × 76 mm block = 823.5 cm³ → Al 7075: 2.31 kg\n• Material removal ≈ 82 %\n• Machining class: medium – L-shape with a rib, 9 cylindrical faces (holes), 24 faces, one setup per side.\n\nFormula: weight = volume (cm³) × density (g/cm³)." + tail
                : "STEP tahmini – BRACKET REV C\n\n• Parça zarfı: 120 × 80 × 70 mm\n• Parça hacmi 145,1 cm³ → alüminyum 7075 (2,81 g/cm³): 0,41 kg · çelik (7,85 g/cm³): 1,14 kg\n• Hammadde (her yüzeye 3 mm): 126 × 86 × 76 mm blok = 823,5 cm³ → Al 7075: 2,31 kg\n• Talaş oranı ≈ %82\n• İşleme sınıfı: orta – nervürlü L parça, 9 silindirik yüzey (delik), 24 yüzey, her taraf için bir bağlama.\n\nFormül: ağırlık = hacim (cm³) × yoğunluk (g/cm³)." + tailTr;
        if (p.Contains("technical drawing check"))
            return en
                ? "Drawing check – FLANGE DN100 PN16 (NW-1042)\n\nTitle block\n• Part NW-1042, material S235JR, scale 1:2 – revision not visible in the title block (mail says rev B).\n• General tolerance: not stated on the drawing (mail says ISO 2768-mK).\n\nToleranced features\n• Ø110 bore, Ø220 outer diameter, 8× Ø18 on PCD 180.\n• No geometric tolerances or fits found.\n\nSurface\n• Ra 3.2 noted; no coating note although the mail asks for galvanizing.\n\nQuestions\n• Confirm revision B and galvanizing thickness.\n• Is a flatness tolerance needed on the sealing face?" + tail
                : "Teknik resim kontrolü – FLANŞ DN100 PN16 (NW-1042)\n\nAntet\n• Parça NW-1042, malzeme S235JR, ölçek 1:2 – antette revizyon görünmüyor (mailde rev B yazıyor).\n• Genel tolerans resimde yok (mailde ISO 2768-mK).\n\nToleranslı ölçüler\n• Ø110 iç çap, Ø220 dış çap, Ø180 bölüm dairesinde 8× Ø18.\n• Geometrik tolerans veya geçme bulunamadı.\n\nYüzey\n• Ra 3,2 notu var; mail galvaniz istese de resimde kaplama notu yok.\n\nSorular\n• Revizyon B ve galvaniz kalınlığını teyit edin.\n• Conta yüzeyinde düzlemsellik toleransı gerekli mi?" + tailTr;
        return en
            ? "Quotation request analysis – flange NW-1042 rev B\n\n• Part: flange DN100 PN16, NW-1042 rev B (the drawing title block shows no revision).\n• Material: S235JR, galvanized.\n• Quantity: 120 pcs · Delivery: week 46.\n• Size: Ø220, 8× Ø18 holes on PCD 180, bore Ø110.\n• Critical: general tolerance ISO 2768-mK, 3.1 material certificate.\n\nMissing / to ask\n• Galvanizing standard and thickness.\n• Revision B confirmation – the drawing does not show it.\n• Packaging and delivery address.\n\nDifficulty: simple – turned and drilled part with standard tolerances." + tail
            : "Teklif isteği analizi – flanş NW-1042 rev B\n\n• Parça: DN100 PN16 flanş, NW-1042 rev B (antette revizyon görünmüyor).\n• Malzeme: S235JR, galvaniz.\n• Adet: 120 · Teslim: 46. hafta.\n• Boyut: Ø220, Ø180 bölüm dairesinde 8× Ø18 delik, iç çap Ø110.\n• Kritik: genel tolerans ISO 2768-mK, 3.1 malzeme sertifikası.\n\nEksik / sorulacak\n• Galvaniz standardı ve kalınlığı.\n• Revizyon B teyidi – resimde görünmüyor.\n• Ambalaj ve teslim adresi.\n\nZorluk: basit – standart toleranslı torna + delme parçası." + tailTr;
    }
}
