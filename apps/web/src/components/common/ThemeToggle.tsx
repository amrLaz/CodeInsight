import { Button } from '../ui';
import { useEffect, useState } from 'react';
import { applyTheme, nextTheme, persistTheme, readStoredTheme, resolveTheme, type Theme } from '@/theme';

type ThemeState = { theme: Theme; manual: boolean };

function browserStoredTheme(): Theme | null {
  try {
    return readStoredTheme(window.localStorage);
  } catch {
    return null;
  }
}

function systemPrefersDark(): boolean {
  return window.matchMedia('(prefers-color-scheme: dark)').matches;
}

function initialTheme(): ThemeState {
  const storedTheme = browserStoredTheme();
  return {
    theme: resolveTheme(storedTheme, systemPrefersDark()),
    manual: storedTheme !== null,
  };
}

export default function ThemeToggle() {
  const [state, setState] = useState(initialTheme);

  useEffect(() => {
    applyTheme(state.theme, document.documentElement, document.querySelector<HTMLMetaElement>('meta[name="theme-color"]'));
  }, [state.theme]);

  useEffect(() => {
    if (state.manual) return;

    const preference = window.matchMedia('(prefers-color-scheme: dark)');
    const followSystemTheme = (event: MediaQueryListEvent) => {
      setState((current) => (current.manual ? current : { theme: resolveTheme(null, event.matches), manual: false }));
    };
    preference.addEventListener('change', followSystemTheme);
    return () => preference.removeEventListener('change', followSystemTheme);
  }, [state.manual]);

  const label = state.theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme';

  return (
    <Button
      type="button"
      className="theme-toggle"
      aria-pressed={state.theme === 'dark'}
      aria-label={label}
      title={label}
      onClick={() => {
        const theme = nextTheme(state.theme);
        try {
          persistTheme(window.localStorage, theme);
        } catch {
          // The current page can still switch when browser storage is unavailable.
        }
        setState({ theme, manual: true });
      }}
    >
      <svg viewBox="0 0 24 24" aria-hidden="true">
        {state.theme === 'dark' ? (
          <>
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2m0 16v2M4.93 4.93l1.42 1.42m11.3 11.3 1.42 1.42M2 12h2m16 0h2M4.93 19.07l1.42-1.42m11.3-11.3 1.42-1.42" />
          </>
        ) : (
          <path d="M20.4 15.5A8.5 8.5 0 0 1 8.5 3.6 8.5 8.5 0 1 0 20.4 15.5Z" />
        )}
      </svg>
    </Button>
  );
}
