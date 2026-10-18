"""Builds timeline-<lang>.json and soundtrack-<lang>.wav from the recorded clips and the narration."""
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

# scene -> list of (clip, share or fixed seconds, camera); clips16/... are the 1.6 recordings (skills, agents)
PLAN = {
 's02': [('c01-inbox', 1, cam((0, .5, .5, 1), (.35, .4, .45, 1.22), (1, .42, .42, 1.3)))],
 's03': [('c02-ai', 1, cam((0, .5, .5, 1), (.12, .5, .5, 1), (.22, .88, .45, 1.55), (.62, .88, .55, 1.55), (.74, .62, .45, 1.3), (1, .62, .45, 1.3)))],
 's11': [('clips16/c21-skill', 1.25, cam((0, .5, .5, 1), (.25, .86, .4, 1.4), (1, .86, .62, 1.6))), ('clips16/c22-excel', 0.75, cam((0, .5, .5, 1), (1, .35, .3, 1.35)))],
 's13': [('clips16/c24-agents', 1, cam((0, .5, .5, 1), (.3, .5, .3, 1.02), (.65, .5, .5, 1.02), (.82, .3, .93, 1.5), (1, .3, .93, 1.5)))],
 's04': [('c03-pdf', 1, cam((0, .5, .5, 1), (1, .5, .5, 1.1))), ('c04-dxf', 1, cam((0, .1, .3, 1.5), (.2, .1, .3, 1.5), (.4, .5, .5, 1), (1, .5, .5, 1)))],
 's06': [('c05-step', 1, cam((0, .4, .5, 1.15), (1, .4, .5, 1.3)))],
 's07': [('c06-excel', 1, cam((0, .3, .2, 1.6), (1, .32, .22, 1.7))), ('c07-word', 1, cam((0, .5, .25, 2.0), (1, .5, .28, 2.1))), ('c08-ppt', 1, cam((0, .45, .5, 1.1), (1, .45, .5, 1.2)))],
 's09': [('c10-rules', 1, cam((0, .5, .5, 1), (1, .5, .45, 1.08))), ('c11-dark', 1.1, cam((0, .5, .5, 1), (1, .5, .5, 1.1)))],
}
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
    man = json.loads((PUB / 'voice2' / lang / 'manifest.json').read_text(encoding='utf-8'))
    scenes, t = [], 0
    for m in man:
        sid = m['id']; vd = m['duration']
        lead = 10 if sid == 's01' else 4
        tail = 45 if sid == 's10' else 7
        dur = lead + math.ceil(vd * FPS) + tail
        sc = dict(id=sid, **{'from': t}, dur=dur, voiceFrom=t + lead, voiceFrames=math.ceil(vd * FPS) - 6, parts=parts(m['text']), segs=[], cam=[], voice=f'voice2/{lang}/{sid}.mp3')
        if sid == 's01':
            sc['still'] = f'stills/intro-{lang}.jpg'; sc['cam'] = cam((0, .5, .5, 1), (1, .45, .45, 1.12))
        elif sid in PLAN:
            plan = PLAN[sid]
            fixed = sum(int(sh * FPS) for c, sh, _ in plan if c.endswith('-a'))
            rest = dur - fixed; wsum = sum(sh for c, sh, _ in plan if not c.endswith('-a'))
            pos = 0
            for i, (clip, sh, cm) in enumerate(plan):
                rel = f'clips16/{lang}/{clip[8:]}.mp4' if clip.startswith('clips16/') else f'clips/{lang}/{clip}.mp4'
                src = PUB / rel
                nat, w, h = probe(src)
                sd = int(sh * FPS) if clip.endswith('-a') else (dur - pos if i == len(plan) - 1 else int(rest * sh / wsum))
                start = 0
                if clip.endswith('-a'):  # just the click: last part of the clip
                    rate = 1.0; start = max(0, int((nat - sd / FPS) * FPS))
                else:
                    rate = max(0.35, nat / (sd / FPS) * 0.985)
                sc['segs'].append({'src': rel, 'from': pos, 'dur': sd, 'rate': round(rate, 4), 'start': start, 'w': w, 'h': h, 'cam': cm})
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
    tmp = PUB / f'_mix-{lang}.wav'
    with wave.open(str(tmp), 'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR); w.writeframes((mix * 32767).astype('<i2').tobytes())
    out = PUB / f'soundtrack-{lang}.wav'
    subprocess.run(['ffmpeg', '-y', '-v', 'error', '-i', str(tmp), '-af', 'loudnorm=I=-14:TP=-1.5:LRA=11', '-ar', str(SR), '-ac', '2', str(out)], check=True)
    tmp.unlink()
    tl = {'totalFrames': total, 'audio': f'soundtrack-{lang}.wav', 'scenes': scenes}
    (ROOT / 'src' / f'timeline-{lang}.json').write_text(json.dumps(tl, ensure_ascii=False, indent=1), encoding='utf-8')
    print(lang, 'frames', total, 'seconds', round(total / FPS, 2))

for lang in (sys.argv[1:] or ['en', 'tr']): build(lang)
