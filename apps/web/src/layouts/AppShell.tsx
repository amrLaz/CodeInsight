import type { ReactNode } from 'react';
import MainNavigation from './MainNavigation';
import ThemeToggle from '../components/common/ThemeToggle';
import { Button } from '../components/ui';
import type { AppTab, SessionUser } from '../types/api';

type Props = {
  children: ReactNode;
  visibleTabs: Array<{ id: AppTab; label: string }>;
  activeTab: AppTab;
  onSelectTab: (tab: AppTab) => void;
  sessionUser: SessionUser | null;
  accountName: string;
  photoUrl: string | null;
  photoFailed: boolean;
  onPhotoError: () => void;
  onAccount: () => void;
  onSignOut: () => void;
  releaseName: string;
};

export default function AppShell({
  children,
  visibleTabs,
  activeTab,
  onSelectTab,
  sessionUser,
  accountName,
  photoUrl,
  photoFailed,
  onPhotoError,
  onAccount,
  onSignOut,
  releaseName,
}: Props) {
  return (
    <main className="app-page">
      <a className="skip-link" href="#workspace-content">
        Skip to content
      </a>
      <header className="topbar">
        <div className="brand-area">
          <div className="logo">CodeInsights</div>
        </div>
        <MainNavigation tabs={visibleTabs} activeTab={activeTab} onSelect={onSelectTab} />
        <div className="status-area">
          <ThemeToggle />
          {sessionUser && (
            <>
              <Button
                type="button"
                className="user-avatar"
                onClick={() => onAccount()}
                title={`${accountName} — personal access tokens`}
                aria-label={`Account of ${accountName}, personal access tokens`}
                aria-haspopup="dialog"
              >
                {photoUrl && !photoFailed ? (
                  <img src={photoUrl} alt="" onError={() => onPhotoError()} />
                ) : (
                  accountName
                    .trim()
                    .split(/\s+/)
                    .filter((part) => !part.startsWith('('))
                    .slice(0, 2)
                    .map((part) => Array.from(part)[0])
                    .join('')
                    .toLocaleUpperCase() || '?'
                )}
              </Button>
              <Button type="button" className="signout-btn" onClick={onSignOut} title="Sign out" aria-label="Sign out">
                <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                  <path d="M9 4H4v16h5M9 12h11m-4-4 4 4-4 4" />
                </svg>
              </Button>
            </>
          )}
        </div>
      </header>

      {children}
      <footer className="footer">
        <span>© {new Date().getFullYear()} Upcoop</span>
        <span>CodeInsights · {releaseName}</span>
      </footer>
    </main>
  );
}
