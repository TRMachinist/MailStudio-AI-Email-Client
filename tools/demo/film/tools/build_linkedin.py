"""Builds timeline, ducked soundtrack, and SRT subtitles for MailStudio 1.8 LinkedIn film (120s)."""
import json, math, os, subprocess, sys, wave
from pathlib import Path
import numpy as np

ROOT = Path(__file__).resolve().parent.parent
PUB = ROOT / 'film' / 'public' if (ROOT / 'film').exists() else ROOT / 'public'
SRC = ROOT / 'src'
FPS, SR = 30, 48000

def probe(p):
    r = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=width,height:format=duration', '-of', 'json', str(p)], capture_output=True, text=True, check=True)
    j = json.loads(r.stdout)
    s = j['streams'][0]
    return float(j['format']['duration']), s['width'], s['height']

def cam(*ks):
    return [dict(t=k[0], cx=k[1], cy=k[2], z=k[3]) for k in ks]

# Segment mapping for the 13 LinkedIn chapters
# Clips are stored in public/linkedin/clips/
PLAN_LINKEDIN = {
    'L01': [('linkedin/clips/L01-intro', 1.0, cam((0, .5, .5, 1), (1, .48, .48, 1.08)))],
    'L02': [('linkedin/clips/L02-inbox', 1.0, cam((0, .5, .5, 1), (.4, .25, .4, 1.18), (1, .55, .45, 1.2)))],
    'L03': [('linkedin/clips/L03-ai', 1.0, cam((0, .5, .5, 1), (.3, .85, .45, 1.35), (.7, .85, .55, 1.35), (1, .6, .45, 1.2)))],
    'L04': [('linkedin/clips/L04-pdf-a', 0.8, cam((0, .5, .2, 1.2), (1, .5, .2, 1.25))),
            ('linkedin/clips/L04-pdf', 1.2, cam((0, .5, .5, 1), (.5, .55, .55, 1.3), (1, .5, .5, 1.08)))],
    'L05': [('linkedin/clips/L05-dxf-a', 0.8, cam((0, .5, .2, 1.2), (1, .5, .2, 1.25))),
            ('linkedin/clips/L05-dxf', 1.2, cam((0, .15, .35, 1.3), (.4, .5, .5, 1.1), (1, .5, .5, 1.15)))],
    'L06': [('linkedin/clips/L06-step-a', 0.8, cam((0, .45, .2, 1.2), (1, .45, .2, 1.25))),
            ('linkedin/clips/L06-step', 1.2, cam((0, .45, .5, 1.12), (.6, .45, .5, 1.2), (1, .8, .35, 1.28)))],
    'L07': [('linkedin/clips/L07a-choose', 0.7, cam((0, .5, .5, 1), (1, .5, .45, 1.05))),
            ('linkedin/clips/L07b-diff', 1.4, cam((0, .5, .6, 1.18), (1, .6, .6, 1.22))),
            ('linkedin/clips/L07c-reply', 0.8, cam((0, .55, .45, 1.18), (1, .55, .45, 1.22)))],
    'L08': [('linkedin/clips/L08a-excel', 1.0, cam((0, .35, .25, 1.3), (1, .35, .25, 1.35))),
            ('linkedin/clips/L08b-word', 1.0, cam((0, .48, .35, 1.3), (1, .48, .35, 1.35))),
            ('linkedin/clips/L08c-ppt', 1.0, cam((0, .35, .45, 1.18), (1, .35, .45, 1.22))),
            ('linkedin/clips/L08d-cnc', 1.0, cam((0, .3, .3, 1.3), (1, .3, .3, 1.35)))],
    'L09': [('linkedin/clips/L09a-archive', 1.0, cam((0, .4, .35, 1.18), (1, .4, .35, 1.22))),
            ('linkedin/clips/L09b-innerpdf', 1.1, cam((0, .5, .5, 1.08), (1, .5, .5, 1.12)))],
    'L10': [('linkedin/clips/L10-quotes', 1.0, cam((0, .5, .5, 1.08), (.4, .55, .3, 1.22), (1, .45, .4, 1.18)))],
    'L11': [('linkedin/clips/L11a-compose', 0.7, cam((0, .45, .4, 1.12), (1, .45, .4, 1.15))),
            ('linkedin/clips/L11b-missing', 0.8, cam((0, .5, .5, 1.04), (1, .5, .5, 1.04))),
            ('linkedin/clips/L11c-undo', 1.1, cam((0, .5, .55, 1.22), (1, .5, .55, 1.22))),
            ('linkedin/clips/L11d-outbox', 0.8, cam((0, .5, .45, 1.08), (1, .5, .45, 1.08)))],
    'L12': [('linkedin/clips/L12a-followup', 0.8, cam((0, .45, .35, 1.15), (1, .45, .35, 1.15))),
            ('linkedin/clips/L12b-snooze', 0.8, cam((0, .45, .4, 1.15), (1, .45, .4, 1.15))),
            ('linkedin/clips/L12c-calendar', 0.8, cam((0, .55, .45, 1.12), (1, .55, .45, 1.12))),
            ('linkedin/clips/L12d-rules', 0.8, cam((0, .4, .35, 1.12), (1, .4, .35, 1.12))),
            ('linkedin/clips/L12e-dark', 1.1, cam((0, .5, .5, 1.08), (1, .5, .5, 1.12)))],
    'L13': [('linkedin/clips/L13-end', 1.0, cam((0, .5, .5, 1.0), (1, .5, .5, 1.04)))],
}

