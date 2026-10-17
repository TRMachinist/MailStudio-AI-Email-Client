export type SceneText = {chip: string; title: string; sub?: string; pills?: string[]};
export const TEXT: Record<'en' | 'tr', Record<string, SceneText> & {badge: string; outro: any}> = {
  en: {
    badge: 'REAL APP · DEMO DATA',
    s02: {chip: 'Mail', title: 'Classic Outlook layout', sub: 'Offline copy of mail and attachments', pills: ['Gmail', 'Microsoft 365', 'IMAP']},
    s03: {chip: 'AI assistant', title: 'Summary & reply in one click', sub: 'Local with Ollama, or your own subscription', pills: ['Ollama', 'Claude', 'Codex', 'Gemini']},
    s04: {chip: 'PDF studio', title: 'Razor-sharp technical drawings', sub: 'Mark up, correct text, OCR, ask AI', pills: ['Annotate', 'OCR', 'AI']},
    s05: {chip: 'DXF · DWG', title: 'CAD drawings with layers', sub: 'Unlimited zoom, snap measuring', pills: ['Layers', 'Measure', 'Search']},
    s06: {chip: 'STEP 3D', title: 'Measure the real geometry', sub: 'Native B-Rep, raw stock & weight', pills: ['AP242 PMI', 'Stock', 'Weight']},
    s07: {chip: 'Office', title: 'Word · Excel · PowerPoint', sub: 'View and edit — no Office needed', pills: ['.docx', '.xlsx', '.pptx']},
    s08: {chip: 'CNC', title: 'G-code with a tool list', sub: 'Highlighted, read-only, safe', pills: ['G/M codes', 'Tools', 'Feeds']},
    s09: {chip: 'Your way', title: 'Rules, dark mode, two languages', sub: 'Conditional formatting like Outlook', pills: ['Rules', 'Dark', 'EN · TR']},
    outro: {
      line1: 'Free & open source', line2: 'Windows 10 / 11 · portable · no telemetry',
      url: 'github.com/TRMachinist/MailStudio-AI-Email-Client', star: '⭐  Star it on GitHub',
      tiles: ['Mail', 'AI', 'PDF', 'DXF/DWG', 'STEP', 'Office', 'CNC', 'Offline'],
    },
  },
  tr: {
    badge: 'GERÇEK UYGULAMA · DEMO VERİ',
    s02: {chip: 'Mail', title: 'Klasik Outlook düzeni', sub: 'Mail ve eklerin çevrimdışı kopyası', pills: ['Gmail', 'Microsoft 365', 'IMAP']},
    s03: {chip: 'Yapay zeka', title: 'Tek tıkla özet ve cevap', sub: 'Ollama ile yerel ya da kendi aboneliğinizle', pills: ['Ollama', 'Claude', 'Codex', 'Gemini']},
    s04: {chip: 'PDF stüdyosu', title: 'Jilet gibi keskin teknik resim', sub: 'İşaretle, metni düzelt, OCR, yapay zekâya sor', pills: ['İşaretle', 'OCR', 'YZ']},
    s05: {chip: 'DXF · DWG', title: 'Katmanlarıyla CAD çizimleri', sub: 'Sınırsız yakınlaştırma, yakalamalı ölçü', pills: ['Katman', 'Ölçü', 'Arama']},
    s06: {chip: 'STEP 3B', title: 'Gerçek geometriden ölçün', sub: 'Native B-Rep, hammadde ve ağırlık', pills: ['AP242 PMI', 'Hammadde', 'Ağırlık']},
    s07: {chip: 'Office', title: 'Word · Excel · PowerPoint', sub: 'Office kurmadan görüntüle ve düzenle', pills: ['.docx', '.xlsx', '.pptx']},
    s08: {chip: 'CNC', title: 'Takım listeli G-kodu', sub: 'Renklendirilmiş, salt okunur, güvenli', pills: ['G/M kodları', 'Takımlar', 'İlerleme']},
    s09: {chip: 'Size göre', title: 'Kurallar, koyu tema, iki dil', sub: 'Outlook gibi koşullu biçimlendirme', pills: ['Kurallar', 'Koyu', 'TR · EN']},
    outro: {
      line1: 'Ücretsiz ve açık kaynak', line2: 'Windows 10 / 11 · taşınabilir · telemetri yok',
      url: 'github.com/TRMachinist/MailStudio-AI-Email-Client', star: '⭐  GitHub’da yıldız verin',
      tiles: ['Mail', 'Yapay zekâ', 'PDF', 'DXF/DWG', 'STEP', 'Office', 'CNC', 'Çevrimdışı'],
    },
  },
};
