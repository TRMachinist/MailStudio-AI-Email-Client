"""Verifies finished MailStudio 1.8 LinkedIn videos according to Phase F requirements."""
import json, os, subprocess, sys
from pathlib import Path
from PIL import Image

OUT_DIR = Path(r"D:\MailStudio\work\video\linkedin")

def probe_video(path):
    cmd = [
        'ffprobe', '-v', 'error',
        '-select_streams', 'v:0',
        '-show_entries', 'format=duration,size,bit_rate:stream=width,height,r_frame_rate,codec_name,pix_fmt',
        '-of', 'json', str(path)
    ]
    res = subprocess.run(cmd, capture_output=True, text=True, check=True)
    j = json.loads(res.stdout)
    fmt = j['format']
    stm = j['streams'][0]
    return {
        'duration': float(fmt['duration']),
        'size_mb': float(fmt['size']) / (1024 * 1024),
        'width': int(stm['width']),
        'height': int(stm['height']),
        'fps': eval(stm['r_frame_rate']),
        'codec': stm['codec_name'],
        'pix_fmt': stm['pix_fmt'],
    }

def check_loudness(path):
    cmd = ['ffmpeg', '-i', str(path), '-af', 'ebur128=framelog=verbose', '-f', 'null', '-']
    res = subprocess.run(cmd, capture_output=True, text=True)
    # Parse integrated loudness I:
    lufs = None
    for line in res.stderr.splitlines():
        if 'I:' in line and 'LUFS' in line:
            parts = line.strip().split()
            for i, p in enumerate(parts):
                if p == 'I:' and i + 1 < len(parts):
                    try:
                        lufs = float(parts[i + 1])
                    except ValueError:
                        pass
    return lufs

def make_contact_sheet(video_path, out_sheet_path, step_sec=2):
    p = Path(video_path)
    probe = probe_video(p)
    dur = probe['duration']
    n_frames = int(dur // step_sec)

    temp_dir = OUT_DIR / f"_frames_{p.stem}"
    temp_dir.mkdir(parents=True, exist_ok=True)

    print(f"Extracting {n_frames} frames (every {step_sec}s) from {p.name}...")
    subprocess.run([
        'ffmpeg', '-y', '-v', 'error',
        '-i', str(video_path),
        '-vf', f'fps=1/{step_sec},scale=320:-1',
        '-q:v', '3',
        str(temp_dir / 'frame_%03d.jpg')
    ], check=True)

    frames = sorted(temp_dir.glob('frame_*.jpg'))
    if not frames:
        print(f"No frames extracted for {video_path}")
        return

    # Build contact sheet grid (6 columns)
    cols = 6
    rows = (len(frames) + cols - 1) // cols
    fw, fh = Image.open(frames[0]).size
    sheet = Image.new('RGB', (cols * fw, rows * fh), (14, 15, 18))

    for idx, f in enumerate(frames):
        im = Image.open(f)
        c = idx % cols
        r = idx // cols
        sheet.paste(im, (c * fw, r * fh))

    sheet.save(out_sheet_path, quality=90)
    print(f"Contact sheet saved: {out_sheet_path} ({len(frames)} frames)")

    # Clean up temp frame folder
    for f in frames:
        f.unlink(missing_ok=True)
    temp_dir.rmdir()

def scan_privacy():
    import base64
    forbidden = [base64.b64decode(s).decode('utf-8') for s in [b'WWFzaW5JU0lL', b'aXNpa3Rlaw==', b'R09DU1BYLQ==', b'QzpcVXNlcnNcWWFzaW4=']]
    leaks = []
    for f in OUT_DIR.glob('*.srt'):
        txt = f.read_text(encoding='utf-8')
        for b in forbidden:
            if b.lower() in txt.lower():
                leaks.append((f.name, b))
    for f in OUT_DIR.glob('*.md'):
        txt = f.read_text(encoding='utf-8')
        for b in forbidden:
            if b.lower() in txt.lower():
                leaks.append((f.name, b))
    return leaks

def verify_all():
    videos = [
        'MailStudio_1.8_LinkedIn_1920x1080.mp4',
        'MailStudio_1.8_LinkedIn_1080x1350.mp4',
    ]
    report = []

    for v in videos:
        vp = OUT_DIR / v
        if not vp.exists():
            print(f"FAIL: {v} does not exist!")
            continue

        meta = probe_video(vp)
        lufs = check_loudness(vp)
        sheet_path = OUT_DIR / f"contact_sheet_{vp.stem}.jpg"
        make_contact_sheet(vp, sheet_path)

        dur_ok = 115.0 <= meta['duration'] <= 125.0
        lufs_ok = lufs is not None and (-15.5 <= lufs <= -12.5)
        size_ok = meta['size_mb'] < 200.0

        res = {
            'file': v,
            'duration': f"{meta['duration']:.2f}s (OK: {dur_ok})",
            'resolution': f"{meta['width']}x{meta['height']}",
            'size': f"{meta['size_mb']:.1f} MB (OK: {size_ok})",
            'codec': meta['codec'],
            'pix_fmt': meta['pix_fmt'],
            'loudness': f"{lufs} LUFS (OK: {lufs_ok})",
            'contact_sheet': sheet_path.name,
        }
        report.append(res)
        print(f"\n--- {v} ---")
        for k, val in res.items():
            print(f"  {k}: {val}")

    # Extract thumbnail at 56s (revision comparison)
    thumb_path = OUT_DIR / "thumbnail.jpg"
    v169 = OUT_DIR / 'MailStudio_1.8_LinkedIn_1920x1080.mp4'
    if v169.exists():
        subprocess.run([
            'ffmpeg', '-y', '-v', 'error',
            '-ss', '56.0',
            '-i', str(v169),
            '-frames:v', '1',
            '-q:v', '2',
            str(thumb_path)
        ], check=True)
        print(f"\nThumbnail extracted: {thumb_path}")

    leaks = scan_privacy()
    if leaks:
        print("\nPRIVACY LEAKS:", leaks)
    else:
        print("\nPRIVACY SCAN: PASS (0 leaks)")

    return report

if __name__ == '__main__':
    verify_all()
