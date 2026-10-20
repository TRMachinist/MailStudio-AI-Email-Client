"""Generates narration audio for MailStudio 1.8 showcase films (Main film 120-150s & New in 1.8 film 45-60s)."""
import asyncio, json, subprocess, sys
from pathlib import Path
import edge_tts

ROOT = Path(__file__).resolve().parent
PUB = ROOT / 'film' / 'public'

SCRIPT_MAIN = {
 'tr': {
  'voices': ['tr-TR-EmelNeural'], 'rate': '+12%',
  'lines': [
   ('s01', 'Meyl Stüdyo for Ti Ar Maşinist: imalatçılar için yapay zekâlı, açık kaynak mail programı.'),
   ('s02', 'Gmail, Microsoft 365 ve IMAP için klasik Outlook düzeni; birleşik gelen kutusu ve arama klasörleri.'),
   ('s03', 'Tek tıkla özet ve cevap taslağı; Ollama ile tamamen yerel, ya da Claude, Codex, Gemini ile.'),
   ('s31', '1.8 ile yeni: revizyon karşılaştırma. PDF teknik resim ve STEP 3B modellerde ölçü, antet ve geometri farklarını anında görün.'),
   ('s32', 'Teklif masası: teklif isteyen mailler otomatik toplanır; parça no, adet, teslim tarihi ve durum tablosu tek ekranda.'),
   ('s33', 'Takvim ve görevler: hafta görünümü, davetler, cevap takibi ve Google ile Microsoft 365 iki yönlü eşitleme.'),
   ('s36', 'Göndermeyi geri alma, sonra gönderme, ek unutma uyarısı, hazır şablonlar ve gelişmiş mail kuralları.'),
   ('s11', 'Yapay zekâ skill\'leri: maili ve eklerini okur; PDF ve DXF resme bakıp teklifi saniyeler içinde analiz eder.'),
   ('s12', 'Excel skill\'i teslim planını günceller; her değişikliği önce görürsünüz, ek dosyası değişmez.'),
   ('s14', 'Kuralınızı düz bir cümleyle anlatın, skill\'i yapay zekâ yazsın; Claude ve Codex ile aynı biçimde.'),
   ('s13', 'Claude, Codex, Gemini ya da Cursor\'a tek tıkla bağlanın; yetkiyi siz seçersiniz, hiçbir ajan mail gönderemez.'),
   ('s04', 'PDF resimler jilet gibi açılır; işaretleyin, metni düzeltin, OCR yapın, yapay zekâya sorun.'),
   ('s05', 'DXF ve DWG çizimler katmanları ve yakalamalı ölçüleriyle açılır.'),
   ('s06', 'STEP modelini döndürün; gerçek geometriden hammadde ve ağırlık hesaplayın.'),
   ('s07', 'Word, Excel ve PowerPoint Office kurmadan açılır ve düzenlenir.'),
   ('s08', 'CNC programları renkli, takım listesiyle.'),
   ('s09', 'Koşullu biçimlendirme, koyu tema; Türkçe ve İngilizce arayüz.'),
   ('s10', 'Kurulum yok, telemetri yok. Tamamen ücretsiz ve açık kaynak, GitHub\'da.'),
  ]},
 'en': {
  'voices': ['en-US-AndrewNeural'], 'rate': '+8%',
  'lines': [
   ('s01', 'MailStudio for TRMachinist: the open-source AI email client for engineers and machinists.'),
   ('s02', 'A classic Outlook layout for Gmail, Microsoft 365 and IMAP, with unified inbox and search folders.'),
   ('s03', 'One click for a summary or a reply draft. Local with Ollama, or with your own Claude, Codex or Gemini.'),
   ('s31', 'New in 1.8: revision comparison. See drawing dimension, title block and 3D geometry differences side by side.'),
   ('s32', 'Quote desk: quotation requests are captured automatically, tracking part numbers, quantities, dates, and statuses.'),
   ('s33', 'Calendar and tasks: week view, invitations, follow-up tracking, and two-way sync for Google and Microsoft 365.'),
   ('s36', 'Undo send, scheduled send, missing attachment warnings, templates, and advanced email rules.'),
   ('s11', 'AI skills: read the mail and attachments, inspecting PDF and DXF drawings to analyse quotations in seconds.'),
   ('s12', 'An Excel skill updates the delivery plan. You see every change first; the attachment never changes.'),
   ('s14', 'Describe a rule in plain words, and the AI writes the skill, in the same format as Claude and Codex.'),
   ('s13', 'Connect Claude, Codex, Gemini or Cursor in one click. You choose the permission; no agent can ever send mail.'),
   ('s04', 'PDF drawings open razor sharp. Mark them up, fix text, run OCR, or ask the AI.'),
   ('s05', 'DXF and DWG drawings open with layers and snap measuring.'),
   ('s06', 'Spin STEP models, and calculate raw stock and weight directly from the true geometry.'),
   ('s07', 'Word, Excel and PowerPoint open and edit without Office.'),
   ('s08', 'CNC programs are highlighted, with a tool list.'),
   ('s09', 'Conditional formatting, dark mode, and an English or Turkish interface.'),
   ('s10', 'No install, no telemetry. Completely free and open source, on GitHub.'),
  ]},
}

