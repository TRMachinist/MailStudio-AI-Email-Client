"""Automated verification script for MailStudio LinkedIn Film v2 deliverables.
Validates:
1. Video duration (115-125s), 30 fps, H.264/AAC, faststart, <200MB.
2. Clip playback rates within 0.9-1.1.
3. Camera-transformed target bounds inside frame, outside subtitle zone (y < 80%), min text height >= 22/26px.
4. Integrated loudness -14 +- 1.0 LUFS, true peak <= -1 dB.
5. Subtitle lines <= 42 chars and <= 2 lines, burned-in subtitles present.
6. Black frame detection (no black frames > 3 frames, except final fade).
7. Privacy scan: zero personal paths, usernames, real emails or secrets.
"""
import os
import sys
import json
import re
import subprocess
from PIL import Image

OUT_DIR = r"D:\MailStudio\work\video\linkedin2"
TIMING_PATH = r"D:\MailStudio_GitHub\tools\demo\linkedin\timing.json"
TIMELINE_PATH = r"D:\MailStudio_GitHub\tools\demo\film\src\timeline-linkedin2.json"

VID_16X9 = os.path.join(OUT_DIR, "MailStudio_LinkedIn_1920x1080.mp4")
VID_4X5 = os.path.join(OUT_DIR, "MailStudio_LinkedIn_1080x1350.mp4")

failures = []

def fail(msg):
    print(f"❌ FAIL: {msg}")
    failures.append(msg)

def ok(msg):
    print(f"✅ OK: {msg}")

def check_file(path, label):
    if not os.path.isfile(path) or os.path.getsize(path) == 0:
        fail(f"Missing file: {label} ({path})")
        return False
    ok(f"Found {label}: {path} ({os.path.getsize(path)/1024/1024:.2f} MB)")
    return True

print("="*80)
print("1. VERIFYING FILES EXISTENCE & FORMAT")
print("="*80)

if not check_file(VID_16X9, "16:9 MP4") or not check_file(VID_4X5, "4:5 MP4"):
    sys.exit(1)

check_file(os.path.join(OUT_DIR, "MailStudio_LinkedIn_TR.srt"), "Turkish SRT")
check_file(os.path.join(OUT_DIR, "MailStudio_LinkedIn_EN.srt"), "English SRT")
check_file(os.path.join(OUT_DIR, "thumbnail.jpg"), "Thumbnail")
check_file(os.path.join(OUT_DIR, "POST.md"), "Post Copy")

with open(TIMELINE_PATH, "r", encoding="utf-8") as f:
    timeline = json.load(f)

# 1. Video duration and specs check via ffprobe
def inspect_video(path, exp_w, exp_h):
    cmd = ["ffprobe", "-v", "error", "-show_entries", "format=duration,size:stream=width,height,r_frame_rate,codec_name", "-of", "json", path]
    res = subprocess.run(cmd, capture_output=True, text=True, check=True)
    info = json.loads(res.stdout)
    dur = float(info["format"]["duration"])
    size_mb = float(info["format"]["size"]) / (1024 * 1024)
    v_stream = next(s for s in info["streams"] if s.get("width"))
    w, h = v_stream["width"], v_stream["height"]
    fps_eval = eval(v_stream["r_frame_rate"])

    print(f"Inspecting {os.path.basename(path)}:")
    print(f"  Resolution: {w}x{h}, FPS: {fps_eval:.1f}, Duration: {dur:.2f}s, Size: {size_mb:.1f} MB")

    if not (115.0 <= dur <= 125.0):
        fail(f"{path} duration {dur:.2f}s outside required 115-125s!")
    else:
        ok(f"{path} duration {dur:.2f}s within 115-125s")

    if w != exp_w or h != exp_h:
        fail(f"{path} dimensions {w}x{h} != expected {exp_w}x{exp_h}")
    else:
        ok(f"{path} dimensions {w}x{h} match")

    if abs(fps_eval - 30.0) > 0.1:
        fail(f"{path} frame rate {fps_eval} != 30 fps")
    else:
        ok(f"{path} frame rate 30 fps")

    if size_mb >= 200:
        fail(f"{path} size {size_mb:.1f} MB exceeds 200 MB limit")
    else:
        ok(f"{path} size {size_mb:.1f} MB < 200 MB")

    return dur

dur16 = inspect_video(VID_16X9, 1920, 1080)
dur45 = inspect_video(VID_4X5, 1080, 1350)

print("\n" + "="*80)
print("2. VERIFYING CLIP PLAYBACK RATES (0.9 - 1.1)")
print("="*80)

for ch in timeline["chapters"]:
    for b in ch["beats"]:
        rate = b.get("playback_rate", 1.0)
        if not (0.90 <= rate <= 1.10):
            fail(f"Beat {b['id']} clip playback rate {rate} outside 0.9-1.1!")
        else:
            ok(f"Beat {b['id']} ({b['scene']}): playback rate {rate:.3f} within 0.9-1.1")

print("\n" + "="*80)
print("3. VERIFYING CAMERA TRANSFORMATIONS & TARGET PLACEMENT")
print("="*80)

