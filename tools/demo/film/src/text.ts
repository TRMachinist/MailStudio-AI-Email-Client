export type SceneText = {chip: string; title: string; sub?: string; pills?: string[]};
export const TEXT: Record<'en' | 'tr', Record<string, SceneText> & {badge: string; outro: any}> = {
  en: {
    badge: 'REAL APP · DEMO DATA',
    s02: {chip: 'Mail', title: 'Classic Outlook layout', sub: 'Offline copy of mail and attachments', pills: ['Gmail', 'Microsoft 365', 'IMAP']},
    s03: {chip: 'AI assistant', title: 'Summary & reply in one click', sub: 'Local with Ollama, or your own subscription', pills: ['Ollama', 'Claude', 'Codex', 'Gemini']},
    s31: {chip: '1.8 · Revision diff', title: 'Compare drawing revisions', sub: 'PDF dimensions, title block, and STEP 3D diff', pills: ['PDF', 'STEP', 'Dimensions', 'OCR']},
    s32: {chip: '1.8 · Quote desk', title: 'RFQ tracking desk', sub: 'Part numbers, quantities, statuses, Excel export', pills: ['RFQ', 'Statuses', 'Excel']},
    s33: {chip: '1.8 · Calendar & Tasks', title: 'Calendar, tasks, 2-way sync', sub: 'Google, Microsoft 365, CalDAV, follow-ups', pills: ['Week view', 'Sync', 'Follow-up']},
    s36: {chip: '1.8 · Smart send', title: 'Undo send, scheduled, rules', sub: 'Missing attachment check, templates, rules engine', pills: ['Undo send', 'Scheduled', 'Rules']},
    s11: {chip: 'Skills', title: 'Skills read mail + attachments', sub: 'Quotation analysis, drawing check, STEP stock', pills: ['PDF', 'DXF', 'STEP', 'Excel']},
    s13: {chip: 'AI agents · MCP', title: 'One-click agent connection', sub: 'Read · Draft · Organize — never send', pills: ['Claude', 'Codex', 'Gemini', 'Cursor']},
    s12: {chip: 'Excel skill', title: 'Every change, checked first', sub: 'Saved as a new copy — the attachment never changes', pills: ['Preview', 'New copy']},
    s14: {chip: 'Write your own', title: 'Describe it — AI writes the skill', sub: 'Plain SKILL.md, same format as Claude & Codex', pills: ['SKILL.md', 'Import', 'Export']},
    s04: {chip: 'PDF studio', title: 'Razor-sharp technical drawings', sub: 'Mark up, correct text, OCR, ask AI', pills: ['Annotate', 'OCR', 'AI']},
    s05: {chip: 'DXF · DWG', title: 'CAD drawings with layers', sub: 'Unlimited zoom, snap measuring', pills: ['Layers', 'Measure', 'Search']},
    s06: {chip: 'STEP 3D', title: 'Measure the real geometry', sub: 'Native B-Rep, raw stock & weight', pills: ['AP242 PMI', 'Stock', 'Weight']},
    s07: {chip: 'Office', title: 'Word · Excel · PowerPoint', sub: 'View and edit — no Office needed', pills: ['.docx', '.xlsx', '.pptx']},
    s08: {chip: 'CNC', title: 'G-code with a tool list', sub: 'Highlighted, read-only, safe', pills: ['G/M codes', 'Tools', 'Feeds']},
    s09: {chip: 'Your way', title: 'Rules, dark mode, two languages', sub: 'Conditional formatting like Outlook', pills: ['Rules', 'Dark', 'EN · TR']},
    outro: {
      line1: 'Free & open source', line2: 'Windows 10 / 11 · portable · no telemetry',
      url: 'github.com/TRMachinist/MailStudio-AI-Email-Client', star: '⭐  Star it on GitHub',
      tiles: ['Mail', 'Revision diff', 'Quote desk', 'Calendar', 'Tasks', 'PDF', 'DXF', 'STEP', 'Office', 'Offline'],
    },
  },
  tr: {
    badge: 'GERÇEK UYGULAMA · DEMO VERİ',
    s02: {chip: 'Mail', title: 'Klasik Outlook düzeni', sub: 'Mail ve eklerin çevrimdışı kopyası', pills: ['Gmail', 'Microsoft 365', 'IMAP']},
    s03: {chip: 'Yapay zeka', title: 'Tek tıkla özet ve cevap', sub: 'Ollama ile yerel ya da kendi aboneliğinizle', pills: ['Ollama', 'Claude', 'Codex', 'Gemini']},
    s31: {chip: '1.8 · Revizyon farkı', title: 'Teknik resim revizyonlarını karşılaştırın', sub: 'PDF ölçüleri, antet ve STEP 3B farkları', pills: ['PDF', 'STEP', 'Ölçüler', 'OCR']},
    s32: {chip: '1.8 · Teklif masası', title: 'Teklif talep masası', sub: 'Parça no, adet, durum takibi, Excel aktarımı', pills: ['Teklif', 'Durumlar', 'Excel']},
    s33: {chip: '1.8 · Takvim ve Görevler', title: 'Takvim, görevler, 2 yönlü eşitleme', sub: 'Google, Microsoft 365, CalDAV, cevap takibi', pills: ['Hafta', 'Eşitleme', 'Takip']},
    s36: {chip: '1.8 · Akıllı gönderim', title: 'Geri al, sonra gönder, kurallar', sub: 'Ek unutma kontrolü, şablonlar, kural motoru', pills: ['Geri al', 'Sonra gönder', 'Kurallar']},
    s11: {chip: "Skill'ler", title: 'Maili ve ekleri okuyan skill', sub: 'Teklif analizi, resim kontrolü, Excel güncelleme', pills: ['PDF', 'DXF', 'STEP', 'Excel']},
    s13: {chip: 'YZ ajanları · MCP', title: 'Tek tıkla ajan bağlantısı', sub: 'Okuma · Taslak · Düzenleme — asla gönderme', pills: ['Claude', 'Codex', 'Gemini', 'Cursor']},
    s12: {chip: "Excel skill'i", title: 'Her değişiklik önce sizde', sub: 'Yeni kopya olarak kaydedilir — ek dosyası değişmez', pills: ['Önizleme', 'Yeni kopya']},
    s14: {chip: 'Kendiniz yazın', title: "Anlatın, skill'i YZ yazsın", sub: 'Düz SKILL.md — Claude ve Codex ile aynı biçim', pills: ['SKILL.md', 'İçe aktar', 'Dışa aktar']},
    s04: {chip: 'PDF stüdyosu', title: 'Jilet gibi keskin teknik resim', sub: 'İşaretle, metni düzelt, OCR, yapay zekâya sor', pills: ['İşaretle', 'OCR', 'YZ']},
    s05: {chip: 'DXF · DWG', title: 'Katmanlarıyla CAD çizimleri', sub: 'Sınırsız yakınlaştırma, yakalamalı ölçü', pills: ['Katman', 'Ölçü', 'Arama']},
    s06: {chip: 'STEP 3B', title: 'Gerçek geometriden ölçün', sub: 'Native B-Rep, hammadde ve ağırlık', pills: ['AP242 PMI', 'Hammadde', 'Ağırlık']},
    s07: {chip: 'Office', title: 'Word · Excel · PowerPoint', sub: 'Office kurmadan görüntüle ve düzenle', pills: ['.docx', '.xlsx', '.pptx']},
    s08: {chip: 'CNC', title: 'Takım listeli G-kodu', sub: 'Renklendirilmiş, salt okunur, güvenli', pills: ['G/M kodları', 'Takımlar', 'İlerleme']},
    s09: {chip: 'Size göre', title: 'Kurallar, koyu tema, iki dil', sub: 'Outlook gibi koşullu biçimlendirme', pills: ['Kurallar', 'Koyu', 'TR · EN']},
    outro: {
      line1: 'Ücretsiz ve açık kaynak', line2: 'Windows 10 / 11 · taşınabilir · telemetri yok',
      url: 'github.com/TRMachinist/MailStudio-AI-Email-Client', star: '⭐  GitHub’da yıldız verin',
      tiles: ['Mail', 'Revizyon farkı', 'Teklif masası', 'Takvim', 'Görevler', 'PDF', 'DXF', 'STEP', 'Office', 'Çevrimdışı'],
    },
  },
};

