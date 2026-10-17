import React from 'react';
import {Composition} from 'remotion';
import {Film} from './Film';
import en from './timeline-en.json';
import tr from './timeline-tr.json';
export const RemotionRoot: React.FC = () => (
  <>
    <Composition id="FilmEN" component={Film} defaultProps={{lang: 'en'}} durationInFrames={(en as any).totalFrames} fps={30} width={1920} height={1080} />
    <Composition id="FilmTR" component={Film} defaultProps={{lang: 'tr'}} durationInFrames={(tr as any).totalFrames} fps={30} width={1920} height={1080} />
    <Composition id="VertEN" component={Film} defaultProps={{lang: 'en'}} durationInFrames={(en as any).totalFrames} fps={30} width={1080} height={1920} />
    <Composition id="VertTR" component={Film} defaultProps={{lang: 'tr'}} durationInFrames={(tr as any).totalFrames} fps={30} width={1080} height={1920} />
  </>
);