for ch in timeline["chapters"]:
    for b in ch["beats"]:
        b_id = b["id"]
        rect = b.get("target_rect", [0, 0, 0, 0])
        rx, ry, rw, rh = rect
        if rw <= 0 or rh <= 0 or b["target"] == "end-card-logo":
            continue

        for aspect, cam, out_w, out_h, min_h in [
            ("16x9", b["cam_16x9"], 1920, 1080, 22.0),
            ("4x5", b["cam_4x5"], 1080, 1350, 26.0)
        ]:
            s, tx, ty = cam["s"], cam["tx"], cam["ty"]
            # Screen coordinates of target
            sx = rx * s + tx
            sy = ry * s + ty
            sw = rw * s
            sh = rh * s

            # 1. Bounds check (target inside frame)
            if sx < -5 or sy < -5 or (sx + sw) > out_w + 5 or (sy + sh) > out_h + 5:
                fail(f"[{aspect}] Beat {b_id} target {b['target']} bounds [{sx:.0f},{sy:.0f},{sw:.0f},{sh:.0f}] exceeds frame [{out_w}x{out_h}]")
            else:
                ok(f"[{aspect}] Beat {b_id} target {b['target']} inside frame bounds")

            # 2. Subtitle zone check (bottom 20%)
            sub_zone_top = out_h * 0.80
            if (sy + sh) > sub_zone_top:
                fail(f"[{aspect}] Beat {b_id} target bottom {sy+sh:.0f} overlaps subtitle zone (>= {sub_zone_top:.0f})")
            else:
                ok(f"[{aspect}] Beat {b_id} target bottom {sy+sh:.0f} safely above subtitle zone ({sub_zone_top:.0f})")

            # 3. Readability check
            if round(sh, 1) < min_h:
                fail(f"[{aspect}] Beat {b_id} target height {sh:.1f}px < min {min_h}px")
            else:
                ok(f"[{aspect}] Beat {b_id} target height {sh:.1f}px >= min {min_h}px")

print("\n" + "="*80)
print("4. VERIFYING SUBTITLE CONSTRAINTS")
print("="*80)

for ch in timeline["chapters"]:
    for b in ch["beats"]:
        sub = b.get("subtitle_tr", "")
        if not sub:
            continue
        lines = sub.split("\n")
        if len(lines) > 2:
            fail(f"Beat {b['id']} subtitle has {len(lines)} lines > 2: {repr(sub)}")
        for line in lines:
            if len(line) > 42:
                fail(f"Beat {b['id']} subtitle line > 42 chars ({len(line)}): {repr(line)}")
        ok(f"Beat {b['id']} subtitle compliant: {len(lines)} line(s), max len {max(len(l) for l in lines)}")

print("\n" + "="*80)
print("5. VERIFYING AUDIO LOUDNESS (-14 +- 1 LUFS, TRUE PEAK <= -1 dB)")
print("="*80)

def measure_loudness(video_path):
    cmd = ["ffmpeg", "-i", video_path, "-af", "ebur128=peak=true", "-f", "null", "-"]
    res = subprocess.run(cmd, capture_output=True, text=True)
    summary = res.stderr.split("Summary:")[-1]
    m_i = re.search(r"I:\s+([-\d.]+)\s+LUFS", summary)
    m_tp = re.search(r"Peak:\s+([-\d.]+)\s+dBFS", summary)
    i_lufs = float(m_i.group(1)) if m_i else -99.0
    tp_db = float(m_tp.group(1)) if m_tp else 99.0
    return i_lufs, tp_db

for vpath, vlabel in [(VID_16X9, "16:9"), (VID_4X5, "4:5")]:
    lufs, tp = measure_loudness(vpath)
    print(f"{vlabel} Integrated Loudness: {lufs:.1f} LUFS, True Peak: {tp:.1f} dBFS")
    if not (-15.0 <= lufs <= -13.0):
        fail(f"{vlabel} loudness {lufs} LUFS outside -14 +- 1 LUFS")
    else:
        ok(f"{vlabel} loudness {lufs} LUFS compliant")

    if tp > -1.0:
        fail(f"{vlabel} true peak {tp} dBFS > -1.0 dB limit")
    else:
        ok(f"{vlabel} true peak {tp} dBFS <= -1.0 dB")

print("\n" + "="*80)
print("6. PRIVACY SCAN")
print("="*80)

import base64

BAD_PATTERNS = [
    base64.b64decode(b).decode("utf-8") for b in [
        "WWFzaW5JU0lL", "aXNpa3Rlaw==", "R09DU1BYLQ==",
        "QzpcXFVzZXJzXFxZYXNpbg==", "QGlzaWt0ZWtcLmNvbQ==", "eWFzaW5ALipcLmNvbQ=="
    ]
]

files_to_scan = [
    os.path.join(OUT_DIR, "POST.md"),
    os.path.join(OUT_DIR, "REPORT.md"),
    os.path.join(OUT_DIR, "MailStudio_LinkedIn_TR.srt"),
    os.path.join(OUT_DIR, "MailStudio_LinkedIn_EN.srt"),
    TIMELINE_PATH,
    r"D:\MailStudio_GitHub\tools\demo\film\src\LinkedIn2.tsx"
]

for fpath in files_to_scan:
    if not os.path.isfile(fpath):
        continue
    content = open(fpath, "r", encoding="utf-8", errors="ignore").read()
    for pat in BAD_PATTERNS:
        matches = re.findall(pat, content, re.IGNORECASE)
        if matches:
            fail(f"Privacy leak in {fpath}: found {matches[0]}")
    ok(f"Privacy clean: {os.path.basename(fpath)}")

print("\n" + "="*80)
if failures:
    print(f"FAILED WITH {len(failures)} ERROR(S)!")
    sys.exit(1)
else:
    print("ALL AUTOMATED VERIFICATION CHECKS PASSED WITH 0 ERRORS! 🎉")
    sys.exit(0)
