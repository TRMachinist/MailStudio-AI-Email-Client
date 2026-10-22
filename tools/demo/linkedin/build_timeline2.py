import os
import sys
import json
import math

RAW_DIR = r"D:\MailStudio\work\video\linkedin2\raw"
TIMING_PATH = r"D:\MailStudio_GitHub\tools\demo\linkedin\timing.json"
OUT_TIMELINE = r"D:\MailStudio_GitHub\tools\demo\film\src\timeline-linkedin2.json"
OUT_TIMELINE_WORK = r"D:\MailStudio\work\video\linkedin2\timeline-linkedin2.json"

TARGET_REFINEMENTS = {
    'diff-row-hole-diam': [27, 264, 850, 41],
    'diff-row-roughness': [27, 307, 850, 41],
    'diff-add-to-reply-btn': [2532, 1532, 192, 52],
    'step-viewport': [800, 350, 1000, 800],
    'step-stock-tab': [2286, 200, 560, 450],
    'dxf-snap-point': [1100, 500, 850, 600],
    'pdf-highlight-box': [650, 350, 950, 400],
    'pdf-ask-answer': [18, 1576, 800, 24],
    'word-viewer-doc': [700, 150, 1450, 900],
    'pptx-viewer-slide': [600, 150, 1500, 900],
    'cnc-tools-header': [200, 120, 1400, 650],
    'archive-item-pdf': [100, 151, 950, 40],
    'undo-bar-btn': [2550, 1515, 200, 50],
    'calendar-week-grid': [600, 220, 1300, 650],
    'contact-card-tabs': [200, 200, 1400, 800],
    'dark-theme-inbox': [350, 150, 1800, 950],
    'ai-summary-text': [945, 347, 850, 48],
}

with open(TIMING_PATH, "r", encoding="utf-8") as f:
    timing = json.load(f)

# Scene to clip mapping
scene_folders = sorted([d for d in os.listdir(RAW_DIR) if os.path.isdir(os.path.join(RAW_DIR, d))])
print(f"Discovered scenes: {scene_folders}")

# Load all events.json and frames.txt
scene_data = {}
for sf in scene_folders:
    d = os.path.join(RAW_DIR, sf)
    ev_path = os.path.join(d, "events.json")
    ft_path = os.path.join(d, "frames.txt")
    if os.path.isfile(ev_path) and os.path.isfile(ft_path):
        with open(ev_path, "r", encoding="utf-8") as ef:
            ev = json.load(ef)
        lines = [l.strip().split("\t") for l in open(ft_path, encoding="utf-8").read().splitlines() if l.strip()]
        frames = [(l[0], int(l[1])) for l in lines]
        dur_ms = frames[-1][1] - frames[0][1] if len(frames) > 1 else 0
        scene_data[sf] = {
            "events": ev.get("events", []),
            "window": ev.get("window", {"w": 2880, "h": 1620, "scale": 1.5}),
            "frames_count": len(frames),
            "dur_ms": dur_ms,
            "dur_s": dur_ms / 1000.0,
            "clip_src": f"linkedin2/clips/{sf}.mp4"
        }

def compute_camera(rect, out_w, out_h, cap_w=2880, cap_h=1620, aspect="16x9"):
    rx, ry, rw, rh = rect
    if rw <= 0 or rh <= 0 or (rw >= cap_w - 50 and rh >= cap_h - 50):
        if aspect == "16x9":
            s = out_w / cap_w
            return {"cx": cap_w / 2, "cy": cap_h / 2, "zoom": 1.0, "s": s, "tx": 0, "ty": 0}
        else:
            s = max(out_w / cap_w, out_h / cap_h)
            tx = (out_w - cap_w * s) / 2
            ty = (out_h - cap_h * s) / 2
            return {"cx": cap_w / 2, "cy": cap_h / 2, "zoom": 1.0, "s": s, "tx": tx, "ty": ty}

    base_s = (out_w / cap_w) if aspect == "16x9" else (out_w / cap_w)

    # Target scale to fill ~60% width
    pw = rw * 1.3
    desired_w = out_w * 0.60
    s = desired_w / max(1.0, pw)
    zoom = s / base_s
    zoom = max(1.0, min(2.5, zoom))
    s = base_s * zoom

    # Readability: in final frame text height must be >= 22 px (16:9) / >= 26 px (4:5)
    min_h = 22.0 if aspect == "16x9" else 26.0
    if rh * s < min_h + 1.0:
        s = (min_h + 1.5) / max(1.0, rh)

    # Keep target within frame safe bounds (not too tall for top 74%)
    max_safe_h = out_h * 0.74
    if rh * s > max_safe_h:
        s = max_safe_h / rh
    max_safe_w = out_w - 40
    if rw * s > max_safe_w:
        s = max_safe_w / rw

    zoom = s / base_s

    # Center horizontally and place vertically in upper half
    cx = rx + rw / 2.0
    cy = ry + rh / 2.0
    tx = out_w / 2.0 - cx * s
    ty = out_h * 0.38 - cy * s

    # Shift to ensure target is strictly inside frame and above subtitle zone
    sx = rx * s + tx
    sy = ry * s + ty

    if sx < 20:
        tx += (20 - sx)
    if (rx + rw) * s + tx > out_w - 20:
        tx -= (((rx + rw) * s + tx) - (out_w - 20))
    if sy < 20:
        ty += (20 - sy)
    if (ry + rh) * s + ty > out_h * 0.78:
        ty -= (((ry + rh) * s + ty) - out_h * 0.78)

    return {
        "cx": round(cx, 1),
        "cy": round(cy, 1),
        "zoom": round(zoom, 3),
        "s": round(s, 4),
        "tx": round(tx, 1),
        "ty": round(ty, 1)
    }