/** The 1.6 film: skills and agent connection. */
export const TEXT16: Record<'en' | 'tr', any> = {
  en: {
    badge: 'REAL APP · DEMO DATA',
    intro: {title: 'MailStudio 1.6', sub: 'Skills & AI agents', tag: 'Your own AI rules for mail and attachments — and one-click connection to your agent'},
    k02: {chip: 'Skills', title: 'Skills read mail + attachments', sub: 'PDF with OCR, DXF/DWG, STEP, Excel, Word', pills: ['Quotation', 'Drawing check', 'STEP stock']},
    k03: {chip: 'Excel skill', title: 'Every changed cell, checked', sub: 'Preview first — saved as a new copy', pills: ['Preview', 'New copy', 'Attachment untouched']},
    k04: {chip: 'Write your own', title: 'Describe it — AI writes the skill', sub: 'Plain SKILL.md, same format as Claude & Codex skills', pills: ['SKILL.md', 'Import', 'Export']},
    k05: {chip: 'Agents · MCP', title: 'One-click agent connection', sub: 'Read · Draft · Organize — never send', pills: ['Claude', 'Codex', 'Gemini', 'Cursor', 'VS Code']},
    outro: {
      line1: 'Free & open source', line2: 'Windows 10 / 11 · portable · no telemetry',
      url: 'github.com/TRMachinist/MailStudio-AI-Email-Client', star: '⭐  Star it on GitHub',
      tiles: ['Skills', 'PDF · DXF · STEP', 'Excel', 'Claude', 'Codex', 'Gemini', 'Cursor', 'Never sends'],
    },
  },
  tr: {
    badge: 'GERÇEK UYGULAMA · DEMO VERİ',
    intro: {title: 'MailStudio 1.6', sub: "Skill'ler ve YZ ajanları", tag: "Mail ve ekler için kendi yapay zekâ kurallarınız — ve ajanınıza tek tıkla bağlantı"},
    k02: {chip: "Skill'ler", title: 'Maili ve ekleri okur', sub: "OCR'lı PDF, DXF/DWG, STEP, Excel, Word", pills: ['Teklif', 'Resim kontrolü', 'STEP hammadde']},
    k03: {chip: "Excel skill'i", title: 'Her hücre sizin onayınızla', sub: 'Önce önizleme — yeni kopya olarak kaydedilir', pills: ['Önizleme', 'Yeni kopya', 'Ek değişmez']},
    k04: {chip: 'Kendiniz yazın', title: "Anlatın, skill'i YZ yazsın", sub: "Düz SKILL.md — Claude ve Codex skill'leriyle aynı biçim", pills: ['SKILL.md', 'İçe aktar', 'Dışa aktar']},
    k05: {chip: 'Ajanlar · MCP', title: 'Tek tıkla ajan bağlantısı', sub: 'Okuma · Taslak · Düzenleme — asla gönderme', pills: ['Claude', 'Codex', 'Gemini', 'Cursor', 'VS Code']},
    outro: {
      line1: 'Ücretsiz ve açık kaynak', line2: 'Windows 10 / 11 · taşınabilir · telemetri yok',
      url: 'github.com/TRMachinist/MailStudio-AI-Email-Client', star: '⭐  GitHub’da yıldız verin',
      tiles: ["Skill'ler", 'PDF · DXF · STEP', 'Excel', 'Claude', 'Codex', 'Gemini', 'Cursor', 'Asla göndermez'],
    },
  },
};

