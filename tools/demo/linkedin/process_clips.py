import os
import sys
import json
import struct
import subprocess
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from collections import Counter

RAW_DIR = r"D:\MailStudio\work\video\linkedin2\raw"
CLIPS_OUT = r"D:\MailStudio_GitHub\tools\demo\film\public\linkedin2\clips"
CHECKS_OUT = r"D:\MailStudio\work\video\linkedin2\checks"
TIMING_PATH = r"D:\MailStudio_GitHub\tools\demo\linkedin\timing.json"

os.makedirs(CLIPS_OUT, exist_ok=True)
os.makedirs(CHECKS_OUT, exist_ok=True)

with open(TIMING_PATH, "r", encoding="utf-8") as f:
    timing_data = json.load(f)

# Build map from scene/beat to expected durations
beat_timings = {}
chapter_timings = {}
for ch in timing_data["chapters"]:
    chapter_timings[ch["id"]] = ch
    for b in ch["beats"]:
        beat_timings[b["id"]] = b

def jpeg_size(path):
    with open(path, 'rb') as fh:
        data = fh.read(65536)
    i = 2
    while i + 9 < len(data):
        if data[i] != 0xFF:
            i += 1
            continue
        marker = data[i + 1]
        if marker in (0xC0, 0xC1, 0xC2):
            h, w = struct.unpack('>HH', data[i + 5:i + 9])
            return w, h
        i += 2 + struct.unpack('>H', data[i + 2:i + 4])[0]
    return None

scenes = sorted([d for d in os.listdir(RAW_DIR) if os.path.isdir(os.path.join(RAW_DIR, d))])
print(f"Found {len(scenes)} scenes in raw directory: {scenes}")

fps_report_rows = []
checks_summary = []

for scene in scenes:
    d = os.path.join(RAW_DIR, scene)
    ft = os.path.join(d, 'frames.txt')
    ev_path = os.path.join(d, 'events.json')
    if not os.path.isfile(ft):
        print(f"Skipping {scene} (no frames.txt)")
        continue

    rows = [l.split('\t') for l in open(ft, encoding='utf-8').read().splitlines() if l.strip()]
    rows = [(f, int(ms)) for f, ms in rows if os.path.exists(os.path.join(d, f))]
    if len(rows) < 2:
        print(f"Skipping {scene} (less than 2 frames: {len(rows)})")
        continue

    # 1. Measure frame intervals
    timestamps = [ms for _, ms in rows]
    intervals = [timestamps[i+1] - timestamps[i] for i in range(len(timestamps)-1)]
    dur_ms = timestamps[-1] - timestamps[0]
    dur_s = dur_ms / 1000.0
    med_interval = float(np.median(intervals)) if intervals else 0.0
    p90_interval = float(np.percentile(intervals, 90)) if intervals else 0.0
    meas_fps = round(1000.0 / med_interval, 1) if med_interval > 0 else 0.0
    avg_fps = round(len(rows) / dur_s, 1) if dur_s > 0 else 0.0

    fps_report_rows.append({
        "scene": scene,
        "frames": len(rows),
        "dur_s": round(dur_s, 2),
        "avg_fps": avg_fps,
        "med_interval_ms": round(med_interval, 1),
        "p90_interval_ms": round(p90_interval, 1),
        "meas_fps": meas_fps
    })

    # 2. Convert to MP4 clip
    lst = os.path.join(d, 'concat.txt')
    with open(lst, 'w', encoding='utf-8') as fh:
        for i, (f, ms) in enumerate(rows):
            nxt = rows[i + 1][1] if i + 1 < len(rows) else ms + 66
            fh.write(f"file '{f}'\nduration {max(0.001, (nxt - ms) / 1000):.3f}\n")
        fh.write(f"file '{rows[-1][0]}'\n")

    dst = os.path.join(CLIPS_OUT, scene + '.mp4')
    sizes = Counter(s for s in (jpeg_size(os.path.join(d, f)) for f, _ in rows) if s)
    w, h = sizes.most_common(1)[0][0] if sizes else (2880, 1620)
    w, h = w + w % 2, h + h % 2

    vf = (f'scale={w}:{h}:force_original_aspect_ratio=decrease:out_range=tv,pad={w}:{h}:0:0:white,setsar=1,fps=30,format=yuv420p'
          if w and h else 'fps=30,pad=ceil(iw/2)*2:ceil(ih/2)*2,scale=out_range=tv,format=yuv420p')

    cmd = ['ffmpeg', '-y', '-v', 'error', '-f', 'concat', '-safe', '0', '-i', lst,
           '-vf', vf, '-c:v', 'libx264', '-preset', 'slow', '-crf', '15',
           '-pix_fmt', 'yuv420p', '-color_range', 'tv', dst]
    subprocess.run(cmd, check=True)
    print(f"Generated clip {dst}: {w}x{h}, {len(rows)} frames, {round(dur_s, 2)}s")

    # 3. Check targets and draw red overlay on frames
    if os.path.isfile(ev_path):
        with open(ev_path, "r", encoding="utf-8") as ef:
            ev_data = json.load(ef)
        for ev in ev_data.get("events", []):
            if ev.get("type") == "target":
                beat_id = ev.get("beat", "")
                target_name = ev.get("name", "")
                rect = ev.get("rect", [0, 0, 0, 0])
                t_ms = ev.get("t", 0)

                # Find closest frame
                best_f = rows[0][0]
                best_diff = 9999999
                for f, f_ms in rows:
                    if abs(f_ms - t_ms) < best_diff:
                        best_diff = abs(f_ms - t_ms)
                        best_f = f

                frame_img_path = os.path.join(d, best_f)
                if os.path.exists(frame_img_path):
                    im = Image.open(frame_img_path).convert("RGB")
                    draw = ImageDraw.Draw(im)
                    rx, ry, rw, rh = rect
                    if rw > 0 and rh > 0:
                        # Draw red rectangle (3px width)
                        for o in range(3):
                            draw.rectangle([rx-o, ry-o, rx+rw+o, ry+rh+o], outline=(255, 0, 0))
                        # Draw label
                        draw.text((rx, max(0, ry - 22)), f"{beat_id}: {target_name} [{rx},{ry},{rw},{rh}]", fill=(255, 0, 0))
                    
                    check_dst = os.path.join(CHECKS_OUT, f"check_{scene}_{beat_id}_{target_name}.jpg")
                    im.save(check_dst, quality=90)
                    checks_summary.append({
                        "scene": scene,
                        "beat": beat_id,
                        "target": target_name,
                        "rect": rect,
                        "check_file": check_dst
                    })

print("\n" + "="*80)
print("MEASURED FRAME RATE TABLE:")
print("| Scene | Frames | Duration (s) | Avg FPS | Median Interval (ms) | 90th% Interval (ms) | Measured FPS |")
print("|---|---|---|---|---|---|---|")
for r in fps_report_rows:
    print(f"| {r['scene']} | {r['frames']} | {r['dur_s']} | {r['avg_fps']} | {r['med_interval_ms']} | {r['p90_interval_ms']} | {r['meas_fps']} |")
print("="*80)

# Save json of results
with open(os.path.join(CHECKS_OUT, "fps_report.json"), "w", encoding="utf-8") as f:
    json.dump(fps_report_rows, f, indent=2)

with open(os.path.join(CHECKS_OUT, "checks_summary.json"), "w", encoding="utf-8") as f:
    json.dump(checks_summary, f, indent=2)

print(f"\nDone! Processed {len(fps_report_rows)} scenes and {len(checks_summary)} target checks.")
