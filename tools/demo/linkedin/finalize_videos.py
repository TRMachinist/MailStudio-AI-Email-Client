import os
import sys
import subprocess
import json
import re

OUT_DIR = r"D:\MailStudio\work\video\linkedin2"
VID_16X9 = os.path.join(OUT_DIR, "MailStudio_LinkedIn_1920x1080.mp4")
VID_4X5 = os.path.join(OUT_DIR, "MailStudio_LinkedIn_1080x1350.mp4")

print("Checking video audio levels...")

def measure(video_path):
    cmd = ["ffmpeg", "-i", video_path, "-af", "ebur128=peak=true", "-f", "null", "-"]
    res = subprocess.run(cmd, capture_output=True, text=True)
    summary = res.stderr.split("Summary:")[-1]
    m_i = re.search(r"I:\s+([-\d.]+)\s+LUFS", summary)
    m_tp = re.search(r"Peak:\s+([-\d.]+)\s+dBFS", summary)
    i_lufs = float(m_i.group(1)) if m_i else -99.0
    tp_db = float(m_tp.group(1)) if m_tp else 99.0
    return i_lufs, tp_db

def normalize_if_needed(video_path, label):
    if not os.path.exists(video_path):
        print(f"Warning: {video_path} not found!")
        return
    i_lufs, tp_db = measure(video_path)
    print(f"Current {label} -> Integrated Loudness: {i_lufs:.1f} LUFS, True Peak: {tp_db:.1f} dBFS")
    if abs(i_lufs - (-14.0)) > 1.0 or tp_db > -1.0:
        print(f"Normalizing {label} audio to -14 LUFS (TP <= -1.0 dBFS)...")
        tmp_norm = os.path.join(OUT_DIR, f"_norm_{label}.mp4")
        cmd_norm = [
            "ffmpeg", "-y", "-i", video_path,
            "-af", "loudnorm=I=-14:TP=-1.5:LRA=7",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart",
            tmp_norm
        ]
        subprocess.run(cmd_norm, check=True)
        os.replace(tmp_norm, video_path)
        i_lufs_new, tp_db_new = measure(video_path)
        print(f"Normalized {label} -> Integrated: {i_lufs_new:.1f} LUFS, True Peak: {tp_db_new:.1f} dBFS")

normalize_if_needed(VID_16X9, "16x9")
normalize_if_needed(VID_4X5, "4x5")

# Run verify.py
verify_script = r"D:\MailStudio_GitHub\tools\demo\linkedin\verify.py"
print("\nRunning verification suite...")
res = subprocess.run([sys.executable, verify_script])
if res.returncode != 0:
    print("Verification failed!")
    sys.exit(res.returncode)

print("\nAll deliverables validated and ready!")
