"""Builds timelines and soundtracks for the 1.9 film (≤ 90 s)."""
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

# 1.9 film plan: every feature in order, ≤ 90 s. Clip names are the recorded scene folders (all four recordings).
# Camera keys: (t, cx, cy, zoom) in the clip's normalized coordinates; check them against the recorded frames.
PLAN = {
 'm11': [('c01-inbox', 1, cam((0, .5, .5, 1), (1, .42, .42, 1.2))), ('c02-ai', 1, cam((0, .5, .5, 1), (.3, .88, .45, 1.5), (1, .88, .55, 1.5)))],
 'm12': [('c03-pdf', 1, cam((0, .5, .5, 1), (1, .5, .5, 1.05))), ('c04-dxf', 1, cam((0, .5, .5, 1), (1, .5, .5, 1.05))),
         ('c05-step', 1, cam((0, .4, .5, 1.15), (1, .4, .5, 1.25))), ('c50-rar', 1, cam((0, .22, .14, 2.3), (1, .22, .14, 2.5)))],
 'm13': [('c31-revdiff', 1, cam((0, .5, .5, 1), (1, .45, .4, 1.15))), ('c53-visual', 1.3, cam((0, .5, .5, 1), (1, .5, .5, 1.05))), ('c53-step', 0.8, cam((0, .5, .5, 1), (1, .3, .42, 1.4)))],
 'm14': [('c58-rfq', 1, cam((0, .5, .5, 1), (.5, .3, .2, 1.8), (1, .3, .2, 1.9))), ('c58-ack@1.0', 1, cam((0, .35, .3, 1.6), (1, .35, .36, 1.7)))],
 'm15': [('c52-cost', 1.3, cam((0, .5, .3, 1.35), (1, .45, .3, 1.45))), ('c52-quotepdf', 1, cam((0, .5, .3, 2.1), (1, .5, .3, 2.3)))],
 'm16': [('c54-partcard', 1, cam((0, .35, .18, 1.7), (1, .4, .2, 1.8)))],
 'm17': [('c55-nc', 1.3, cam((0, .5, .5, 1), (1, .5, .5, 1.05))), ('c55-nccompare', 1, cam((0, .5, .5, 1), (1, .5, .5, 1.05)))],
 'm18': [('c57-tables', 1, cam((0, .3, .1, 2.0), (.7, .3, .1, 2.2), (1, .5, .5, 1)))],
 'm19': [('c34-rules', 1, cam((0, .5, .5, 1), (1, .55, .55, 1.1))), ('c35-undo', 1, cam((0, .5, .5, 1), (1, .4, .8, 1.4))), ('c37-followup', 1, cam((0, .5, .5, 1), (1, .45, .35, 1.15)))],
 'm20': [('c40-calendar', 1, cam((0, .5, .5, 1), (1, .55, .45, 1.1))), ('c51-brief', 1.2, cam((0, .5, .5, 1), (1, .5, .4, 1.05)))],
 'm21': [('c56-semantic', 1, cam((0, .3, .1, 1.6), (.45, .3, .15, 1.7), (1, .3, .3, 1.75)))],
 'm22': [('c21-skill@3.2', 1, cam((0, .5, .5, 1), (1, .86, .55, 1.45))), ('c24-agents', 1, cam((0, .5, .5, 1), (1, .3, .9, 1.4)))],
}

# Minimum seconds per scene so every clip is really visible (the voice starts the scene, pictures finish it)
MIN_SECONDS = {'m11': 6, 'm12': 8, 'm13': 7.5, 'm14': 6, 'm15': 6.5, 'm16': 5, 'm17': 6.5, 'm18': 5.5, 'm19': 7, 'm20': 6, 'm21': 5, 'm22': 6}

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

def build_timeline(lang, voice_dir, plan_map, out_json, out_wav, is_new=False):
    man = json.loads((PUB / voice_dir / lang / 'manifest.json').read_text(encoding='utf-8'))
    scenes, t = [], 0
    intro_id = 'm01'
    outro_id = 'm10'

    for m in man:
        sid = m['id']; vd = m['duration']
        lead = 12 if sid == intro_id else 6
        tail = 55 if sid == outro_id else 12
        dur = lead + math.ceil(vd * FPS) + tail
        dur = max(dur, int(MIN_SECONDS.get(sid, 0) * FPS))
        voice_rel = f'{voice_dir}/{lang}/{sid}.mp3'
        sc = dict(id=sid, **{'from': t}, dur=dur, voiceFrom=t + lead, voiceFrames=math.ceil(vd * FPS) - 6, parts=parts(m['text']), segs=[], cam=[], voice=voice_rel)
        if sid == intro_id:
            sc['still'] = f'stills/intro19-{lang}.jpg'
            sc['cam'] = cam((0, .5, .5, 1), (1, .45, .45, 1.12))
        elif sid in plan_map:
            plan = plan_map[sid]
            fixed = sum(int(sh * FPS) for c, sh, _ in plan if c.endswith('-a'))
            rest = dur - fixed; wsum = sum(sh for c, sh, _ in plan if not c.endswith('-a'))
            pos = 0
            for i, (clip, sh, cm) in enumerate(plan):
                clip, _, skip = clip.partition('@'); skip = float(skip or 0)
                rel = f'clips19/{lang}/{clip}.mp4'
                src = PUB / rel
                nat, w, h = probe(src); nat -= skip
                sd = int(sh * FPS) if clip.endswith('-a') else (dur - pos if i == len(plan) - 1 else int(rest * sh / wsum))
                start = 0
                if clip.endswith('-a'):
                    rate = 1.0; start = max(0, int((nat - sd / FPS) * FPS))
                else:
                    rate = max(0.35, nat / (sd / FPS) * 0.985); start = int(skip * FPS)
                sc['segs'].append({'src': rel, 'from': pos, 'dur': sd, 'rate': round(rate, 4), 'start': start, 'w': w, 'h': h, 'cam': cm})
                pos += sd
        scenes.append(sc); t += dur
    total = t

    # soundtrack: synthesized chords with ducking
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
    tmp = PUB / f'_mix_{out_wav.stem}.wav'
    with wave.open(str(tmp), 'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR); w.writeframes((mix * 32767).astype('<i2').tobytes())
    out_path = PUB / out_wav
    subprocess.run(['ffmpeg', '-y', '-v', 'error', '-i', str(tmp), '-af', 'loudnorm=I=-14:TP=-1.5:LRA=11', '-ar', str(SR), '-ac', '2', str(out_path)], check=True)
    tmp.unlink()

    tl = {'totalFrames': total, 'audio': out_wav.name, 'scenes': scenes}
    (ROOT / 'src' / out_json).write_text(json.dumps(tl, ensure_ascii=False, indent=1), encoding='utf-8')
    secs = round(total / FPS, 2)
    print(f'[{out_json.stem}] {lang}: {total} frames ({secs} s)')
    return secs

def main():
    for lang in ['en', 'tr']:
        secs = build_timeline(lang, 'voice19', PLAN, Path(f'timeline19-{lang}.json'), Path(f'soundtrack19-{lang}.wav'))
        if secs > 90: raise SystemExit(f'{lang}: {secs} s is longer than 90 s')

if __name__ == '__main__':
    main()
