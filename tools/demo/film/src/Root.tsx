import React from 'react';
import {Composition} from 'remotion';
import {Film} from './Film';
import {LinkedInFilm} from './LinkedIn';
import en from './timeline-en.json';
import tr from './timeline-tr.json';
import enNew18 from './timelinenew18-en.json';
import trNew18 from './timelinenew18-tr.json';
import linkedinTimeline from './timeline-linkedin.json';

export const RemotionRoot: React.FC = () => (
  <>
    {/* LinkedIn Product Film (120s, 30fps) */}
    <Composition
      id="LinkedIn16x9"
      component={LinkedInFilm}
      defaultProps={{aspect: '16x9'}}
      durationInFrames={(linkedinTimeline as any).totalFrames}
      fps={30}
      width={1920}
      height={1080}
    />
    <Composition
      id="LinkedIn4x5"
      component={LinkedInFilm}
      defaultProps={{aspect: '4x5'}}
      durationInFrames={(linkedinTimeline as any).totalFrames}
      fps={30}
      width={1080}
      height={1350}
    />

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
  </>
);
