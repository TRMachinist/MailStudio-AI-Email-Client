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
import timeline from './timeline-linkedin.json';

const FONT = "Inter, -apple-system, BlinkMacSystemFont, 'Segoe UI Variable', 'Segoe UI', Roboto, sans-serif";
const clamp = {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'} as const;
const easeInOut = Easing.bezier(0.42, 0, 0.58, 1);

type Cam = {t: number; cx: number; cy: number; z: number};
type Seg = {src: string; from: number; dur: number; rate: number; start: number; w: number; h: number; cam?: Cam[]};
type Scene = {
  id: string;
  label: string;
  from: number;
  dur: number;
  voiceFrom: number;
  voiceFrames: number;
  voice: string;
  en_text: string;
  tr_sub: string;
  segs: Seg[];
  cam: Cam[];
};

interface LinkedInProps {
  aspect?: '16x9' | '4x5';
}

/** Camera pan & zoom over recorded footage */
const Camera: React.FC<{
  cam: Cam[];
  dur: number;
  iw: number;
  ih: number;
  w: number;
  h: number;
  children: React.ReactNode;
}> = ({cam, dur, iw, ih, w, h, children}) => {
  const f = useCurrentFrame();
  const t = Math.min(1, Math.max(0, f / Math.max(1, dur)));
  const ks = cam.length ? cam : [{t: 0, cx: 0.5, cy: 0.5, z: 1}];

  const pick = (sel: (k: Cam) => number) =>
    ks.length === 1
      ? sel(ks[0])
      : interpolate(
          t,
          ks.map((k) => k.t),
          ks.map(sel),
          {...clamp, easing: easeInOut}
        );

  const fit = Math.max(w / iw, h / ih);
  const z = Math.min(1.8, Math.max(1.0, pick((k) => k.z)));
  const s = fit * z;

  let tx = w / 2 - pick((k) => k.cx) * iw * s;
  let ty = h / 2 - pick((k) => k.cy) * ih * s;

  if (iw * s >= w) tx = Math.min(0, Math.max(w - iw * s, tx));
  else tx = (w - iw * s) / 2;

  if (ih * s >= h) ty = Math.min(0, Math.max(h - ih * s, ty));
  else ty = (h - ih * s) / 2;

  return (
    <div style={{position: 'absolute', inset: 0, overflow: 'hidden', background: '#0E0F12'}}>
      <div
        style={{
          position: 'absolute',
          left: 0,
          top: 0,
          width: iw,
          height: ih,
          transformOrigin: '0 0',
          transform: `translate(${tx}px, ${ty}px) scale(${s})`,
        }}
      >
        {children}
      </div>
    </div>
  );
};

export const LinkedInFilm: React.FC<LinkedInProps> = ({aspect = '16x9'}) => {
  const {width, height} = useVideoConfig();
  const f = useCurrentFrame();
  const scenes = (timeline as any).scenes as Scene[];

  // Find active scene
  const activeScene = scenes.find((s) => f >= s.from && f < s.from + s.dur) ?? scenes[0];
  const sceneFrame = f - activeScene.from;

  // Chapter label fade in / out (shown for first 2 seconds = 60 frames)
  const labelOpacity = interpolate(
    sceneFrame,
    [0, 10, 50, 60],
    [0, 1, 1, 0],
    clamp
  );

  // Subtitle visibility (matches voice timing with smooth fade)
  const voiceRelStart = activeScene.voiceFrom - activeScene.from;
  const subStart = Math.max(0, voiceRelStart - 4);
  const subEnd = Math.min(activeScene.dur - 6, subStart + activeScene.voiceFrames + 18);
  const subOpacity = interpolate(
    sceneFrame,
    [subStart, subStart + 8, subEnd - 8, subEnd],
    [0, 1, 1, 0],
    clamp
  );

  // End card overlay on L13 (last 4 seconds)
  const isEndScene = activeScene.id === 'L13';
  const endCardOpacity = isEndScene
    ? interpolate(sceneFrame, [60, 100], [0, 1], clamp)
    : 0;

  return (
    <AbsoluteFill style={{backgroundColor: '#0E0F12', fontFamily: FONT}}>
      {/* Soundtrack with ducked ambient music & en-US-AndrewNeural voice */}
      <Audio src={staticFile((timeline as any).audio ?? 'soundtrack-linkedin.wav')} />

      {/* Video footage across scenes */}
      {scenes.map((sc) => (
        <Sequence key={sc.id} from={sc.from} durationInFrames={sc.dur}>
          <div style={{position: 'absolute', inset: 0}}>
            {sc.segs.map((seg, i) => (
              <Sequence key={i} from={seg.from} durationInFrames={seg.dur}>
                <Camera
                  cam={seg.cam ?? sc.cam}
                  dur={seg.dur}
                  iw={seg.w}
                  ih={seg.h}
                  w={width}
                  h={height}
                >
                  <OffthreadVideo
                    src={staticFile(seg.src)}
                    playbackRate={seg.rate}
                    startFrom={seg.start}
                    muted
                    style={{width: seg.w, height: seg.h}}
                  />
                </Camera>
              </Sequence>
            ))}
          </div>
        </Sequence>
      ))}

      {/* Top watermark badge */}
      <div
        style={{
          position: 'absolute',
          top: 24,
          right: 28,
          background: 'rgba(0, 0, 0, 0.45)',
          backdropFilter: 'blur(8px)',
          border: '1px solid rgba(255, 255, 255, 0.12)',
          borderRadius: 10,
          padding: '6px 14px',
          color: 'rgba(255, 255, 255, 0.65)',
          fontSize: 13,
          fontWeight: 500,
          letterSpacing: '0.04em',
          textTransform: 'uppercase',
          zIndex: 40,
        }}
      >
        Real app · demo data
      </div>

      {/* Top chapter pill (first 2s of scene) */}
      {labelOpacity > 0 && (
        <div
          style={{
            position: 'absolute',
            top: 24,
            left: 28,
            background: 'rgba(0, 0, 0, 0.60)',
            backdropFilter: 'blur(10px)',
            border: '1px solid rgba(255, 255, 255, 0.15)',
            borderRadius: 12,
            padding: '7px 18px',
            color: '#FFFFFF',
            fontSize: 16,
            fontWeight: 600,
            opacity: labelOpacity,
            zIndex: 40,
          }}
        >
          {activeScene.label}
        </div>
      )}

      {/* Subtitles: Turkish burned-in at bottom center */}
      {subOpacity > 0 && activeScene.tr_sub && (
        <div
          style={{
            position: 'absolute',
            left: 0,
            right: 0,
            bottom: aspect === '4x5' ? 120 : 75,
            display: 'flex',
            justifyContent: 'center',
            alignItems: 'center',
            padding: '0 40px',
            zIndex: 50,
            opacity: subOpacity,
          }}
        >
          <div
            style={{
              background: 'rgba(0, 0, 0, 0.65)',
              backdropFilter: 'blur(12px)',
              border: '1px solid rgba(255, 255, 255, 0.14)',
              borderRadius: 22,
              padding: aspect === '4x5' ? '12px 24px' : '10px 28px',
              maxWidth: aspect === '4x5' ? 960 : 1240,
              color: '#FFFFFF',
              fontSize: aspect === '4x5' ? 36 : 40,
              fontWeight: 600,
              textAlign: 'center',
              lineHeight: 1.35,
              textShadow: '0 2px 8px rgba(0, 0, 0, 0.75)',
            }}
          >
            {activeScene.tr_sub}
          </div>
        </div>
      )}

      {/* End Card Overlay on Chapter 13 */}
      {endCardOpacity > 0 && (
        <div
          style={{
            position: 'absolute',
            inset: 0,
            background: 'rgba(14, 15, 18, 0.92)',
            backdropFilter: 'blur(16px)',
            display: 'flex',
            flexDirection: 'column',
            justifyContent: 'center',
            alignItems: 'center',
            color: '#FFFFFF',
            opacity: endCardOpacity,
            zIndex: 60,
          }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 16,
              marginBottom: 16,
            }}
          >
            <div
              style={{
                width: 56,
                height: 56,
                borderRadius: 14,
                background: 'linear-gradient(135deg, #0F6CBD, #2B88D8)',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                boxShadow: '0 8px 24px rgba(15, 108, 189, 0.45)',
                fontSize: 28,
                fontWeight: 800,
              }}
            >
              M
            </div>
            <div style={{fontSize: 48, fontWeight: 700, letterSpacing: '-0.02em'}}>
              MailStudio <span style={{color: '#2B88D8'}}>1.8</span>
            </div>
          </div>

          <div
            style={{
              fontSize: 22,
              fontWeight: 400,
              color: 'rgba(255, 255, 255, 0.75)',
              marginBottom: 32,
              textAlign: 'center',
              maxWidth: 700,
            }}
          >
            The open-source email client built for engineers and machinists.
          </div>

          <div
            style={{
              display: 'flex',
              gap: 24,
              marginBottom: 36,
              fontSize: 16,
              color: 'rgba(255, 255, 255, 0.60)',
            }}
          >
            <span>C# .NET 8 / WPF</span>
            <span>·</span>
            <span>Offline First</span>
            <span>·</span>
            <span>Zero Telemetry</span>
            <span>·</span>
            <span>MIT License</span>
          </div>

          <div
            style={{
              background: 'rgba(255, 255, 255, 0.08)',
              border: '1px solid rgba(255, 255, 255, 0.16)',
              borderRadius: 30,
              padding: '12px 32px',
              fontSize: 19,
              fontWeight: 600,
              color: '#FFFFFF',
              letterSpacing: '0.01em',
            }}
          >
            github.com/TRMachinist/MailStudio-AI-Email-Client
          </div>
        </div>
      )}
    </AbsoluteFill>
  );
};
