"""Narration for the MailStudio 1.9 film (≤ 90 s, every feature in order). Only this public text is sent to edge-tts.
python tools/demo/voice19.py  → film/public/voice19/<lang>/<id>.mp3 + manifest.json"""
import asyncio, json, subprocess
from pathlib import Path
import edge_tts

ROOT = Path(__file__).resolve().parent
PUB = ROOT / 'film' / 'public'

SCRIPT = {
 'tr': {'voice': 'tr-TR-EmelNeural', 'rate': '+14%', 'lines': [
  ('m01', 'Meyl Stüdyo bir nokta dokuz: imalat atölyeleri için yapay zekâlı mail programı.'),
  ('m11', 'Klasik Outlook düzeni; tek tıkla özet ve cevap taslağı.'),
  ('m12', 'PDF, DXF, STEP ve RAR ekleri başka program olmadan açılır.'),
  ('m13', 'Eski revizyonu bulur, değişenleri listeler, iki resmi kırmızı yeşil üst üste gösterir.'),
  ('m14', 'Gelen teklif talebi teklif masasına kendisi düşer; alındı cevabı taslak olarak hazırdır.'),
  ('m15', 'STEP modelden maliyeti hesaplayın, tek tıkla teklif PDF\'i yapın.'),
  ('m16', 'Parça kartı: bir parçanın bütün mailleri, revizyonları ve teklifleri.'),
  ('m17', 'NC programında takım yolu, çevrim süresi ve iki programın farkı.'),
  ('m18', 'PDF\'teki parça listesi doğrudan Excel\'e.'),
  ('m19', 'Kurallar, göndermeyi geri alma ve cevap takibi.'),
  ('m20', 'Takvim, görevler ve günün sabah özeti.'),
  ('m21', 'Maili anlamıyla bulun; arama bu bilgisayarda kalır.'),
  ('m22', 'Skill\'ler ve yapay zekâ ajanları okur, taslak yazar; asla mail göndermez.'),
  ('m10', 'Ücretsiz ve açık kaynak, GitHub\'da.'),
 ]},
 'en': {'voice': 'en-US-AndrewNeural', 'rate': '+10%', 'lines': [
  ('m01', 'MailStudio 1.9: the AI email client built for machine shops.'),
  ('m11', 'A classic Outlook layout, with one-click summaries and reply drafts.'),
  ('m12', 'PDF, DXF, STEP and RAR attachments open right inside.'),
  ('m13', 'It finds the earlier revision, lists the changes, and overlays both drawings in red and green.'),
  ('m14', 'New requests land on the quote desk by themselves, with an acknowledgement draft ready.'),
  ('m15', 'Estimate the cost from the STEP model, and make the quotation PDF in one click.'),
  ('m16', 'The part card: every mail, revision and quote of a part.'),
  ('m17', 'NC programs get a toolpath, a cycle time, and a comparison.'),
  ('m18', 'Parts lists in PDFs go straight to Excel.'),
  ('m19', 'Rules, undo send, and follow-up reminders.'),
  ('m20', 'Calendar, tasks, and a morning brief of your day.'),
  ('m21', 'Search mail by meaning, entirely on your computer.'),
  ('m22', 'Skills and AI agents read and draft, but never send.'),
  ('m10', 'Free and open source, on GitHub.'),
 ]},
}


def duration(p):
    r = subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', str(p)], capture_output=True, text=True, check=True)
    return float(r.stdout.strip())


async def main():
    for lang, cfg in SCRIPT.items():
        out = PUB / 'voice19' / lang; out.mkdir(parents=True, exist_ok=True)
        man = []
        for sid, text in cfg['lines']:
            f = out / f'{sid}.mp3'
            for attempt in range(4):
                try:
                    await edge_tts.Communicate(text, cfg['voice'], rate=cfg['rate']).save(str(f)); break
                except edge_tts.exceptions.NoAudioReceived:
                    print('retry', lang, sid); await asyncio.sleep(2 + attempt * 3)
            else:
                raise SystemExit(f'no audio for {lang} {sid}: {text}')
            man.append({'id': sid, 'text': text, 'duration': round(duration(f), 3)})
        (out / 'manifest.json').write_text(json.dumps(man, ensure_ascii=False, indent=1), encoding='utf-8')
        print(lang, round(sum(m['duration'] for m in man), 1), 's of speech')

asyncio.run(main())