# Target frame allocation to hit exactly ~120s (3600 frames at 30 fps)
# The 13 chapters durations in seconds:
# L01: 6s (180f)
# L02: 8s (240f)
# L03: 9s (270f)
# L04: 10s (300f)
# L05: 8s (240f)
# L06: 9s (270f)
# L07: 12s (360f)
# L08: 8s (240f)
# L09: 8s (240f)
# L10: 10s (300f)
# L11: 12s (360f)
# L12: 12s (360f)
# L13: 8s (240f)
# Total = 120s (3600 frames)
TARGET_DURATIONS = {
    'L01': 180,
    'L02': 240,
    'L03': 270,
    'L04': 300,
    'L05': 240,
    'L06': 270,
    'L07': 360,
    'L08': 240,
    'L09': 240,
    'L10': 300,
    'L11': 360,
    'L12': 360,
    'L13': 240,
}

def format_srt_time(sec):
    h = int(sec // 3600)
    m = int((sec % 3600) // 60)
    s = int(sec % 60)
    ms = int(round((sec - int(sec)) * 1000))
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"

def write_srt(path, subs):
    lines = []
    for idx, (t0, t1, text) in enumerate(subs, 1):
        lines.append(str(idx))
        lines.append(f"{format_srt_time(t0)} --> {format_srt_time(t1)}")
        lines.append(text)
        lines.append("")
    Path(path).write_text("\n".join(lines), encoding="utf-8")

def build_timeline():
    man_path = PUB / 'voice_linkedin' / 'manifest.json'
    if not man_path.exists():
        print(f"Manifest not found: {man_path}")
        return
    man = json.loads(man_path.read_text(encoding='utf-8'))

    scenes, t = [], 0
    tr_subs = []
    en_subs = []

    for m in man:
        sid = m['id']
        dur = TARGET_DURATIONS.get(sid, 270)
        vd = m['duration']
        voice_frames = math.ceil(vd * FPS)
        # Position voice centered within scene duration
        lead = max(6, int((dur - voice_frames) / 2))
        voice_from = t + lead
        voice_rel = f"voice_linkedin/{sid}.mp3"

        # Subtitles timing (matches voice with small padding)
        sub_start = max(0.0, (voice_from - 4) / FPS)
        sub_end = min((t + dur) / FPS, sub_start + vd + 0.6)
        tr_subs.append((sub_start, sub_end, m['tr_sub']))
        en_subs.append((sub_start, sub_end, m['text']))

        sc = {
            'id': sid,
            'label': m['label'],
            'from': t,
            'dur': dur,
            'voiceFrom': voice_from,
            'voiceFrames': voice_frames,
            'voice': voice_rel,
            'en_text': m['text'],
            'tr_sub': m['tr_sub'],
            'segs': [],
            'cam': [],
        }

        if sid in PLAN_LINKEDIN:
            plan = PLAN_LINKEDIN[sid]
            fixed = sum(int(sh * FPS) for c, sh, _ in plan if c.endswith('-a'))
            rest = dur - fixed
            wsum = sum(sh for c, sh, _ in plan if not c.endswith('-a'))
            pos = 0

            for i, (clip, sh, cm) in enumerate(plan):
                rel = f"{clip}.mp4"
                src = PUB / rel
                if src.exists():
                    nat, w, h = probe(src)
                else:
                    nat, w, h = 10.0, 1920, 1080

                sd = int(sh * FPS) if clip.endswith('-a') else (dur - pos if i == len(plan) - 1 else int(rest * sh / wsum))
                if clip.endswith('-a'):
                    rate = 1.0
                    start = max(0, int((nat - sd / FPS) * FPS))
                else:
                    rate = max(0.35, min(2.5, nat / (sd / FPS) * 0.985))
                    start = 0

                sc['segs'].append({
                    'src': rel,
                    'from': pos,
                    'dur': sd,
                    'rate': round(rate, 4),
                    'start': start,
                    'w': w,
                    'h': h,
                    'cam': cm,
                })
                pos += sd

        scenes.append(sc)
        t += dur

    total = t
    total_sec = total / FPS
    print(f"Total video frames: {total} ({total_sec:.2f} s)")

    # Synthesize ducked background chords
    n = int(total / FPS * SR) + SR
    voice = np.zeros(n, np.float32)
    for sc in scenes:
        vpath = PUB / sc['voice']
        if vpath.exists():
            raw = subprocess.run(['ffmpeg', '-v', 'error', '-i', str(vpath), '-f', 'f32le', '-ac', '1', '-ar', str(SR), '-'], capture_output=True, check=True).stdout
            a = np.frombuffer(raw, np.float32)
            s0 = int(sc['voiceFrom'] / FPS * SR)
            voice[s0:s0 + len(a)] += a[:max(0, n - s0)]

    tt = np.arange(n) / SR
    chords = [(57, 60, 64), (53, 57, 60), (48, 52, 55), (55, 59, 62)]  # Am F C G
    music = np.zeros(n, np.float32)
    bar = 4.0
    for i in range(int(n / SR / bar) + 1):
        a_idx, b_idx = int(i * bar * SR), min(n, int((i + 1) * bar * SR + SR))
        if a_idx >= n:
            break
        seg = tt[a_idx:b_idx] - i * bar
        env = np.minimum(1, seg / 1.2) * np.exp(-np.maximum(0, seg - bar) * 3)
        for k, note in enumerate(chords[i % 4]):
            fr = 440 * 2 ** ((note - 69) / 12)
            for det in (-0.5, 0.5):
                music[a_idx:b_idx] += (0.045 / (1 + k * 0.3)) * env * np.sin(2 * np.pi * (fr + det) * seg).astype(np.float32)
        bass = 440 * 2 ** ((chords[i % 4][0] - 81) / 12)
        music[a_idx:b_idx] += 0.045 * env * np.sin(2 * np.pi * bass * seg)
        for q in range(8):
            p0 = a_idx + int(q * bar / 8 * SR)
            p1 = min(b_idx, p0 + int(0.12 * SR))
            if p0 < n:
                e = np.exp(-np.arange(p1 - p0) / SR * 30)
                music[p0:p1] += 0.010 * e * np.sin(2 * np.pi * 880 * 2 ** ((chords[i % 4][q % 3] - 69 + 12) / 12 - 1) * np.arange(p1 - p0) / SR)

    # Ducking under voice
    env_v = np.convolve(np.abs(voice), np.ones(4800) / 4800, mode='same')
    duck = 1 - 0.60 * np.clip(env_v * 12, 0, 1)
    fade = np.minimum(1, tt / 1.8) * np.minimum(1, np.maximum(0, (total / FPS - tt)) / 2.5)
    mix = voice + music * duck * fade * 0.85
    mix = np.clip(mix / max(1e-6, np.abs(mix).max()) * 0.9, -1, 1)

    tmp = PUB / '_mix_linkedin.wav'
    with wave.open(str(tmp), 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((mix * 32767).astype('<i2').tobytes())

    out_wav = PUB / 'soundtrack-linkedin.wav'
    subprocess.run(['ffmpeg', '-y', '-v', 'error', '-i', str(tmp), '-af', 'loudnorm=I=-14:TP=-1.5:LRA=11', '-ar', str(SR), '-ac', '2', str(out_wav)], check=True)
    tmp.unlink(missing_ok=True)
    print(f"Soundtrack generated: {out_wav} (-14 LUFS)")

    tl = {
        'totalFrames': total,
        'durationSec': round(total_sec, 2),
        'fps': FPS,
        'audio': 'soundtrack-linkedin.wav',
        'scenes': scenes,
    }
    (SRC / 'timeline-linkedin.json').write_text(json.dumps(tl, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f"Timeline written: {SRC / 'timeline-linkedin.json'}")

    # Subtitles in target delivery dir
    deliv_dir = Path(r"D:\MailStudio\work\video\linkedin")
    deliv_dir.mkdir(parents=True, exist_ok=True)
    write_srt(deliv_dir / "MailStudio_1.8_LinkedIn_TR.srt", tr_subs)
    write_srt(deliv_dir / "MailStudio_1.8_LinkedIn_EN.srt", en_subs)
    print(f"Subtitles written to {deliv_dir}")

if __name__ == '__main__':
    build_timeline()