SCRIPT_NEW = {
 'tr': {
  'voices': ['tr-TR-EmelNeural'], 'rate': '+10%',
  'lines': [
   ('k01', 'Meyl Stüdyo 1.8 ile yeni: İmalatçılar için revizyon karşılaştırma, teklif masası, takvim ve akıllı gönderim.'),
   ('k02', 'Revizyon karşılaştırma: PDF teknik resim ve STEP 3B modellerde ölçü, delik, antet ve tolerans farklarını anında görün.'),
   ('k03', 'Teklif masası: teklif isteyen mailler otomatik toplanır; parça no, adet, teslim tarihi ve durum tablosu tek ekranda.'),
   ('k04', 'Akıllı gönderim: göndermeyi geri al, sonra gönder, ek unutma uyarısı, cevap takibi ve otomatik kurallar.'),
   ('k05', 'Takvim ve görevler: iki yönlü Google ve Microsoft 365 eşitlemesi, birleşik gelen kutusu ve bülten abonelikten çıkma.'),
   ('k10', 'Kurulum yok, telemetri yok. Ücretsiz ve açık kaynak, GitHub\'da.'),
  ]},
 'en': {
  'voices': ['en-US-AndrewNeural'], 'rate': '+6%',
  'lines': [
   ('k01', 'New in MailStudio 1.8: revision comparison for manufacturing, quotation desk, calendar, and smart sending.'),
   ('k02', 'Revision comparison: see dimension, hole count, title block, and tolerance changes side by side in seconds.'),
   ('k03', 'Quote desk: incoming RFQs are organized into a structured table tracking part numbers, quantities, dates, and statuses.'),
   ('k04', 'Smart sending: undo send, scheduled send, missing attachment alerts, follow-up tracking, and automated rules.'),
   ('k05', 'Calendar and tasks: two-way sync for Google and Microsoft 365, unified inboxes, snooze, and one-click unsubscribe.'),
   ('k10', 'No install, no telemetry. Free and open source on GitHub.'),
  ]},
}

def dur(p):
    r = subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=duration', '-of', 'default=nw=1:nk=1', str(p)], capture_output=True, text=True)
    return float(r.stdout.strip())

async def say(text, voices, rate, path):
    last = None
    for v in voices:
        for _ in range(20):
            try:
                await edge_tts.Communicate(text, v, rate=rate).save(str(path))
                if path.stat().st_size > 2000: return v
            except Exception as e:
                last = e
                await asyncio.sleep(4)
    raise RuntimeError(f'tts failed: {last}')

async def generate_set(script_map, out_prefix):
    for lang in ['tr', 'en']:
        cfg = script_map[lang]
        out = PUB / f'{out_prefix}' / lang
        out.mkdir(parents=True, exist_ok=True)
        man = []
        for sid, text in cfg['lines']:
            p = out / f'{sid}.mp3'
            v = await say(text, cfg['voices'], cfg['rate'], p)
            clean_text = text.replace('Meyl Stüdyo for Ti Ar Maşinist', 'MailStudio for TRMachinist').replace('Meyl Stüdyo', 'MailStudio')
            d = round(dur(p), 3)
            man.append({'id': sid, 'text': clean_text, 'voice': v, 'duration': d})
            print(f'{out_prefix} {lang} {sid} {v} {d}s', flush=True)
        (out / 'manifest.json').write_text(json.dumps(man, ensure_ascii=False, indent=1), encoding='utf-8')
        total_d = round(sum(m['duration'] for m in man), 2)
        print(f'{out_prefix} {lang} TOTAL: {total_d}s')

async def main():
    print("Generating voice18_main...")
    await generate_set(SCRIPT_MAIN, 'voice18_main')
    print("Generating voice18_new...")
    await generate_set(SCRIPT_NEW, 'voice18_new')

if __name__ == '__main__':
    asyncio.run(main())
