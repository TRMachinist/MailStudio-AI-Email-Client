"""Turns recorded frame folders (frames.txt: file, ms) into constant-30fps MP4 clips."""
import os, subprocess, sys
rec, out = sys.argv[1], sys.argv[2]
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
    subprocess.run(['ffmpeg', '-y', '-v', 'error', '-f', 'concat', '-safe', '0', '-i', lst,
                    '-vf', 'fps=30,pad=ceil(iw/2)*2:ceil(ih/2)*2', '-c:v', 'libx264', '-preset', 'slow', '-crf', '15',
                    '-pix_fmt', 'yuv420p', dst], check=True)
    print(scene, len(rows), 'frames', round((rows[-1][1] - rows[0][1]) / 1000, 2), 's')
