"""Turns recorded frame folders (frames.txt: file, ms) into constant-30fps MP4 clips."""
import os, struct, subprocess, sys
from collections import Counter
rec, out = sys.argv[1], sys.argv[2]


def jpeg_size(path):
    """(width, height) from the JPEG header, no imaging library needed."""
    with open(path, 'rb') as fh:
        data = fh.read(65536)
    i = 2
    while i + 9 < len(data):
        if data[i] != 0xFF: i += 1; continue
        marker = data[i + 1]
        if marker in (0xC0, 0xC1, 0xC2):
            h, w = struct.unpack('>HH', data[i + 5:i + 9])
            return w, h
        i += 2 + struct.unpack('>H', data[i + 2:i + 4])[0]
    return None
os.makedirs(out, exist_ok=True)
for scene in sorted(os.listdir(rec)):
    d = os.path.join(rec, scene); ft = os.path.join(d, 'frames.txt')
    if not os.path.isfile(ft): continue
    rows = [l.split('\t') for l in open(ft, encoding='utf-8').read().splitlines() if l.strip()]
    rows = [(f, int(ms)) for f, ms in rows if os.path.exists(os.path.join(d, f))]
    if len(rows) < 2: print('skip', scene); continue
    lst = os.path.join(d, 'concat.txt')
    with open(lst, 'w', encoding='utf-8') as fh:
        for i, (f, ms) in enumerate(rows):
            nxt = rows[i + 1][1] if i + 1 < len(rows) else ms + 66
            fh.write(f"file '{f}'\nduration {max(0.001, (nxt - ms) / 1000):.3f}\n")
        fh.write(f"file '{rows[-1][0]}'\n")
    dst = os.path.join(out, scene + '.mp4')
    # One size for the whole clip: a window that changes size during a scene would otherwise change the stream
    # resolution mid-clip (the film renderer stops with "Output changed").
    sizes = Counter(s for s in (jpeg_size(os.path.join(d, f)) for f, _ in rows) if s)
    w, h = sizes.most_common(1)[0][0] if sizes else (0, 0)
    w, h = w + w % 2, h + h % 2
    # JPEG frames are full-range (yuvj420p, "deprecated pixel format"): convert to standard limited-range yuv420p
    vf = (f'scale={w}:{h}:force_original_aspect_ratio=decrease:out_range=tv,pad={w}:{h}:0:0:white,setsar=1,fps=30,format=yuv420p' if w and h
          else 'fps=30,pad=ceil(iw/2)*2:ceil(ih/2)*2,scale=out_range=tv,format=yuv420p')
    if len(sizes) > 1: print(scene, 'frame sizes', dict(sizes), '->', (w, h))
    subprocess.run(['ffmpeg', '-y', '-v', 'error', '-f', 'concat', '-safe', '0', '-i', lst,
                    '-vf', vf, '-c:v', 'libx264', '-preset', 'slow', '-crf', '15',
                    '-pix_fmt', 'yuv420p', '-color_range', 'tv', dst], check=True)
    print(scene, len(rows), 'frames', round((rows[-1][1] - rows[0][1]) / 1000, 2), 's')
