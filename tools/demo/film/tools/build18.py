"""Builds timelines and soundtracks for 1.8 Main and New showcase films."""
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

# Main film plan (120-150s)
PLAN_MAIN = {
 's02': [('c01-inbox', 1, cam((0, .5, .5, 1), (.35, .4, .45, 1.22), (1, .42, .42, 1.3))),
         ('clips18/c39-inbox', 1, cam((0, .5, .5, 1), (.4, .25, .4, 1.25), (1, .5, .5, 1.1)))],
 's03': [('c02-ai', 1, cam((0, .5, .5, 1), (.12, .5, .5, 1), (.22, .88, .45, 1.55), (.62, .88, .55, 1.55), (.74, .62, .45, 1.3), (1, .62, .45, 1.3)))],
 's31': [('clips18/c31-mail', 0.7, cam((0, .5, .5, 1), (1, .3, .45, 1.25))),
         ('clips18/c31a-choose', 0.8, cam((0, .5, .5, 1), (1, .5, .45, 1.05))),
         ('clips18/c31-revdiff', 1.4, cam((0, .5, .5, 1), (.4, .45, .35, 1.25), (1, .6, .6, 1.15))),
         ('clips18/c31b-reply', 0.8, cam((0, .5, .5, 1), (1, .62, .45, 1.3)))],
 's32': [('clips18/c32-step', 1, cam((0, .5, .5, 1), (.5, .45, .35, 1.3), (1, .5, .4, 1.25)))],
 's33': [('clips18/c33-quotes', 1, cam((0, .5, .5, 1), (.4, .55, .3, 1.2), (1, .45, .35, 1.25)))],
 's34': [('clips18/c34-rules', 1, cam((0, .5, .5, 1), (1, .55, .55, 1.15))),
         ('clips18/c35b-missing', 0.8, cam((0, .5, .5, 1), (1, .5, .5, 1.05)))],
 's35': [('clips18/c35-undo', 1, cam((0, .5, .5, 1), (.4, .4, .8, 1.5), (1, .4, .8, 1.5))),
         ('clips18/c36-outbox', 0.8, cam((0, .5, .5, 1), (1, .5, .4, 1.1)))],
 's36': [('clips18/c37-followup', 1, cam((0, .5, .5, 1), (1, .45, .35, 1.2))),
         ('clips18/c38-snooze', 1, cam((0, .5, .5, 1), (.5, .35, .3, 1.25), (1, .35, .3, 1.2)))],
 's37': [('clips18/c40-calendar', 1, cam((0, .5, .5, 1), (1, .55, .45, 1.15))),
         ('clips18/c40b-event', 0.8, cam((0, .5, .5, 1), (1, .5, .5, 1.05)))],
 's11': [('clips16/c21-skill@3.2', 1, cam((0, .5, .5, 1), (.2, .3, .3, 1.25), (.45, .86, .45, 1.45), (1, .86, .62, 1.6)))],
 's12': [('clips16/c22-excel-a', 1.0, cam((0, .8, .45, 1.4), (1, .8, .45, 1.45))), ('clips16/c22-excel', 1, cam((0, .5, .5, 1), (.5, .35, .3, 1.35), (1, .6, .75, 1.25)))],
 's14': [('clips16/c23-skills@2', 1, cam((0, .5, .5, 1), (.18, .2, .3, 1.5), (.3, .2, .8, 1.7), (.62, .2, .8, 1.7), (.78, .5, .55, 1.15), (1, .35, .9, 1.4)))],
 's13': [('clips16/c24-agents', 1, cam((0, .5, .5, 1), (.3, .5, .3, 1.02), (.65, .5, .5, 1.02), (.82, .3, .93, 1.5), (1, .3, .93, 1.5)))],
 's04': [('c03-pdf-a', 0.9, cam((0, .5, .2, 1.4), (1, .5, .2, 1.5))), ('c03-pdf', 1, cam((0, .5, .5, 1), (1, .5, .5, 1.05)))],
 's05': [('c04-dxf-a', 0.9, cam((0, .55, .2, 1.4), (1, .55, .2, 1.5))), ('c04-dxf', 1, cam((0, .1, .3, 1.5), (.2, .1, .3, 1.5), (.4, .5, .5, 1), (1, .5, .5, 1)))],
 's06': [('c05-step-a', 0.9, cam((0, .4, .2, 1.4), (1, .4, .2, 1.5))), ('c05-step', 1, cam((0, .4, .5, 1.15), (1, .4, .5, 1.3)))],
 's07': [('c06-excel', 1, cam((0, .3, .2, 1.6), (1, .32, .22, 1.7))), ('c07-word', 1, cam((0, .5, .25, 2.0), (1, .5, .28, 2.1))), ('c08-ppt', 1, cam((0, .45, .5, 1.1), (1, .45, .5, 1.2)))],
 's08': [('c09-cnc', 1, cam((0, .2, .2, 1.7), (1, .2, .25, 1.8)))],
 's09': [('c10-rules', 1, cam((0, .5, .5, 1), (1, .5, .45, 1.08))), ('c11-dark', 1.1, cam((0, .5, .5, 1), (1, .5, .5, 1.1)))],
}

