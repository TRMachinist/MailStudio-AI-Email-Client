import React from 'react';
import {
  AbsoluteFill,
  Audio,
  OffthreadVideo,
  Sequence,
  interpolate,
  staticFile,
  useCurrentFrame,
  useVideoConfig,
  Easing,
} from 'remotion';
import timeline from './timeline-linkedin2.json';

const FONT = "Inter, -apple-system, BlinkMacSystemFont, 'Segoe UI Variable', 'Segoe UI', Roboto, sans-serif";
const clamp = {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'} as const;
const easeInOut = Easing.bezier(0.42, 0, 0.58, 1);

interface CamState {
  cx: number;
  cy: number;
  zoom: number;
  s: number;
  tx: number;
  ty: number;
}

interface CursorPt {
  t: number;
  x: number;
  y: number;
}

interface ClickEvent {
  t: number;
  x: number;
  y: number;
}

interface Beat {
  id: string;
  voice: string;
  subtitle_tr: string;
  target: string;
  target_rect: [number, number, number, number];
  scene: string;
  clip_src: string;
  playback_rate: number;
  start_frame: number;
  dur_frames: number;
  start_ms: number;
  dur_ms: number;
  voice_file: string;
  cam_16x9: CamState;
  cam_4x5: CamState;
  cursor_pts: CursorPt[];
  clicks: ClickEvent[];
}

interface Chapter {
  id: string;
  title: string;
  scene: string;
  start_frame: number;
  dur_frames: number;
  start_ms: number;
  dur_ms: number;
  beats: Beat[];
}

interface LinkedIn2Props {
  aspect?: '16x9' | '4x5';
}

export const LinkedIn2Film: React.FC<LinkedIn2Props> = ({aspect = '16x9'}) => {
  const {width, height} = useVideoConfig();
  const f = useCurrentFrame();

  const chapters = (timeline as any).chapters as Chapter[];
  const allBeats: Beat[] = [];
  chapters.forEach((ch) => {
    ch.beats.forEach((b) => allBeats.push(b));
  });

  // Find active beat and index
  let activeBeatIndex = allBeats.findIndex(
    (b) => f >= b.start_frame && f < b.start_frame + b.dur_frames
  );
  if (activeBeatIndex === -1) {
    activeBeatIndex = f >= allBeats[allBeats.length - 1].start_frame ? allBeats.length - 1 : 0;
  }
  const activeBeat = allBeats[activeBeatIndex];
  const beatRelFrame = f - activeBeat.start_frame;

  // Active chapter
  const activeChapter =
    chapters.find((ch) => f >= ch.start_frame && f < ch.start_frame + ch.dur_frames) ??
    chapters[0];
  const chRelFrame = f - activeChapter.start_frame;

  // Compute camera position with smooth ease transition between beats
  // Transitions start 12 frames (400 ms) before the next beat
  const getCam = (b: Beat): CamState => (aspect === '16x9' ? b.cam_16x9 : b.cam_4x5);

  let currentCam = getCam(activeBeat);
  const nextBeat = allBeats[activeBeatIndex + 1];
  const TRANSITION_FRAMES = 12;

  if (nextBeat) {
    const framesUntilNext = nextBeat.start_frame - f;
    if (framesUntilNext <= TRANSITION_FRAMES && framesUntilNext >= 0) {
      const nextCam = getCam(nextBeat);
      const progress = interpolate(
        TRANSITION_FRAMES - framesUntilNext,
        [0, TRANSITION_FRAMES],
        [0, 1],
        {...clamp, easing: easeInOut}
      );
      currentCam = {
        cx: interpolate(progress, [0, 1], [currentCam.cx, nextCam.cx]),
        cy: interpolate(progress, [0, 1], [currentCam.cy, nextCam.cy]),
        zoom: interpolate(progress, [0, 1], [currentCam.zoom, nextCam.zoom]),
        s: interpolate(progress, [0, 1], [currentCam.s, nextCam.s]),
        tx: interpolate(progress, [0, 1], [currentCam.tx, nextCam.tx]),
        ty: interpolate(progress, [0, 1], [currentCam.ty, nextCam.ty]),
      };
    }
  }

  // Cursor interpolation at 30 fps
  let curX = -100;
  let curY = -100;
  let showCursor = false;

  if (activeBeat.cursor_pts && activeBeat.cursor_pts.length > 0) {
    const elapsedMs = Math.round((beatRelFrame / 30) * 1000);
    const pts = activeBeat.cursor_pts;
    if (elapsedMs <= pts[0].t) {
      curX = pts[0].x;
      curY = pts[0].y;
      showCursor = true;
    } else if (elapsedMs >= pts[pts.length - 1].t) {
      curX = pts[pts.length - 1].x;
      curY = pts[pts.length - 1].y;
      showCursor = true;
    } else {
      // Find segment
      for (let i = 0; i < pts.length - 1; i++) {
        if (elapsedMs >= pts[i].t && elapsedMs <= pts[i + 1].t) {
          const segDur = pts[i + 1].t - pts[i].t;
          const u = segDur > 0 ? (elapsedMs - pts[i].t) / segDur : 0;
          curX = pts[i].x + (pts[i + 1].x - pts[i].x) * u;
          curY = pts[i].y + (pts[i + 1].y - pts[i].y) * u;
          showCursor = true;
          break;
        }
      }
    }
  }

  // Target highlight outline
  const [rx, ry, rw, rh] = activeBeat.target_rect;
  const hasTarget = rw > 0 && rh > 0 && activeBeat.target !== 'end-card-logo';
  const highlightOpacity = hasTarget
    ? interpolate(beatRelFrame, [0, 8, 38, 48], [0, 1, 1, 0], clamp)
    : 0;

  // Chapter label badge (visible for first 45 frames = 1.5s of chapter)
  const labelOpacity = interpolate(chRelFrame, [0, 8, 38, 46], [0, 1, 1, 0], clamp);

  // Subtitle fade in / out per beat
  const subOpacity = interpolate(
    beatRelFrame,
    [0, 6, activeBeat.dur_frames - 6, activeBeat.dur_frames],
    [0, 1, 1, 0],
    clamp
  );

  // End card overlay on last beat
  const isEndBeat = activeBeat.id === 'C11b4';
  const endCardOpacity = isEndBeat
    ? interpolate(beatRelFrame, [20, 45], [0, 1], clamp)
    : 0;

  return (
    <AbsoluteFill style={{backgroundColor: '#0E0F12', fontFamily: FONT, overflow: 'hidden'}}>
      {/* Background audio tracks */}
      <Audio src={staticFile('soundtrack-linkedin.wav')} volume={0.12} />

      {/* Voice audio sequenced per beat */}
      {allBeats.map((b) => (
        <Sequence key={b.id} from={b.start_frame} durationInFrames={b.dur_frames}>
          <Audio src={staticFile(b.voice_file)} volume={1.0} />
        </Sequence>
      ))}

      {/* Video layer transformed by dynamic camera */}
      <div
        style={{
          position: 'absolute',
          left: 0,
          top: 0,
          width: 2856,
          height: 1608,
          transformOrigin: '0 0',
          transform: `translate(${currentCam.tx}px, ${currentCam.ty}px) scale(${currentCam.s})`,
        }}
      >
        {/* Render each continuous scene video clip */}
        {((timeline as any).scenes || []).map((sc: any) => (
          <Sequence key={`scene-${sc.id}`} from={sc.start_frame} durationInFrames={sc.dur_frames}>
            <OffthreadVideo
              src={staticFile(sc.clip_src)}
              playbackRate={sc.playback_rate}
              style={{
                width: 2856,
                height: 1608,
                objectFit: 'contain',
              }}
            />
          </Sequence>
        ))}

        {/* Dynamic target highlight outline */}
        {hasTarget && (
          <div
            style={{
              position: 'absolute',
              left: rx,
              top: ry,
              width: rw,
              height: rh,
              border: '3px solid rgba(59, 130, 246, 0.85)',
              borderRadius: 8,
              boxShadow: '0 0 16px rgba(59, 130, 246, 0.4)',
              opacity: highlightOpacity,
              pointerEvents: 'none',
              transition: 'opacity 0.2s ease',
            }}
          />
        )}

        {/* Vector cursor and click ripple */}
        {showCursor && (
          <div
            style={{
              position: 'absolute',
              left: curX,
              top: curY,
              pointerEvents: 'none',
              zIndex: 50,
            }}
          >
            <svg
              width="28"
              height="28"
              viewBox="0 0 24 24"
              style={{
                filter: 'drop-shadow(0 2px 5px rgba(0,0,0,0.5))',
                transform: 'translate(-2px, -2px)',
              }}
            >
              <path
                d="M2 2 L2 20 L6.8 15.2 L11.5 24 L14.5 22.4 L9.8 13.6 L15.5 13.6 Z"
                fill="#FFFFFF"
                stroke="#0E0F12"
                strokeWidth="1.6"
                strokeLinejoin="round"
              />
            </svg>
          </div>
        )}

        {/* Click ripple animations */}
        {activeBeat.clicks &&
          activeBeat.clicks.map((clk, idx) => {
            const clkFrame = Math.round((clk.t / 1000) * 30);
            const clkAge = beatRelFrame - clkFrame;
            if (clkAge < 0 || clkAge > 16) return null;
            const r = interpolate(clkAge, [0, 16], [4, 30], clamp);
            const op = interpolate(clkAge, [0, 16], [0.85, 0], clamp);
            return (
              <div
                key={`click-${idx}`}
                style={{
                  position: 'absolute',
                  left: clk.x - r,
                  top: clk.y - r,
                  width: r * 2,
                  height: r * 2,
                  borderRadius: '50%',
                  border: '2.5px solid rgba(37, 99, 235, 0.9)',
                  opacity: op,
                  pointerEvents: 'none',
                }}
              />
            );
          })}
      </div>

      {/* Chapter title badge (top left) */}
      <div
        style={{
          position: 'absolute',
          top: 36,
          left: 40,
          background: 'rgba(14, 15, 18, 0.75)',
          backdropFilter: 'blur(8px)',
          color: '#F3F4F6',
          fontSize: aspect === '16x9' ? 18 : 20,
          fontWeight: 600,
          padding: '8px 18px',
          borderRadius: 10,
          opacity: labelOpacity,
          border: '1px solid rgba(255, 255, 255, 0.12)',
          pointerEvents: 'none',
        }}
      >
        {activeChapter.title}
      </div>

      {/* Burned-in Turkish subtitles (centered in bottom 20% area) */}
      {activeBeat.subtitle_tr && (
        <div
          style={{
            position: 'absolute',
            bottom: aspect === '16x9' ? 44 : 64,
            left: 0,
            right: 0,
            display: 'flex',
            justifyContent: 'center',
            alignItems: 'center',
            pointerEvents: 'none',
            zIndex: 100,
          }}
        >
          <div
            style={{
              background: 'rgba(14, 15, 18, 0.86)',
              backdropFilter: 'blur(10px)',
              color: '#FFFFFF',
              fontSize: aspect === '16x9' ? 42 : 46,
              fontWeight: 600,
              lineHeight: 1.36,
              padding: '12px 32px',
              borderRadius: 16,
              textAlign: 'center',
              whiteSpace: 'pre-line',
              boxShadow: '0 6px 24px rgba(0,0,0,0.55)',
              maxWidth: aspect === '16x9' ? 1350 : 960,
              border: '1px solid rgba(255, 255, 255, 0.1)',
              opacity: subOpacity,
            }}
          >
            {activeBeat.subtitle_tr}
          </div>
        </div>
      )}

      {/* Full-bleed End Card on final beat */}
      {isEndBeat && (
        <div
          style={{
            position: 'absolute',
            inset: 0,
            backgroundColor: '#0E0F12',
            display: 'flex',
            flexDirection: 'column',
            justifyContent: 'center',
            alignItems: 'center',
            opacity: endCardOpacity,
            zIndex: 150,
            textAlign: 'center',
            padding: 40,
          }}
        >
          <div
            style={{
              fontSize: aspect === '16x9' ? 72 : 64,
              fontWeight: 800,
              letterSpacing: '-0.02em',
              color: '#FFFFFF',
              marginBottom: 16,
            }}
          >
            MailStudio <span style={{color: '#3B82F6'}}>1.8</span>
          </div>

          <div
            style={{
              fontSize: aspect === '16x9' ? 32 : 30,
              fontWeight: 500,
              color: '#9CA3AF',
              marginBottom: 44,
            }}
          >
            The AI Email Client for Engineers & Manufacturing
          </div>

          <div
            style={{
              display: 'flex',
              gap: 20,
              marginBottom: 50,
              fontSize: aspect === '16x9' ? 22 : 20,
              color: '#D1D5DB',
            }}
          >
            <div style={{background: 'rgba(255,255,255,0.06)', padding: '10px 22px', borderRadius: 10, border: '1px solid rgba(255,255,255,0.1)'}}>
              Free & Open Source
            </div>
            <div style={{background: 'rgba(255,255,255,0.06)', padding: '10px 22px', borderRadius: 10, border: '1px solid rgba(255,255,255,0.1)'}}>
              No Telemetry
            </div>
            <div style={{background: 'rgba(255,255,255,0.06)', padding: '10px 22px', borderRadius: 10, border: '1px solid rgba(255,255,255,0.1)'}}>
              Offline 3D CAD & Drawing Tools
            </div>
          </div>

          <div
            style={{
              fontSize: aspect === '16x9' ? 26 : 24,
              fontWeight: 600,
              color: '#60A5FA',
              fontFamily: 'monospace',
              background: 'rgba(59, 130, 246, 0.1)',
              padding: '12px 28px',
              borderRadius: 12,
              border: '1px solid rgba(59, 130, 246, 0.3)',
            }}
          >
            github.com/TRMachinist/MailStudio-AI-Email-Client
          </div>
        </div>
      )}
    </AbsoluteFill>
  );
};
