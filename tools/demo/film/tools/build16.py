"""1.6 film: builds timeline16-<lang>.json and soundtrack16-<lang>.wav (clips16/, voice16/)."""
import json, math, subprocess, sys, wave
from pathlib import Path
import numpy as np

ROOT = Path(__file__).resolve().parent.parent
PUB = ROOT / 'public'
FPS, SR = 30, 48000

def probe(p):
    r = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=width,height:format=duration', '-of', 'json', str(p)], capture_output=True, text=True, check=True)
    j = json.loads(r.stdout); s = j['streams'][0]
    return float(j['format']['duration']), s['width'], s['height']

def cam(*ks): return [dict(t=k[0], cx=k[1], cy=k[2], z=k[3]) for k in ks]

# scene -> list of (clip, share or fixed seconds, camera)
PLAN = {
 'k02': [('c21-skill', 1, cam((0, .5, .5, 1), (.18, .3, .3, 1.25), (.42, .86, .45, 1.45), (1, .86, .62, 1.6)))],
 'k03': [('c22-excel-a', 1.2, cam((0, .8, .45, 1.4), (1, .8, .45, 1.45))), ('c22-excel', 1, cam((0, .5, .5, 1), (.5, .35, .3, 1.35), (1, .6, .75, 1.25)))],
 'k04': [('c23-skills', 1, cam((0, .5, .5, 1), (.18, .2, .3, 1.5), (.3, .2, .8, 1.7), (.62, .2, .8, 1.7), (.78, .5, .55, 1.15), (1, .35, .9, 1.4)))],
 'k05': [('c24-agents', 1, cam((0, .5, .5, 1), (.3, .5, .3, 1.02), (.65, .5, .5, 1.02), (.82, .3, .93, 1.5), (1, .3, .93, 1.5)))],
}
MAX_RATE = 2.3   # a scene is stretched (silence after the voice) rather than playing the recording faster than this
SPLIT = {  # subtitles split points
 'en': None, 'tr': None,
}

