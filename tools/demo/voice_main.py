"""Narration for the 90-second main film (1.6). Only the public product script below is sent to edge-tts."""
import asyncio, json, subprocess, sys
from pathlib import Path
import edge_tts

ROOT = Path(__file__).resolve().parent
SCRIPT = {
 'tr': {
  'voices': ['tr-TR-EmelNeural'], 'rate': '+16%',
  'lines': [
   ('s01', 'Meyl Stüdyo for Ti Ar Maşinist: imalatçılar için yapay zekâlı, açık kaynak mail programı.'),
   ('s02', 'Gmail, Microsoft 365 ve IMAP için klasik Outlook düzeni; mailler ve ekler çevrimdışı da açılır.'),
   ('s03', 'Tek tıkla özet ve cevap taslağı; Ollama ile tamamen yerel, ya da Claude, Codex, Gemini ile.'),
   ('s11', 'Yeni: skill\'ler. Maili ve eklerini okur; PDF ve DXF resme bakıp teklifi saniyeler içinde analiz eder.'),
   ('s12', 'Excel skill\'i teslim planını günceller; her değişikliği önce görürsünüz, ek dosyası değişmez.'),
   ('s14', 'Kuralınızı düz bir cümleyle anlatın, skill\'i yapay zekâ yazsın; Claude ve Codex ile aynı biçimde.'),
   ('s13', 'Claude, Codex, Gemini ya da Cursor\'a tek tıkla bağlanın; yetkiyi siz seçersiniz, hiçbir ajan mail gönderemez.'),
   ('s04', 'PDF resimler jilet gibi açılır; işaretleyin, metni düzeltin, OCR yapın, yapay zekâya sorun.'),
   ('s05', 'DXF ve DWG çizimler katmanları ve ölçüleriyle açılır.'),
   ('s06', 'STEP modeli döndürün; gerçek geometriden hammadde ve ağırlık.'),
   ('s07', 'Word, Excel ve PowerPoint, Office olmadan açılır ve düzenlenir.'),
   ('s08', 'CNC programları renkli, takım listesiyle.'),
   ('s09', 'Koşullu biçimlendirme, koyu tema; Türkçe ve İngilizce arayüz.'),
   ('s10', 'Kurulum yok, telemetri yok. Ücretsiz ve açık kaynak, GitHub\'da.'),
  ]},
 'en': {
  'voices': ['en-US-AndrewNeural'], 'rate': '+10%',
  'lines': [
   ('s01', 'MailStudio for TRMachinist: the open-source AI email client for engineers and machinists.'),
   ('s02', 'A classic Outlook layout for Gmail, Microsoft 365 and IMAP. Mail and attachments open offline.'),
   ('s03', 'One click for a summary or a reply draft. Local with Ollama, or with your own Claude, Codex or Gemini.'),
   ('s11', 'New: skills. A skill reads the mail and its PDF and DXF drawings, and analyses the quotation in seconds.'),
   ('s12', 'An Excel skill updates the delivery plan. You see every change first; the attachment never changes.'),
   ('s14', 'Describe a rule in plain words, and the AI writes the skill, in the same format as Claude and Codex skills.'),
   ('s13', 'Connect Claude, Codex, Gemini or Cursor in one click. You choose the permission; no agent can send mail.'),
   ('s04', 'PDF drawings open razor sharp. Mark them up, fix text, run OCR, or ask the AI.'),
   ('s05', 'DXF and DWG drawings open with layers and measuring.'),
   ('s06', 'Spin STEP models, and get raw stock and weight from the true geometry.'),
   ('s07', 'Word, Excel and PowerPoint open and edit, without Microsoft Office.'),
   ('s08', 'CNC programs are highlighted, with a tool list.'),
   ('s09', 'Conditional formatting, dark mode, and an English or Turkish interface.'),
   ('s10', 'No install. No telemetry. Free and open source, on GitHub.'),
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
                await asyncio.sleep(6)
    raise RuntimeError(f'tts failed: {last}')


async def main():
    for lang in (sys.argv[1:] or ['tr', 'en']):
        cfg = SCRIPT[lang]; out = ROOT / f'voice3-{lang}'; out.mkdir(exist_ok=True)
        man = []
        for sid, text in cfg['lines']:
            p = out / f'{sid}.mp3'
            v = await say(text, cfg['voices'], cfg['rate'], p)
            man.append({'id': sid, 'text': text.replace('Meyl Stüdyo for Ti Ar Maşinist', 'MailStudio for TRMachinist').replace('Meyl Stüdyo', 'MailStudio'), 'voice': v, 'duration': round(dur(p), 3)})
            print(lang, sid, v, man[-1]['duration'], flush=True)
        (out / 'manifest.json').write_text(json.dumps(man, ensure_ascii=False, indent=1), encoding='utf-8')
        print(lang, 'total', round(sum(m['duration'] for m in man), 2))

asyncio.run(main())
