"""Narration for the 1.6 film (skills + agents). Only the public product script below is sent to edge-tts."""
import asyncio, json, subprocess, sys
from pathlib import Path
import edge_tts

ROOT = Path(__file__).resolve().parent
SCRIPT = {
 'tr': {
  'voices': ['tr-TR-EmelNeural'], 'rate': '+10%',
  'lines': [
   ('k01', 'Meyl Stüdyo 1.6 ile yeni: skill\'ler ve tek tıkla yapay zekâ ajanı bağlantısı.'),
   ('k02', 'Skill\'ler maili ve eklerini okur; PDF ve DXF resme bakıp teklif isteğini saniyeler içinde analiz eder.'),
   ('k03', 'Excel skill\'i teslim planını günceller. Değişen her hücreyi siz kontrol edersiniz; yeni bir kopya kaydedilir.'),
   ('k04', 'Kuralınızı düz bir cümleyle anlatın, skill\'i yapay zekâ yazsın.'),
   ('k05', 'Claude, Codex, Gemini ya da Cursor\'a tek tıkla bağlanın. Yetkiyi siz seçersiniz; hiçbir ajan mail gönderemez.'),
   ('k10', 'Ücretsiz ve açık kaynak, GitHub\'da.'),
  ]},
 'en': {
  'voices': ['en-US-AndrewNeural'], 'rate': '+4%',
  'lines': [
   ('k01', 'New in MailStudio 1.6: skills, and one-click connection to your AI agent.'),
   ('k02', 'Skills read the mail and its attachments, like this PDF and DXF drawing, and analyse a quotation request in seconds.'),
   ('k03', 'An Excel skill updates the delivery plan. You check every changed cell, and a new copy is saved.'),
   ('k04', 'Describe a rule in plain words, and the AI writes the skill for you.'),
   ('k05', 'Connect Claude, Codex, Gemini or Cursor in one click. You choose the permission, and no agent can ever send mail.'),
   ('k10', 'Free and open source, on GitHub.'),
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
        cfg = SCRIPT[lang]; out = ROOT / f'voice16-{lang}'; out.mkdir(exist_ok=True)
        man = []
        for sid, text in cfg['lines']:
            p = out / f'{sid}.mp3'
            v = await say(text, cfg['voices'], cfg['rate'], p)
            man.append({'id': sid, 'text': text.replace('Meyl Stüdyo', 'MailStudio'), 'voice': v, 'duration': round(dur(p), 3)})
            print(lang, sid, v, man[-1]['duration'], flush=True)
        (out / 'manifest.json').write_text(json.dumps(man, ensure_ascii=False, indent=1), encoding='utf-8')
        print(lang, 'total', round(sum(m['duration'] for m in man), 2))

asyncio.run(main())
