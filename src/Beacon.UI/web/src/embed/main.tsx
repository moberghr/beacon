import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { EmbedView } from './EmbedView';
import { readFrameMarker, takeToken } from './embedClient';
import '../index.css';

// Taken before anything renders: the fragment leaves the address at once and the token stays in memory only.
const token = takeToken();
const frameMarker = readFrameMarker();

// Follows the viewer's colour scheme; the app's own theme setting is not read here.
const darkScheme = window.matchMedia?.('(prefers-color-scheme: dark)');
const applyScheme = () => {
  if (darkScheme?.matches) {
    document.documentElement.dataset.theme = 'dark';
  } else {
    delete document.documentElement.dataset.theme;
  }
};
applyScheme();
darkScheme?.addEventListener?.('change', applyScheme);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <EmbedView token={token} frameMarker={frameMarker} />
  </StrictMode>,
);
