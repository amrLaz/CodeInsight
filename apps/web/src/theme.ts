export type Theme = 'light' | 'dark';

export const THEME_STORAGE_KEY = 'codeinsights-theme-v1';
export const THEME_COLOR: Record<Theme, string> = {
  light: '#005eb8',
  dark: '#101923',
};

export function resolveTheme(storedTheme: string | null, prefersDark: boolean): Theme {
  if (storedTheme === 'light' || storedTheme === 'dark') return storedTheme;
  return prefersDark ? 'dark' : 'light';
}

export function readStoredTheme(storage: Pick<Storage, 'getItem'>): Theme | null {
  try {
    const storedTheme = storage.getItem(THEME_STORAGE_KEY);
    return storedTheme === 'light' || storedTheme === 'dark' ? storedTheme : null;
  } catch {
    return null;
  }
}

export function persistTheme(storage: Pick<Storage, 'setItem'>, theme: Theme): boolean {
  try {
    storage.setItem(THEME_STORAGE_KEY, theme);
    return true;
  } catch {
    return false;
  }
}

export function nextTheme(theme: Theme): Theme {
  return theme === 'dark' ? 'light' : 'dark';
}

export function applyTheme(theme: Theme, root: HTMLElement, themeColor: HTMLMetaElement | null): void {
  root.dataset.theme = theme;
  root.style.colorScheme = theme;
  themeColor?.setAttribute('content', THEME_COLOR[theme]);
}
