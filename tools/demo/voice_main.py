"""Narration for the main film (1.6: with skills and agents). Only the public product script below is sent to edge-tts."""
import asyncio, json, subprocess, sys
from pathlib import Path
import edge_tts

ROOT = Path(__file__).resolve().parent
SCRIPT = {
 'tr': {
  'voices': ['tr-TR-EmelNeural'], 'rate': '+14%',
  'lines': [
   ('s01', 'Meyl Stüdyo for Ti Ar Maşinist: imalatçılar için yapay zekâlı, açık kaynak mail programı.'),
   ('s02', 'Klasik Outlook düzeni; mailler ve ekler bilgisayarınızda, çevrimdışı da açılır.'),
   ('s03', 'Tek tıkla özet ve cevap taslağı; Ollama ile tamamen yerel, ya da Claude, Codex, Gemini ile.'),
   ('s11', 'Yeni: skill\'ler. Maili ve eklerini okur, teklifi analiz eder, Excel planını günceller.'),
   ('s13', 'Claude, Codex ya da Gemini\'ye tek tıkla bağlanın; ajanlar okur ve taslak yazar, asla mail göndermez.'),
   ('s04', 'PDF, DXF ve DWG çizimler jilet gibi açılır; katmanlarıyla, ölçüleriyle.'),
   ('s06', 'STEP modeli döndürün; gerçek geometriden hammadde ve ağırlık.'),
   ('s07', 'Word, Excel ve PowerPoint, Office olmadan açılır.'),
   ('s09', 'Kurallar, koyu tema; Türkçe ve İngilizce arayüz.'),
   ('s10', 'Kurulum yok, telemetri yok. Ücretsiz ve açık kaynak, GitHub\'da.'),
  ]},
 'en': {
  'voices': ['en-US-AndrewNeural'], 'rate': '+6%',
  'lines': [
   ('s01', 'MailStudio for TRMachinist: the open-source AI email client for engineers and machinists.'),
   ('s02', 'A classic Outlook layout. Mail and attachments stay on your PC, and open offline.'),
   ('s03', 'One click for a summary or a reply draft. Local with Ollama, or with your own Claude, Codex or Gemini.'),
   ('s11', 'New: skills. They read the mail and its attachments, analyse a quotation, and update your Excel plan.'),
   ('s13', 'Connect Claude, Codex or Gemini in one click. Agents can read and draft, but never send mail.'),
   ('s04', 'PDF, DXF and DWG drawings open razor sharp, with layers and measuring.'),
   ('s06', 'Spin STEP models, and get raw stock and weight from the true geometry.'),
   ('s07', 'Word, Excel and PowerPoint open without Microsoft Office.'),
   ('s09', 'Rules, dark mode, and an English or Turkish interface.'),
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
        cfg = SCRIPT[lang]; out = ROOT / f'voice2-{lang}'; out.mkdir(exist_ok=True)
        man = []
        for sid, text in cfg['lines']:
            p = out / f'{sid}.mp3'
            v = await say(text, cfg['voices'], cfg['rate'], p)
            man.append({'id': sid, 'text': text.replace('Meyl Stüdyo for Ti Ar Maşinist', 'MailStudio for TRMachinist').replace('Meyl Stüdyo', 'MailStudio'), 'voice': v, 'duration': round(dur(p), 3)})
            print(lang, sid, v, man[-1]['duration'], flush=True)
        (out / 'manifest.json').write_text(json.dumps(man, ensure_ascii=False, indent=1), encoding='utf-8')
        print(lang, 'total', round(sum(m['duration'] for m in man), 2))

asyncio.run(main())
