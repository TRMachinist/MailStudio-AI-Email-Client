import React from 'react';
import {AbsoluteFill, Audio, Img, OffthreadVideo, Sequence, interpolate, spring, staticFile, useCurrentFrame, useVideoConfig, Easing} from 'remotion';
import en from './timeline-en.json';
import tr from './timeline-tr.json';
import {TEXT} from './text';

const C = {bg0: '#060A13', bg1: '#0C1628', ink: '#EAF2FF', dim: '#9FB3CF', blue: '#2F8CFF', cyan: '#33C8FF', orange: '#FF8A3D', line: 'rgba(120,170,255,0.10)'};
const FONT = "Inter, 'Inter Display', 'DejaVu Sans', sans-serif";
const clamp = {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'} as const;
const ease = Easing.bezier(0.65, 0, 0.35, 1);

type Seg = {src: string; from: number; dur: number; rate: number; start: number; w: number; h: number; cam?: Cam[]};
type Cam = {t: number; cx: number; cy: number; z: number};
type Scene = {id: string; from: number; dur: number; voiceFrom: number; voiceFrames: number; parts: string[]; segs: Seg[]; cam: Cam[]; still?: string};
type TL = {totalFrames: number; scenes: Scene[]; audio: string};

const useL = () => {
  const {width, height, fps} = useVideoConfig();
  const V = height > width;
  return V
    ? {V, width, height, fps, win: {x: 40, y: 700, w: 1000, h: 563}, bar: 26}
    : {V, width, height, fps, win: {x: 500, y: 118, w: 1360, h: 765}, bar: 32};
};

const Background: React.FC = () => {
  const f = useCurrentFrame();
  const {width, height} = useL();
  const shift = (f * 0.4) % 80;
  const gx = 50 + 18 * Math.sin(f / 140), gy = 40 + 12 * Math.cos(f / 170);
  return (
    <AbsoluteFill>
      <AbsoluteFill style={{background: `radial-gradient(1200px 800px at ${gx}% ${gy}%, rgba(40,110,220,0.30), transparent 60%), radial-gradient(900px 700px at ${100 - gx}% ${100 - gy}%, rgba(255,138,61,0.10), transparent 60%), linear-gradient(180deg, ${C.bg1}, ${C.bg0})`}} />
      <svg width={width} height={height} style={{position: 'absolute'}}>
        <defs>
          <pattern id="g" width="80" height="80" patternUnits="userSpaceOnUse" patternTransform={`translate(${shift} ${shift})`}>
            <path d="M 80 0 L 0 0 0 80" fill="none" stroke={C.line} strokeWidth="1" />
          </pattern>
        </defs>
        <rect width="100%" height="100%" fill="url(#g)" />
      </svg>
      <AbsoluteFill style={{background: 'radial-gradient(ellipse at center, transparent 55%, rgba(0,0,0,0.55) 100%)'}} />
    </AbsoluteFill>
  );
};

/** Camera over a clip: keys in normalized source coordinates, eased, clamped so the frame is always filled. */
const Camera: React.FC<{cam: Cam[]; dur: number; iw: number; ih: number; w: number; h: number; children: React.ReactNode}> = ({cam, dur, iw, ih, w, h, children}) => {
  const f = useCurrentFrame();
  const t = f / Math.max(1, dur);
  const ks = cam.length ? cam : [{t: 0, cx: 0.5, cy: 0.5, z: 1}];
  const pick = (sel: (k: Cam) => number) => (ks.length === 1 ? sel(ks[0]) : interpolate(t, ks.map((k) => k.t), ks.map(sel), {...clamp, easing: ease}));
  const fit = Math.min(w / iw, h / ih);
  const z = pick((k) => k.z), s = fit * z;
  let tx = w / 2 - pick((k) => k.cx) * iw * s, ty = h / 2 - pick((k) => k.cy) * ih * s;
  if (iw * s >= w) tx = Math.min(0, Math.max(w - iw * s, tx)); else tx = (w - iw * s) / 2;
  if (ih * s >= h) ty = Math.min(0, Math.max(h - ih * s, ty)); else ty = (h - ih * s) / 2;
  return (
    <div style={{position: 'absolute', inset: 0, overflow: 'hidden', background: '#eef1f5'}}>
      <div style={{position: 'absolute', left: 0, top: 0, width: iw, height: ih, transformOrigin: '0 0', transform: `translate(${tx}px, ${ty}px) scale(${s})`}}>{children}</div>
    </div>
  );
};

const AppWindow: React.FC<{sc: Scene}> = ({sc}) => {
  const {win, bar} = useL();
  const f = useCurrentFrame();
  const o = interpolate(f, [0, 6], [0.0, 1], clamp);
  return (
    <div style={{position: 'absolute', left: win.x, top: win.y, width: win.w, height: win.h + bar, borderRadius: 14, overflow: 'hidden', background: '#11161f', boxShadow: '0 40px 120px rgba(0,0,0,0.65), 0 0 0 1px rgba(140,190,255,0.20)'}}>
      <div style={{height: bar, display: 'flex', alignItems: 'center', gap: 8, padding: '0 14px', background: 'linear-gradient(180deg,#1d2431,#151b25)'}}>
        {['#ff5f57', '#febc2e', '#28c840'].map((c) => <div key={c} style={{width: 11, height: 11, borderRadius: 6, background: c, opacity: 0.85}} />)}
      </div>
      <div style={{position: 'absolute', left: 0, top: bar, width: win.w, height: win.h, opacity: o}}>
        {sc.still ? (
          <Camera cam={sc.cam} dur={sc.dur} iw={1680} ih={946} w={win.w} h={win.h}>
            <Img src={staticFile(sc.still)} style={{width: 1680, height: 946}} />
          </Camera>
        ) : (
          sc.segs.map((s, i) => (
            <Sequence key={i} from={s.from} durationInFrames={s.dur} layout="none">
              <Camera cam={s.cam ?? sc.cam} dur={s.cam ? s.dur : sc.dur} iw={s.w} ih={s.h} w={win.w} h={win.h}>
                <OffthreadVideo src={staticFile(s.src)} playbackRate={s.rate} startFrom={s.start} muted style={{width: s.w, height: s.h}} />
              </Camera>
            </Sequence>
          ))
        )}
      </div>
    </div>
  );
};

const Chip: React.FC<{children: React.ReactNode; size: number}> = ({children, size}) => (
  <div style={{display: 'inline-flex', padding: `${size * 0.35}px ${size * 0.8}px`, borderRadius: 999, border: `1.5px solid ${C.cyan}`, color: C.cyan, fontFamily: FONT, fontWeight: 700, fontSize: size, letterSpacing: size * 0.12, textTransform: 'uppercase', background: 'rgba(10,20,40,0.55)'}}>{children}</div>
);

const Words: React.FC<{text: string; size: number; delay: number; color?: string; weight?: number}> = ({text, size, delay, color = C.ink, weight = 800}) => {
  const f = useCurrentFrame();
  const {fps} = useL();
  return (
    <div style={{fontFamily: FONT, fontWeight: weight, fontSize: size, lineHeight: 1.1, color, letterSpacing: -size * 0.02}}>
      {text.split(' ').map((w, i) => {
        const p = spring({frame: f - delay - i * 2, fps, config: {damping: 18, stiffness: 140, mass: 0.7}});
        return (
          <span key={i} style={{display: 'inline-block', overflow: 'hidden', verticalAlign: 'bottom', paddingBottom: size * 0.08, marginRight: size * 0.24}}>
            <span style={{display: 'inline-block', transform: `translateY(${(1 - p) * 110}%)`, opacity: p}}>{w}</span>
          </span>
        );
      })}
    </div>
  );
};

const Side: React.FC<{lang: 'en' | 'tr'; id: string; dur: number}> = ({lang, id, dur}) => {
  const f = useCurrentFrame();
  const {V, fps} = useL();
  const t = (TEXT[lang] as any)[id];
  if (!t) return null;
  const out = interpolate(f, [dur - 6, dur], [1, 0], clamp);
  const p = spring({frame: f - 2, fps, config: {damping: 20}});
  const subO = interpolate(f, [14, 24], [0, 1], clamp);
  const pos: React.CSSProperties = V ? {left: 60, right: 60, top: 170} : {left: 70, width: 400, top: 300};
  return (
    <div style={{position: 'absolute', ...pos, opacity: out}}>
      <div style={{opacity: p, transform: `translateX(${(1 - p) * -30}px)`}}><Chip size={V ? 26 : 18}>{t.chip}</Chip></div>
      <div style={{marginTop: V ? 26 : 18}}><Words text={t.title} size={V ? 78 : 50} delay={5} /></div>
      {t.sub ? <div style={{opacity: subO, color: C.dim, fontFamily: FONT, fontSize: V ? 34 : 23, fontWeight: 500, marginTop: 10, lineHeight: 1.3}}>{t.sub}</div> : null}
      <div style={{display: 'flex', flexWrap: 'wrap', gap: 10, marginTop: V ? 30 : 26}}>
        {(t.pills ?? []).map((x: string, i: number) => {
          const q = spring({frame: f - 16 - i * 4, fps, config: {damping: 16}});
          return <div key={x} style={{opacity: q, transform: `scale(${0.8 + 0.2 * q})`, fontFamily: FONT, fontWeight: 600, fontSize: V ? 28 : 19, color: C.ink, padding: V ? '8px 18px' : '6px 14px', borderRadius: 10, background: 'rgba(47,140,255,0.16)', border: '1px solid rgba(120,180,255,0.35)'}}>{x}</div>;
        })}
      </div>
    </div>
  );
};

const Logo: React.FC<{size: number}> = ({size}) => <Img src={staticFile('logo.png')} style={{width: size, height: size, borderRadius: size * 0.22, boxShadow: '0 10px 40px rgba(47,140,255,0.45)'}} />;

const Intro: React.FC<{lang: 'en' | 'tr'; dur: number}> = ({lang, dur}) => {
  const f = useCurrentFrame();
  const {V, fps} = useL();
  const p = spring({frame: f, fps, config: {damping: 14, stiffness: 110}});
  const out = interpolate(f, [dur - 6, dur], [1, 0], clamp);
  const tag = lang === 'en' ? 'The open-source AI email client for engineers & machinists' : 'İmalatçılar için yapay zekâlı, açık kaynak mail programı';
  const pos: React.CSSProperties = V ? {left: 60, right: 60, top: 150} : {left: 70, width: 410, top: 250};
  return (
    <div style={{position: 'absolute', ...pos, opacity: out}}>
      <div style={{transform: `scale(${0.6 + 0.4 * p})`, transformOrigin: 'left center', opacity: p}}><Logo size={V ? 130 : 96} /></div>
      <div style={{marginTop: 26}}><Words text="MailStudio" size={V ? 110 : 76} delay={6} /></div>
      <div style={{marginTop: 2}}><Words text="for TRMachinist" size={V ? 56 : 38} delay={12} color={C.cyan} weight={700} /></div>
      <div style={{marginTop: 22, color: C.dim, fontFamily: FONT, fontSize: V ? 36 : 25, fontWeight: 500, lineHeight: 1.3, opacity: interpolate(f, [22, 34], [0, 1], clamp)}}>{tag}</div>
    </div>
  );
};

const Outro: React.FC<{lang: 'en' | 'tr'}> = ({lang}) => {
  const f = useCurrentFrame();
  const {V, fps} = useL();
  const o = (TEXT[lang] as any).outro;
  const p = spring({frame: f, fps, config: {damping: 14}});
  const pulse = 1 + 0.03 * Math.sin(f / 5);
  return (
    <AbsoluteFill style={{alignItems: 'center', justifyContent: 'center', flexDirection: 'column', fontFamily: FONT, textAlign: 'center', padding: V ? 60 : 0}}>
      <div style={{transform: `scale(${0.6 + 0.4 * p})`, opacity: p}}><Logo size={V ? 170 : 120} /></div>
      <div style={{marginTop: 26}}><Words text="MailStudio for TRMachinist" size={V ? 76 : 70} delay={4} /></div>
      <div style={{color: C.cyan, fontSize: V ? 44 : 38, fontWeight: 700, marginTop: 8, opacity: interpolate(f, [10, 20], [0, 1], clamp)}}>{o.line1}</div>
      <div style={{color: C.dim, fontSize: V ? 30 : 26, marginTop: 10, opacity: interpolate(f, [14, 24], [0, 1], clamp)}}>{o.line2}</div>
      <div style={{display: 'flex', flexWrap: 'wrap', justifyContent: 'center', gap: 12, marginTop: 34, maxWidth: V ? 940 : 1500}}>
        {o.tiles.map((t: string, i: number) => {
          const q = spring({frame: f - 14 - i * 3, fps, config: {damping: 15}});
          return <div key={t} style={{opacity: q, transform: `translateY(${(1 - q) * 30}px)`, padding: V ? '12px 22px' : '10px 22px', borderRadius: 12, background: 'rgba(47,140,255,0.15)', border: '1px solid rgba(120,180,255,0.35)', color: C.ink, fontWeight: 600, fontSize: V ? 30 : 24}}>{t}</div>;
        })}
      </div>
      <div style={{marginTop: 40, opacity: interpolate(f, [30, 40], [0, 1], clamp), transform: `scale(${pulse})`, padding: V ? '18px 26px' : '16px 34px', borderRadius: 16, background: 'linear-gradient(90deg, #1f6fe0, #33C8FF)', color: '#fff', fontWeight: 800, fontSize: V ? 30 : 32, boxShadow: '0 16px 50px rgba(47,140,255,0.45)'}}>{o.url}</div>
      <div style={{marginTop: 20, color: '#FFD36A', fontWeight: 700, fontSize: V ? 36 : 32, opacity: interpolate(f, [38, 48], [0, 1], clamp)}}>{o.star}</div>
    </AbsoluteFill>
  );
};

const Captions: React.FC<{tl: TL}> = ({tl}) => {
  const f = useCurrentFrame();
  const {V} = useL();
  let line: string | null = null, local = 0, outro = false;
  for (const s of tl.scenes) {
    if (f >= s.voiceFrom && f < s.voiceFrom + s.voiceFrames && s.id === 's10') outro = true;
    if (f < s.voiceFrom || f >= s.voiceFrom + s.voiceFrames) continue;
    const tot = s.parts.reduce((a, p) => a + p.length, 0);
    let cur = s.voiceFrom;
    for (const p of s.parts) {
      const d = (s.voiceFrames * p.length) / tot;
      if (f >= cur && f < cur + d) { line = p; local = f - cur; break; }
      cur += d;
    }
  }
  if (!line) return null;
  const o = interpolate(local, [0, 4], [0, 1], clamp);
  const st: React.CSSProperties = V ? {left: 50, right: 50, top: outro ? 1700 : 1330} : outro ? {left: 0, right: 0, top: 985} : {left: 500, width: 1360, top: 935};
  return (
    <div style={{position: 'absolute', ...st, textAlign: 'center', opacity: o}}>
      <span style={{fontFamily: FONT, fontWeight: 650, fontSize: V ? 44 : 31, lineHeight: 1.4, color: '#fff', background: 'rgba(5,10,20,0.78)', padding: '6px 16px', borderRadius: 10, boxDecorationBreak: 'clone', WebkitBoxDecorationBreak: 'clone'}}>{line}</span>
    </div>
  );
};

export const Film: React.FC<{lang: 'en' | 'tr'}> = ({lang}) => {
  const tl = (lang === 'en' ? en : tr) as unknown as TL;
  const f = useCurrentFrame();
  const {width, V} = useL();
  const last = tl.scenes[tl.scenes.length - 1];
  return (
    <AbsoluteFill style={{background: C.bg0}}>
      <Background />
      {tl.scenes.map((s) => (
        <Sequence key={s.id} from={s.from} durationInFrames={s.dur} name={s.id}>
          {s.id === 's10' ? <Outro lang={lang} /> : (
            <>
              <AppWindow sc={s} />
              {s.id === 's01' ? <Intro lang={lang} dur={s.dur} /> : <Side lang={lang} id={s.id} dur={s.dur} />}
            </>
          )}
        </Sequence>
      ))}
      {f < last.from ? <div style={{position: 'absolute', right: V ? 40 : 36, top: V ? 60 : 34, fontFamily: FONT, fontSize: V ? 20 : 15, color: 'rgba(200,215,235,0.75)', letterSpacing: 1}}>{TEXT[lang].badge}</div> : null}
      <Captions tl={tl} />
      <div style={{position: 'absolute', left: 0, bottom: 0, height: V ? 8 : 5, width: (width * f) / tl.totalFrames, background: `linear-gradient(90deg, ${C.cyan}, ${C.orange})`}} />
      <Audio src={staticFile(tl.audio)} />
    </AbsoluteFill>
  );
};