# New in 1.8 plan (45-60s)
PLAN_NEW = {
 'k02': [('clips18/c31a-choose', 0.7, cam((0, .5, .5, 1), (1, .5, .45, 1.05))),
         ('clips18/c31-revdiff', 1.4, cam((0, .5, .5, 1), (.4, .45, .35, 1.25), (1, .6, .6, 1.15))),
         ('clips18/c31b-reply', 0.7, cam((0, .5, .5, 1), (1, .62, .45, 1.3)))],
 'k03': [('clips18/c32-step', 1, cam((0, .5, .5, 1), (.5, .45, .35, 1.3), (1, .5, .4, 1.25)))],
 'k04': [('clips18/c33-quotes', 1, cam((0, .5, .5, 1), (.4, .55, .3, 1.2), (1, .45, .35, 1.25)))],
 'k05': [('clips18/c34-rules', 1, cam((0, .5, .5, 1), (1, .55, .55, 1.15))),
         ('clips18/c35b-missing', 0.8, cam((0, .5, .5, 1), (1, .5, .5, 1.05)))],
 'k06': [('clips18/c35-undo', 1, cam((0, .5, .5, 1), (.4, .4, .8, 1.5), (1, .4, .8, 1.5))),
         ('clips18/c36-outbox', 0.8, cam((0, .5, .5, 1), (1, .5, .4, 1.1)))],
 'k07': [('clips18/c37-followup', 1, cam((0, .5, .5, 1), (1, .45, .35, 1.2))),
         ('clips18/c38-snooze', 1, cam((0, .5, .5, 1), (.5, .35, .3, 1.25), (1, .35, .3, 1.2)))],
 'k08': [('clips18/c39-inbox', 1, cam((0, .5, .5, 1), (.4, .25, .4, 1.25), (1, .5, .5, 1.1)))],
 'k09': [('clips18/c40-calendar', 1, cam((0, .5, .5, 1), (1, .55, .45, 1.15))),
         ('clips18/c40b-event', 0.8, cam((0, .5, .5, 1), (1, .5, .5, 1.05)))],
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

def build_timeline(lang, voice_dir, plan_map, out_json, out_wav, is_new=False):
    man = json.loads((PUB / voice_dir / lang / 'manifest.json').read_text(encoding='utf-8'))
    scenes, t = [], 0
    intro_id = 'k01' if is_new else 's01'
    outro_id = 'k10' if is_new else 's10'

    for m in man:
        sid = m['id']; vd = m['duration']
        lead = 12 if sid == intro_id else 6
        tail = 55 if sid == outro_id else 12
        dur = lead + math.ceil(vd * FPS) + tail
        voice_rel = f'{voice_dir}/{lang}/{sid}.mp3'
        sc = dict(id=sid, **{'from': t}, dur=dur, voiceFrom=t + lead, voiceFrames=math.ceil(vd * FPS) - 6, parts=parts(m['text']), segs=[], cam=[], voice=voice_rel)
        if sid == intro_id:
            sc['still'] = f'stills/intro18-{lang}.jpg' if is_new else f'stills/intro-{lang}.jpg'
            sc['cam'] = cam((0, .5, .5, 1), (1, .45, .45, 1.12))
        elif sid in plan_map:
            plan = plan_map[sid]
            fixed = sum(int(sh * FPS) for c, sh, _ in plan if c.endswith('-a'))
            rest = dur - fixed; wsum = sum(sh for c, sh, _ in plan if not c.endswith('-a'))
            pos = 0
            for i, (clip, sh, cm) in enumerate(plan):
                clip, _, skip = clip.partition('@'); skip = float(skip or 0)
                if clip.startswith('clips18/'):
                    rel = f'clips18/{lang}/{clip[8:]}.mp4'
                elif clip.startswith('clips16/'):
                    rel = f'clips16/{lang}/{clip[8:]}.mp4'
                else:
                    rel = f'clips/{lang}/{clip}.mp4'
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
        # Main film
        build_timeline(lang, 'voice18_main', PLAN_MAIN, Path(f'timeline-{lang}.json'), Path(f'soundtrack-{lang}.wav'), is_new=False)
        # New 1.8 film
        build_timeline(lang, 'voice18_new', PLAN_NEW, Path(f'timelinenew18-{lang}.json'), Path(f'soundtracknew18-{lang}.wav'), is_new=True)

if __name__ == '__main__':
    main()