def parts(text):
    import re
    bits = [b.strip() for b in re.split(r'(?<=[.;!?])\s+', text) if b.strip()]
    out = []
    for b in bits:
        if len(b) > 62 and ',' in b:
            i = b.rfind(',', 0, len(b) // 2 + 12)
            if i > 10: out += [b[:i + 1], b[i + 1:].strip()]; continue
        out.append(b)
    return out

def build(lang):
    man = json.loads((PUB / 'voice16' / lang / 'manifest.json').read_text(encoding='utf-8'))
    scenes, t = [], 0
    for m in man:
        sid = m['id']; vd = m['duration']
        lead = 10 if sid == 'k01' else 5
        tail = 45 if sid == 'k10' else 9
        dur = lead + math.ceil(vd * FPS) + tail
        if sid in PLAN:
            nat_total = sum(probe(PUB / 'clips16' / lang / f'{c}.mp4')[0] for c, sh, _ in PLAN[sid] if not c.endswith('-a'))
            fixed_f = sum(int(sh * FPS) for c, sh, _ in PLAN[sid] if c.endswith('-a'))
            dur = max(dur, fixed_f + math.ceil(nat_total / MAX_RATE * FPS))
        sc = dict(id=sid, **{'from': t}, dur=dur, voiceFrom=t + lead, voiceFrames=math.ceil(vd * FPS) - 6, parts=parts(m['text']), segs=[], cam=[], voice=f'voice16/{lang}/{sid}.mp3')
        if sid == 'k01':
            sc['still'] = f'stills/intro16-{lang}.jpg'; sc['cam'] = cam((0, .5, .5, 1), (1, .45, .45, 1.12))
        elif sid in PLAN:
            plan = PLAN[sid]
            fixed = sum(int(sh * FPS) for c, sh, _ in plan if c.endswith('-a'))
            rest = dur - fixed; wsum = sum(sh for c, sh, _ in plan if not c.endswith('-a'))
            pos = 0
            for i, (clip, sh, cm) in enumerate(plan):
                src = PUB / 'clips16' / lang / f'{clip}.mp4'
                nat, w, h = probe(src)
                sd = int(sh * FPS) if clip.endswith('-a') else (dur - pos if i == len(plan) - 1 else int(rest * sh / wsum))
                start = 0
                if clip.endswith('-a'):  # just the click: last part of the clip
                    rate = 1.0; start = max(0, int((nat - sd / FPS) * FPS))
                else:
                    rate = max(0.6, nat / (sd / FPS) * 0.985)
                sc['segs'].append({'src': f'clips16/{lang}/{clip}.mp4', 'from': pos, 'dur': sd, 'rate': round(rate, 4), 'start': start, 'w': w, 'h': h, 'cam': cm})
                pos += sd
        scenes.append(sc); t += dur
    total = t
    # ---- audio: narration + synthesized pad, ducked, normalized later by ffmpeg
    n = int(total / FPS * SR) + SR
    voice = np.zeros(n, np.float32)
    for sc in scenes:
        raw = subprocess.run(['ffmpeg', '-v', 'error', '-i', str(PUB / sc['voice']), '-f', 'f32le', '-ac', '1', '-ar', str(SR), '-'], capture_output=True, check=True).stdout
        a = np.frombuffer(raw, np.float32); s0 = int(sc['voiceFrom'] / FPS * SR)
        voice[s0:s0 + len(a)] += a[:max(0, n - s0)]
    tt = np.arange(n) / SR
    chords = [(57, 60, 64), (53, 57, 60), (48, 52, 55), (55, 59, 62)]  # Am F C G
    music = np.zeros(n, np.float32); bar = 4.0
    for i in range(int(n / SR / bar) + 1):
        a, b = int(i * bar * SR), min(n, int((i + 1) * bar * SR + SR))
        if a >= n: break
        seg = tt[a:b] - i * bar
        env = np.minimum(1, seg / 1.2) * np.exp(-np.maximum(0, seg - bar) * 3)
        for k, note in enumerate(chords[i % 4]):
            fr = 440 * 2 ** ((note - 69) / 12)
            for det in (-0.6, 0.6):
                music[a:b] += (0.05 / (1 + k * 0.3)) * env * np.sin(2 * np.pi * (fr + det) * seg).astype(np.float32)
        bass = 440 * 2 ** ((chords[i % 4][0] - 81) / 12)
        music[a:b] += 0.05 * env * np.sin(2 * np.pi * bass * seg)
        # soft pulse
        for q in range(8):
            p0 = a + int(q * bar / 8 * SR); p1 = min(b, p0 + int(0.12 * SR))
            if p0 < n:
                e = np.exp(-np.arange(p1 - p0) / SR * 30)
                music[p0:p1] += 0.012 * e * np.sin(2 * np.pi * 880 * 2 ** ((chords[i % 4][q % 3] - 69 + 12) / 12 - 1) * np.arange(p1 - p0) / SR)
    env_v = np.convolve(np.abs(voice), np.ones(4800) / 4800, mode='same')
    duck = 1 - 0.55 * np.clip(env_v * 12, 0, 1)
    fade = np.minimum(1, tt / 1.5) * np.minimum(1, np.maximum(0, (total / FPS - tt)) / 2.0)
    mix = voice + music * duck * fade * 0.9
    mix = np.clip(mix / max(1e-6, np.abs(mix).max()) * 0.9, -1, 1)
    tmp = PUB / f'_mix16-{lang}.wav'
    with wave.open(str(tmp), 'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR); w.writeframes((mix * 32767).astype('<i2').tobytes())
    out = PUB / f'soundtrack16-{lang}.wav'
    subprocess.run(['ffmpeg', '-y', '-v', 'error', '-i', str(tmp), '-af', 'loudnorm=I=-14:TP=-1.5:LRA=11', '-ar', str(SR), '-ac', '2', str(out)], check=True)
    tmp.unlink()
    tl = {'totalFrames': total, 'audio': f'soundtrack16-{lang}.wav', 'scenes': scenes}
    (ROOT / 'src' / f'timeline16-{lang}.json').write_text(json.dumps(tl, ensure_ascii=False, indent=1), encoding='utf-8')
    print(lang, 'frames', total, 'seconds', round(total / FPS, 2))

for lang in (sys.argv[1:] or ['en', 'tr']): build(lang)
