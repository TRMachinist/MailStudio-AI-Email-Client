"""
Voice generator for MailStudio 1.8 LinkedIn Product Film v2.
Reads script.json, generates narration per beat via edge-tts (en-US-AndrewNeural),
measures audio durations with ffprobe, calculates exact chapter/beat timing (total 115-125s, target 120s),
and outputs timing.json and SRT files.
"""
import asyncio
import json
import math
import subprocess
import sys
from pathlib import Path
import edge_tts

ROOT = Path(__file__).resolve().parent.parent.parent.parent
SCRIPT_PATH = ROOT / "tools" / "demo" / "linkedin" / "script.json"
OUT_VOICE = ROOT / "tools" / "demo" / "film" / "public" / "voice_linkedin2"
TIMING_PATH_1 = ROOT / "tools" / "demo" / "linkedin" / "timing.json"
TIMING_PATH_2 = ROOT / "tools" / "demo" / "film" / "src" / "timing.json"
TIMING_PATH_3 = Path(r"D:\MailStudio\work\video\linkedin2\timing.json")

SRT_TR_PATH = Path(r"D:\MailStudio\work\video\linkedin2\MailStudio_LinkedIn_TR.srt")
SRT_EN_PATH = Path(r"D:\MailStudio\work\video\linkedin2\MailStudio_LinkedIn_EN.srt")

VOICE_NAME = "en-US-AndrewNeural"
VOICE_RATE = "+2%"
FPS = 30

def probe_duration(path: Path) -> float:
    cmd = [
        "ffprobe", "-v", "error",
        "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1:nokey=1",
        str(path)
    ]
    res = subprocess.run(cmd, capture_output=True, text=True, check=True)
    return float(res.stdout.strip())

def format_srt_time(seconds: float) -> str:
    millis = int(round(seconds * 1000))
    hrs = millis // 3600000
    millis %= 3600000
    mins = millis // 60000
    millis %= 60000
    secs = millis // 1000
    ms = millis % 1000
    return f"{hrs:02d}:{mins:02d}:{secs:02d},{ms:03d}"

async def generate_beat_voice(text: str, out_mp3: Path, out_wav: Path) -> float:
    out_mp3.parent.mkdir(parents=True, exist_ok=True)
    # Retry loop for reliability
    last_err = None
    for attempt in range(5):
        try:
            comm = edge_tts.Communicate(text, VOICE_NAME, rate=VOICE_RATE)
            await comm.save(str(out_mp3))
            if out_mp3.stat().st_size > 500:
                break
        except Exception as e:
            last_err = e
            await asyncio.sleep(2)
    else:
        raise RuntimeError(f"TTS failed for text '{text}': {last_err}")

    # Convert to 48kHz mono WAV for precision
    subprocess.run([
        "ffmpeg", "-y", "-v", "error",
        "-i", str(out_mp3),
        "-ar", "48000", "-ac", "1",
        str(out_wav)
    ], check=True)
    return probe_duration(out_wav)