# Process chapters and beats
chapters = []
scenes = []

for ch in timing["chapters"]:
    ch_id = ch["id"]
    ch_beats = ch["beats"]
    sc_name = ch["scene"]

    # Handle C09 split into subscenes
    if ch_id == "C09":
        sub_map = {
            "C09b1": "C09a-word",
            "C09b2": "C09b-ppt",
            "C09b3": "C09c-cnc",
            "C09b4": "C09d-rar"
        }
        for b in ch_beats:
            b_id = b["id"]
            sub_sc = sub_map[b_id]
            sc_info = scene_data.get(sub_sc, {})
            clip_dur_s = sc_info.get("dur_s", b["dur_s"])
            rate = round(clip_dur_s / max(0.1, b["dur_s"]), 3) if clip_dur_s > 0 else 1.0
            rate = max(0.90, min(1.10, rate))
            scenes.append({
                "id": sub_sc,
                "clip_src": sc_info.get("clip_src", f"linkedin2/clips/{sub_sc}.mp4"),
                "start_frame": b["start_frame"],
                "dur_frames": b["dur_frames"],
                "playback_rate": rate
            })
    else:
        sub_map = {b["id"]: sc_name for b in ch_beats}
        sc_info = scene_data.get(sc_name, {})
        ch_clip_dur = sc_info.get("dur_s", ch["dur_s"])
        rate = round(ch_clip_dur / max(0.1, ch["dur_s"]), 3) if ch_clip_dur > 0 else 1.0
        rate = max(0.90, min(1.10, rate))
        scenes.append({
            "id": sc_name,
            "clip_src": sc_info.get("clip_src", f"linkedin2/clips/{sc_name}.mp4"),
            "start_frame": ch["start_frame"],
            "dur_frames": ch["dur_frames"],
            "playback_rate": rate
        })

    processed_beats = []
    for b in ch_beats:
        b_id = b["id"]
        sub_sc = sub_map[b_id]
        sc_info = scene_data.get(sub_sc, {})

        target_name = b.get("target", "")
        rect = TARGET_REFINEMENTS.get(target_name, [0, 0, 0, 0])
        if rect == [0, 0, 0, 0]:
            for ev in sc_info.get("events", []):
                if ev.get("type") == "target" and (ev.get("beat") == b_id or ev.get("name") == target_name):
                    rect = ev.get("rect", [0, 0, 0, 0])
                    break

        cam16 = compute_camera(rect, 1920, 1080, aspect="16x9")
        cam45 = compute_camera(rect, 1080, 1350, aspect="4x5")

        # Collect cursor points for this beat
        cursor_pts = []
        clicks = []
        for ev in sc_info.get("events", []):
            t = ev.get("t", 0)
            if ev.get("type") == "cursor":
                cursor_pts.append({"t": t, "x": ev["x"], "y": ev["y"]})
            elif ev.get("type") == "click":
                clicks.append({"t": t, "x": ev["x"], "y": ev["y"]})

        expected_dur_s = b["dur_s"]
        clip_dur_s = sc_info.get("dur_s", expected_dur_s)
        if ch_id == "C09":
            rate = round(clip_dur_s / max(0.1, expected_dur_s), 3) if clip_dur_s > 0 else 1.0
        else:
            ch_clip_dur = sc_info.get("dur_s", ch["dur_s"])
            rate = round(ch_clip_dur / max(0.1, ch["dur_s"]), 3) if ch_clip_dur > 0 else 1.0
        rate = max(0.90, min(1.10, rate))

        processed_beats.append({
            "id": b_id,
            "voice": b["voice"],
            "subtitle_tr": b["subtitle_tr"],
            "target": target_name,
            "target_rect": rect,
            "scene": sub_sc,
            "clip_src": sc_info.get("clip_src", f"linkedin2/clips/{sub_sc}.mp4"),
            "playback_rate": rate,
            "start_frame": b["start_frame"],
            "dur_frames": b["dur_frames"],
            "start_ms": b["start_ms"],
            "dur_ms": b["dur_ms"],
            "voice_file": b["voice_file"],
            "cam_16x9": cam16,
            "cam_4x5": cam45,
            "cursor_pts": cursor_pts,
            "clicks": clicks
        })

    chapters.append({
        "id": ch_id,
        "title": ch["title"],
        "scene": sc_name,
        "start_frame": ch["start_frame"],
        "dur_frames": ch["dur_frames"],
        "start_ms": ch["start_ms"],
        "dur_ms": ch["dur_ms"],
        "beats": processed_beats
    })

full_timeline = {
    "fps": 30,
    "totalFrames": timing["total_frames"],
    "totalDurationS": timing["total_duration_s"],
    "width_16x9": 1920,
    "height_16x9": 1080,
    "width_4x5": 1080,
    "height_4x5": 1350,
    "scenes": scenes,
    "chapters": chapters
}

with open(OUT_TIMELINE, "w", encoding="utf-8") as f:
    json.dump(full_timeline, f, indent=2)

with open(OUT_TIMELINE_WORK, "w", encoding="utf-8") as f:
    json.dump(full_timeline, f, indent=2)

print(f"Generated {OUT_TIMELINE} successfully with {len(scenes)} scenes, {len(chapters)} chapters, and {sum(len(c['beats']) for c in chapters)} beats.")
