"""Generates narration audio for MailStudio 1.8 LinkedIn Product Film (115-125s, en-US-AndrewNeural)."""
import asyncio, json, os, subprocess, sys
from pathlib import Path
import edge_tts

ROOT = Path(__file__).resolve().parent
PUB = ROOT / 'film' / 'public'
OUT_VOICE = PUB / 'voice_linkedin'

CHAPTERS = [
    {
        'id': 'L01',
        'en': 'MailStudio. An open-source email client built for engineers and machinists.',
        'tr': 'MailStudio. Mühendisler ve imalatçılar için açık kaynak e-posta programı.',
        'label': 'MailStudio 1.8',
    },
    {
        'id': 'L02',
        'en': 'Classic Outlook feel. Gmail, Microsoft 365 or any IMAP account – and it works offline.',
        'tr': 'Klasik Outlook hissi. Gmail, Microsoft 365 ya da IMAP – çevrimdışı da çalışır.',
        'label': 'Inbox & Offline',
    },
    {
        'id': 'L03',
        'en': 'One click summarizes the mail and drafts the reply. Local with Ollama, or your own Claude, Codex or Gemini.',
        'tr': 'Tek tıkla özet ve cevap taslağı. Ollama ile tamamen yerel ya da kendi Claude, Codex, Gemini aboneliğinle.',
        'label': 'AI Summarize & Reply',
    },
    {
        'id': 'L04',
        'en': 'Technical drawings open right in the mail. Zoom, mark up, read the title block.',
        'tr': 'Teknik resimler mailin içinde açılır. Yakınlaştır, işaretle, anteti oku.',
        'label': 'PDF Drawings',
    },
    {
        'id': 'L05',
        'en': 'DXF and DWG with layers and real measuring.',
        'tr': 'Katmanlı DXF ve DWG, gerçek ölçüm.',
        'label': 'CAD Viewer',
    },
    {
        'id': 'L06',
        'en': 'STEP models from the real B-rep geometry – size, raw stock and weight.',
        'tr': 'STEP modeli gerçek geometriden – ölçü, ham malzeme ve ağırlık.',
        'label': 'STEP 3D Models',
    },
    {
        'id': 'L07',
        'en': 'New in 1.8: it finds the previous revision in the customer\'s mails and lists exactly what changed.',
        'tr': '1.8\'de yeni: müşterinin maillerinden eski revizyonu bulur, neyin değiştiğini tek tek listeler.',
        'label': 'Revision Compare',
    },
    {
        'id': 'L08',
        'en': 'Excel, Word, PowerPoint and G-code – no Office needed.',
        'tr': 'Excel, Word, PowerPoint ve G-kodu – Office gerekmez.',
        'label': 'Office & CNC Code',
    },
    {
        'id': 'L09',
        'en': 'Archives too: open a drawing straight from a ZIP or RAR.',
        'tr': 'Arşivler de: ZIP ya da RAR\'ın içindeki resmi doğrudan aç.',
        'label': 'ZIP & RAR Archives',
    },
    {
        'id': 'L10',
        'en': 'The quote desk turns requests into a list – part, quantity, due date, status.',
        'tr': 'Teklif masası talepleri listeye çevirir – parça, adet, termin, durum.',
        'label': 'Quote Desk',
    },
    {
        'id': 'L11',
        'en': 'It warns when you forget the attachment, lets you undo a send, and never sends without you.',
        'tr': 'Eki unutursan uyarır, göndermeyi geri almana izin verir, sensiz asla göndermez.',
        'label': 'Safe Sending',
    },
    {
        'id': 'L12',
        'en': 'Follow-ups, snooze, calendar, rules and dark mode – in English or Turkish.',
        'tr': 'Takip, erteleme, takvim, kurallar ve koyu tema – Türkçe ya da İngilizce.',
        'label': 'Productivity & Dark Mode',
    },
    {
        'id': 'L13',
        'en': 'Free, open source, no telemetry. Link in the comments.',
        'tr': 'Ücretsiz, açık kaynak, telemetri yok. Link yorumlarda.',
        'label': 'Free & Open Source',
    },
]

def dur(p):
    r = subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=duration', '-of', 'default=nw=1:nk=1', str(p)], capture_output=True, text=True, check=True)
    return float(r.stdout.strip())

async def generate():
    voice = 'en-US-AndrewNeural'
    rate = '+2%'
    OUT_VOICE.mkdir(parents=True, exist_ok=True)
    manifest = []
    total_voice_dur = 0.0

    for ch in CHAPTERS:
        cid = ch['id']
        text = ch['en']
        mp3 = OUT_VOICE / f'{cid}.mp3'
        wav = OUT_VOICE / f'{cid}.wav'
        print(f'Generating voice for {cid}: "{text}"...')
        comm = edge_tts.Communicate(text, voice, rate=rate)
        await comm.save(str(mp3))
        # Convert to 48kHz WAV
        subprocess.run(['ffmpeg', '-y', '-v', 'error', '-i', str(mp3), '-ar', '48000', '-ac', '1', str(wav)], check=True)
        d = dur(wav)
        total_voice_dur += d
        manifest.append({
            'id': cid,
            'text': text,
            'tr_sub': ch['tr'],
            'label': ch['label'],
            'duration': round(d, 3),
            'file': f'voice_linkedin/{cid}.mp3',
        })
        print(f'  -> {d:.2f}s')

    (OUT_VOICE / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'Done! Total speech duration: {total_voice_dur:.2f}s across {len(manifest)} chapters.')

if __name__ == '__main__':
    asyncio.run(generate())