async def main():
    print("Loading script from", SCRIPT_PATH)
    with open(SCRIPT_PATH, "r", encoding="utf-8") as f:
        script_data = json.load(f)

    chapters = script_data["chapters"]
    total_beats = sum(len(ch["beats"]) for ch in chapters)
    print(f"Loaded {len(chapters)} chapters, {total_beats} beats.")

    OUT_VOICE.mkdir(parents=True, exist_ok=True)

    # Step 1: Generate audio for each beat
    beat_durations = {}
    total_speech_sec = 0.0

    for ch in chapters:
        for b in ch["beats"]:
            bid = b["id"]
            mp3_path = OUT_VOICE / f"{bid}.mp3"
            wav_path = OUT_VOICE / f"{bid}.wav"
            print(f"Generating TTS for {bid}: \"{b['voice']}\"...")
            dur = await generate_beat_voice(b["voice"], mp3_path, wav_path)
            beat_durations[bid] = dur
            total_speech_sec += dur
            print(f"  -> {dur:.2f}s")

    print(f"\nTotal pure speech duration: {total_speech_sec:.2f}s")

    # Step 2: Calibrate timeline
    # Target total duration: 120.0 seconds
    TARGET_TOTAL_SEC = 120.0
    # Available pause budget
    pause_budget = TARGET_TOTAL_SEC - total_speech_sec
    print(f"Pause budget across 120s film: {pause_budget:.2f}s")

    # We distribute pauses:
    # - intro lead time: 0.8s
    # - outro tail time: 2.5s
    # - chapter gaps (10 gaps between 11 chapters): gap_ch
    # - beat gaps inside chapters: gap_beat
    intro_lead = 0.8
    outro_tail = 2.5
    remaining = pause_budget - (intro_lead + outro_tail)

    num_chapter_gaps = len(chapters) - 1 # 10
    num_intra_beat_gaps = total_beats - len(chapters) # 34 - 11 = 23

    # We want gap_ch ~= 2 * gap_beat
    # remaining = num_chapter_gaps * gap_ch + num_intra_beat_gaps * gap_beat
    # remaining = (10 * 2 + 23) * gap_beat = 43 * gap_beat
    gap_beat = max(0.30, min(0.60, remaining / (num_chapter_gaps * 2.0 + num_intra_beat_gaps)))
    gap_ch = max(0.60, min(1.20, gap_beat * 2.0))

    # Recompute total with these gaps
    calculated_total = intro_lead + outro_tail + total_speech_sec + (num_chapter_gaps * gap_ch) + (num_intra_beat_gaps * gap_beat)
    # Adjust outro_tail slightly so final duration is exactly TARGET_TOTAL_SEC
    outro_tail += (TARGET_TOTAL_SEC - calculated_total)
    outro_tail = max(1.5, outro_tail)

    final_total_sec = intro_lead + total_speech_sec + (num_chapter_gaps * gap_ch) + (num_intra_beat_gaps * gap_beat) + outro_tail
    print(f"Calibrated: gap_beat = {gap_beat:.3f}s, gap_ch = {gap_ch:.3f}s, intro = {intro_lead:.2f}s, outro = {outro_tail:.2f}s")
    print(f"Final film duration: {final_total_sec:.3f}s ({round(final_total_sec * FPS)} frames @ {FPS} fps)")

    # Build timing structure
    current_time = intro_lead
    timing_chapters = []
    srt_tr_entries = []
    srt_en_entries = []
    sub_index = 1

    for ch_idx, ch in enumerate(chapters):
        ch_start = current_time
        timing_beats = []

        for b_idx, b in enumerate(ch["beats"]):
            bid = b["id"]
            dur = beat_durations[bid]
            b_start = current_time
            b_end = b_start + dur

            # Subtitle timing: start at voice start, end at voice end + slight linger
            sub_end = min(b_end + 0.25, b_end + gap_beat * 0.5)

            # Record beat
            beat_info = {
                "id": bid,
                "voice": b["voice"],
                "subtitle_tr": b["subtitle_tr"],
                "target": b["target"],
                "action": b["action"],
                "hold_ms": b["hold_ms"],
                "start_s": round(b_start, 3),
                "dur_s": round(dur, 3),
                "end_s": round(b_end, 3),
                "start_ms": int(round(b_start * 1000)),
                "dur_ms": int(round(dur * 1000)),
                "end_ms": int(round(b_end * 1000)),
                "start_frame": int(round(b_start * FPS)),
                "dur_frames": int(round(dur * FPS)),
                "voice_file": f"voice_linkedin2/{bid}.mp3"
            }
            timing_beats.append(beat_info)

            # SRT entry TR
            srt_tr_entries.append(
                f"{sub_index}\n{format_srt_time(b_start)} --> {format_srt_time(sub_end)}\n{b['subtitle_tr']}\n"
            )
            # SRT entry EN
            srt_en_entries.append(
                f"{sub_index}\n{format_srt_time(b_start)} --> {format_srt_time(sub_end)}\n{b['voice']}\n"
            )
            sub_index += 1

            current_time = b_end
            if b_idx < len(ch["beats"]) - 1:
                current_time += gap_beat

        ch_end = current_time
        timing_chapters.append({
            "id": ch["id"],
            "title": ch["title"],
            "scene": ch["scene"],
            "start_s": round(ch_start, 3),
            "dur_s": round(ch_end - ch_start, 3),
            "end_s": round(ch_end, 3),
            "start_ms": int(round(ch_start * 1000)),
            "dur_ms": int(round((ch_end - ch_start) * 1000)),
            "end_ms": int(round(ch_end * 1000)),
            "start_frame": int(round(ch_start * FPS)),
            "dur_frames": int(round((ch_end - ch_start) * FPS)),
            "beats": timing_beats
        })

        if ch_idx < len(chapters) - 1:
            current_time += gap_ch

    current_time += outro_tail
    final_duration = round(current_time, 3)
    final_frames = int(round(final_duration * FPS))

    timing_manifest = {
        "fps": FPS,
        "total_duration_s": final_duration,
        "total_frames": final_frames,
        "width_16x9": 1920,
        "height_16x9": 1080,
        "width_4x5": 1080,
        "height_4x5": 1350,
        "capture_scale": 1.5,
        "chapters": timing_chapters
    }

    # Save timing.json
    timing_json_str = json.dumps(timing_manifest, ensure_ascii=False, indent=2)
    TIMING_PATH_1.write_text(timing_json_str, encoding="utf-8")
    TIMING_PATH_2.write_text(timing_json_str, encoding="utf-8")
    TIMING_PATH_3.parent.mkdir(parents=True, exist_ok=True)
    TIMING_PATH_3.write_text(timing_json_str, encoding="utf-8")
    print(f"Saved timing.json to {TIMING_PATH_1}, {TIMING_PATH_2}, {TIMING_PATH_3}")

    # Save SRTs
    SRT_TR_PATH.parent.mkdir(parents=True, exist_ok=True)
    SRT_TR_PATH.write_text("\n".join(srt_tr_entries), encoding="utf-8")
    SRT_EN_PATH.write_text("\n".join(srt_en_entries), encoding="utf-8")
    print(f"Saved SRTs: {SRT_TR_PATH} and {SRT_EN_PATH}")

    # Verification of constraints
    assert 115.0 <= final_duration <= 125.0, f"Duration {final_duration} outside 115-125s!"
    print(f"\nSUCCESS: Timing fully generated and calibrated: {final_duration:.2f}s ({final_frames} frames).")

if __name__ == "__main__":
    asyncio.run(main())
