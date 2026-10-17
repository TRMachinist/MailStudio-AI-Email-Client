# Demo film (≤ 60 s, English + Turkish)

Everything in the film is the real app running on the demo mailbox — no real mail.

1. Record: `MailStudio.exe --demo --showcase-video <rec-dir>` (set `MAILSTUDIO_LANG=en` or `tr`).
   MailStudio drives itself (real clicks, real viewers) and records only its own windows with a drawn pointer.
2. Clips: `python tools/demo/clips.py <rec-dir> film/public/clips/<lang>` (constant 30 fps MP4s, needs ffmpeg).
3. Voice: `python tools/demo/voice.py` (edge-tts; only the public narration text is sent), copy `voice-<lang>` to
   `film/public/voice/<lang>`. Put `MailStudio.png` as `film/public/logo.png` and the last inbox frame as
   `film/public/stills/intro-<lang>.jpg`.
4. Timeline + soundtrack: `python film/tools/build.py` (music is synthesized in code; loudness -14 LUFS).
5. Render: `npm install` then `npx remotion render src/index.ts FilmEN out/MailStudio_Demo_EN_1920x1080.mp4`
   (also `FilmTR`, `VertEN`, `VertTR`).
