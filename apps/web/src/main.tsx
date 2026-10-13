import React from 'react';
import ReactDOM from 'react-dom/client';
import { App } from './App';
import './styles.css';

const SPLASH_MIN_MS = 600;
const SPLASH_EXIT_MS = 150;
const splashStartedAt = performance.now();
const root = document.getElementById('root')!;

window.addEventListener('vite:preloadError', (event) => {
  event.preventDefault();
  window.location.reload();
});

ReactDOM.createRoot(root).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);

// ponytail: the fixed splash is the only startup choreography; React and its requests start behind it.
requestAnimationFrame(() =>
  requestAnimationFrame(() => {
    const remainingDelay = Math.max(0, SPLASH_MIN_MS - (performance.now() - splashStartedAt));
    window.setTimeout(() => {
      const splash = document.getElementById('startup-splash');
      root.removeAttribute('inert');
      root.removeAttribute('aria-hidden');
      if (!splash) return;

      splash.setAttribute('aria-hidden', 'true');
      if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
        splash.remove();
        return;
      }

      splash.classList.add('is-exiting');
      window.setTimeout(() => splash.remove(), SPLASH_EXIT_MS);
    }, remainingDelay);
  }),
);
