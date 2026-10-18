"""Narration for the 90-second main film (1.6). Only the public product script below is sent to edge-tts."""
import asyncio, json, subprocess, sys
from pathlib import Path
import edge_tts

ROOT = Path(__file__).resolve().parent
SCRIPT = {
 'tr': {
  'voices': ['tr-TR-EmelNeural'], 'rate': '+8%',
  'lines': [
   ('s01', 'Meyl Stüdyo for Ti Ar Maşinist: imalatçılar için yapay zekâlı, açık kaynak mail programı.'),
   ('s02', 'Gmail, Microsoft 365 ve IMAP için klasik Outlook düzeni. Mailler ve ekler bilgisayarınızda kalır, çevrimdışı da açılır.'),
   ('s03', 'Tek tıkla özet ve cevap taslağı, doğrudan cevaba yazılır. Ollama ile tamamen yerel, ya da kendi Claude, Codex veya Gemini aboneliğinizle.'),
   ('s11', 'Yeni: skill\'ler. Skill maili ve eklerini okur; burada PDF ve DXF resme bakıp teklif isteğini saniyeler içinde analiz ediyor.'),
   ('s12', 'Excel skill\'i teslim planını günceller. Değişen her hücre önce gösterilir, yeni bir kopya kaydedilir; ek dosyası asla değişmez.'),
   ('s14', 'Kendi kuralınızı düz bir cümleyle anlatın, skill\'i yapay zekâ yazsın. Skill\'ler, Claude ve Codex ile aynı biçimde düz SKILL.md dosyalarıdır.'),
   ('s13', 'Claude, Codex, Gemini, Cursor ya da VS Code\'a tek tıkla bağlanın. Okuma, taslak ya da düzenleme yetkisini siz seçersiniz; hiçbir ajan mail gönderemez.'),
   ('s04', 'PDF teknik resimler jilet gibi açılır. Yakınlaştırın, işaretleyin, metni düzeltin, OCR yapın ya da yapay zekâya sorun.'),
   ('s05', 'DXF ve DWG çizimler katmanları, ölçüleri ve yakalamalı ölçümle açılır.'),
   ('s06', 'STEP modeli döndürün, gerçek geometriden ölçün; hammadde ve ağırlığı hesaplayın.'),
   ('s07', 'Word, Excel ve PowerPoint, Microsoft Office olmadan açılır ve düzenlenir.'),
   ('s08', 'CNC programları renkli, takım listesiyle.'),
   ('s09', 'Outlook gibi koşullu biçimlendirme, koyu tema; Türkçe ve İngilizce arayüz.'),
   ('s10', 'Kurulum yok, telemetri yok. Ücretsiz ve açık kaynak, GitHub\'da.'),
  ]},
 'en': {
  'voices': ['en-US-AndrewNeural'], 'rate': '+2%',
  'lines': [
   ('s01', 'MailStudio for TRMachinist: the open-source AI email client for engineers and machinists.'),
   ('s02', 'A classic Outlook layout for Gmail, Microsoft 365 and IMAP. Your mail and attachments stay on your PC, and open offline.'),
   ('s03', 'One click for a summary or a reply draft, written straight into the reply. Fully local with Ollama, or with your own Claude, Codex or Gemini.'),
   ('s11', 'New in 1.6: skills. A skill reads the mail and its attachments, here a PDF and a DXF drawing, and analyses the quotation request in seconds.'),
   ('s12', 'An Excel skill updates the delivery plan. Every changed cell is shown first, and a new copy is saved; the attachment never changes.'),
   ('s14', 'Describe your own rule in plain words, and the AI writes the skill. Skills are plain SKILL.md files, the same format as Claude and Codex skills.'),
   ('s13', 'Connect Claude, Codex, Gemini, Cursor or VS Code in one click. You choose read, draft or organize, and no agent can ever send mail.'),
   ('s04', 'PDF drawings open razor sharp. Zoom in, mark them up, fix text, run OCR, or ask the AI about a page.'),
   ('s05', 'DXF and DWG drawings open with layers, dimensions and snap measuring.'),
   ('s06', 'Spin STEP models, measure the true geometry, and get the raw stock and weight.'),
   ('s07', 'Word, Excel and PowerPoint open and edit, with no Microsoft Office needed.'),
   ('s08', 'CNC programs are highlighted, with a tool list.'),
   ('s09', 'Conditional formatting like Outlook, dark mode, and an English or Turkish interface.'),
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
