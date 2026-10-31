import React from 'react';
import {Composition} from 'remotion';
import {Film} from './Film';
import en from './timeline-en.json';
import tr from './timeline-tr.json';
import enNew18 from './timelinenew18-en.json';
import trNew18 from './timelinenew18-tr.json';
import en19 from './timeline19-en.json';
import tr19 from './timeline19-tr.json';

export const RemotionRoot: React.FC = () => (
  <>
    {/* Main Film (120-150s) */}
    <Composition id="FilmEN" component={Film} defaultProps={{lang: 'en'}} durationInFrames={(en as any).totalFrames} fps={30} width={1920} height={1080} />
    <Composition id="FilmTR" component={Film} defaultProps={{lang: 'tr'}} durationInFrames={(tr as any).totalFrames} fps={30} width={1920} height={1080} />
    <Composition id="VertEN" component={Film} defaultProps={{lang: 'en'}} durationInFrames={(en as any).totalFrames} fps={30} width={1080} height={1920} />
    <Composition id="VertTR" component={Film} defaultProps={{lang: 'tr'}} durationInFrames={(tr as any).totalFrames} fps={30} width={1080} height={1920} />

    {/* New in 1.8 Short Film (45-60s) */}
    <Composition id="New18EN" component={Film} defaultProps={{lang: 'en', set: 'v18'}} durationInFrames={(enNew18 as any).totalFrames} fps={30} width={1920} height={1080} />
    <Composition id="New18TR" component={Film} defaultProps={{lang: 'tr', set: 'v18'}} durationInFrames={(trNew18 as any).totalFrames} fps={30} width={1920} height={1080} />
    <Composition id="New18VertEN" component={Film} defaultProps={{lang: 'en', set: 'v18'}} durationInFrames={(enNew18 as any).totalFrames} fps={30} width={1080} height={1920} />
    <Composition id="New18VertTR" component={Film} defaultProps={{lang: 'tr', set: 'v18'}} durationInFrames={(trNew18 as any).totalFrames} fps={30} width={1080} height={1920} />

    {/* 1.9 film (≤ 90 s, every feature in order) */}
    <Composition id="Film19EN" component={Film} defaultProps={{lang: 'en', set: 'v19'}} durationInFrames={(en19 as any).totalFrames} fps={30} width={1920} height={1080} />
    <Composition id="Film19TR" component={Film} defaultProps={{lang: 'tr', set: 'v19'}} durationInFrames={(tr19 as any).totalFrames} fps={30} width={1920} height={1080} />
    <Composition id="Film19VertEN" component={Film} defaultProps={{lang: 'en', set: 'v19'}} durationInFrames={(en19 as any).totalFrames} fps={30} width={1080} height={1920} />
    <Composition id="Film19VertTR" component={Film} defaultProps={{lang: 'tr', set: 'v19'}} durationInFrames={(tr19 as any).totalFrames} fps={30} width={1080} height={1920} />
  </>
);