/** The 1.8 film: revision diff, quote desk, calendar sync, smart sending. */
export const TEXT18: Record<'en' | 'tr', any> = {
  en: {
    badge: 'REAL APP · DEMO DATA',
    intro: {title: 'MailStudio 1.8', sub: 'Revision Diff & Quote Desk', tag: 'Drawing comparison, quotation desk, calendar sync, and smart sending'},
    k02: {chip: 'Revision comparison', title: 'Compare revisions side by side', sub: 'PDF drawing dimensions, title block, and STEP geometry', pills: ['PDF', 'STEP', 'CAD', 'Excel']},
    k03: {chip: 'Quote desk', title: 'Automated RFQ tracking table', sub: 'Track part numbers, quantities, deadlines, and statuses', pills: ['RFQ', 'Statuses', 'Excel export']},
    k04: {chip: 'Smart sending', title: 'Undo send & follow-up tracking', sub: 'Scheduled send, missing attachment alert, automated rules', pills: ['Undo send', 'Scheduled', 'Follow-up']},
    k05: {chip: 'Calendar & Tasks', title: 'Two-way sync & unified inboxes', sub: 'Google & Microsoft 365 sync, unsubscribe bar, snooze', pills: ['Google', 'M365', 'Unsubscribe', 'Snooze']},
    outro: {
      line1: 'Free & open source', line2: 'Windows 10 / 11 · portable · no telemetry',
      url: 'github.com/TRMachinist/MailStudio-AI-Email-Client', star: '⭐  Star it on GitHub',
      tiles: ['Revision diff', 'Quote desk', 'Calendar sync', 'Smart send', 'Unified inbox', 'Snooze', 'Rules', 'Free & open'],
    },
  },
  tr: {
    badge: 'GERÇEK UYGULAMA · DEMO VERİ',
    intro: {title: 'MailStudio 1.8', sub: 'Revizyon Farkı ve Teklif Masası', tag: 'Teknik resim karşılaştırma, teklif masası, takvim eşitleme ve akıllı gönderim'},
    k02: {chip: 'Revizyon karşılaştırma', title: 'Revizyonları yan yana karşılaştırın', sub: 'PDF teknik resim ölçüleri, antet ve STEP geometrisi', pills: ['PDF', 'STEP', 'CAD', 'Excel']},
    k03: {chip: 'Teklif masası', title: 'Otomatik teklif takip masası', sub: 'Parça no, miktar, termin tarihi ve durum takibi', pills: ['Teklif', 'Durumlar', 'Excel aktarımı']},
    k04: {chip: 'Akıllı gönderim', title: 'Geri al ve cevap gelmezse takip', sub: 'Sonra gönder, ek unutma uyarısı, otomatik kurallar', pills: ['Geri al', 'Sonra gönder', 'Takip']},
    k05: {chip: 'Takvim ve Görevler', title: 'İki yönlü eşitleme ve birleşik kutu', sub: 'Google ve M365 eşitleme, bülten abonelikten çıkma, erteleme', pills: ['Google', 'M365', 'Abonelikten çık', 'Ertele']},
    outro: {
      line1: 'Ücretsiz ve açık kaynak', line2: 'Windows 10 / 11 · taşınabilir · telemetri yok',
      url: 'github.com/TRMachinist/MailStudio-AI-Email-Client', star: '⭐  GitHub’da yıldız verin',
      tiles: ['Revizyon farkı', 'Teklif masası', 'Takvim eşitleme', 'Akıllı gönderim', 'Birleşik kutu', 'Ertele', 'Kurallar', 'Ücretsiz'],
    },
  },
};
