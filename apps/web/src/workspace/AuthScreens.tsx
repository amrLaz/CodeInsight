import { type Dispatch, type RefObject, type SetStateAction } from 'react';
import { Button, Checkbox, TextBox } from '../components/ui';
import ThemeToggle from '../components/common/ThemeToggle';
import type { SessionUser } from '../types/api';
import type { useWorkspaceData } from './useWorkspaceData';

type SignInForm = { email: string; password: string; remember_me: boolean };
type SignInProps = {
  methodsQuery: ReturnType<typeof useWorkspaceData>['methodsQuery'];
  signInForm: SignInForm;
  setSignInForm: Dispatch<SetStateAction<SignInForm>>;
  signInError: string | null;
  signInEmailRef: RefObject<HTMLInputElement | null>;
  signInPasswordRef: RefObject<HTMLInputElement | null>;
  handleSignIn: () => Promise<void>;
  handleEntraSignIn: (mode?: 'interactive' | 'silent') => Promise<void>;
};
type SignOutProps = { sessionUser: SessionUser | null; confirmSignOut: () => void; cancelSignOut: () => void };

export function SignInScreen({ methodsQuery, signInForm, setSignInForm, signInError, signInEmailRef, signInPasswordRef, handleSignIn, handleEntraSignIn }: SignInProps) {
  return (
    <main className="auth-page">
      <ThemeToggle />
      <section className="auth-shell">
        <div className="logo">CodeInsights</div>
        <h1>Sign In</h1>
        <p>Sign in with your organizational account{methodsQuery.data?.local_login_visible ? ' or a local platform account' : ''}.</p>
        {methodsQuery.isLoading && <p role="status">Loading sign-in methods…</p>}
        {methodsQuery.isError && (
          <div className="feedback error" role="alert">
            <p>{methodsQuery.error.message}</p>
            <Button type="button" onClick={() => void methodsQuery.refetch()}>
              Retry
            </Button>
          </div>
        )}
        {methodsQuery.data?.entra_enabled && (
          <div className="auth-card sign-card sso-card">
            <p className="sso-label">Single Sign-On</p>
            <Button type="button" className="sso-btn" onClick={() => void handleEntraSignIn('interactive')}>
              <svg width="20" height="20" viewBox="0 0 21 21" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                <rect x="1" y="1" width="9" height="9" fill="#f25022" />
                <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
                <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
                <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
              </svg>
              Sign in with Microsoft / Entra ID
            </Button>
          </div>
        )}
        {methodsQuery.data?.local_login_visible && (
          <form
            className="auth-card sign-card"
            onSubmit={(event) => {
              event.preventDefault();
              void handleSignIn();
            }}
          >
            <p className="sso-label">Local account</p>
            <div className="form-grid compact">
              <label>
                Email
                <TextBox
                  ref={signInEmailRef}
                  value={signInForm.email}
                  onChange={(event) => setSignInForm((current) => ({ ...current, email: event.target.value }))}
                  type="email"
                  placeholder="name@organization.com"
                  autoComplete="username"
                />
              </label>
              <label>
                Password
                <TextBox
                  ref={signInPasswordRef}
                  type="password"
                  value={signInForm.password}
                  onChange={(event) => setSignInForm((current) => ({ ...current, password: event.target.value }))}
                  placeholder="Enter password"
                  autoComplete="current-password"
                />
              </label>
            </div>
            <label className="signin-remember">
              <Checkbox type="checkbox" checked={signInForm.remember_me} onChange={(event) => setSignInForm((current) => ({ ...current, remember_me: event.target.checked }))} />
              Keep me signed in on this browser
            </label>
            <div className="action-row">
              <Button type="submit" className="primary">
                Sign in
              </Button>
            </div>
          </form>
        )}
        {signInError && (
          <p className="feedback error" role="alert">
            {signInError}
          </p>
        )}
        {methodsQuery.data && !methodsQuery.data.entra_enabled && !methodsQuery.data.local_login_visible && (
          <p className="feedback error" role="alert">
            No sign-in method is currently available.
          </p>
        )}
      </section>
    </main>
  );
}

export function SignOutScreen({ sessionUser, confirmSignOut, cancelSignOut }: SignOutProps) {
  return (
    <main className="auth-page">
      <ThemeToggle />
      <section className="auth-shell">
        <div className="logo">CodeInsights</div>
        <h1>Sign Out</h1>
        <p>Choose whether to close your current platform session.</p>
        <div className="auth-card sign-card">
          <p>
            Signed in as <strong>{sessionUser?.full_name ?? 'Unknown user'}</strong>
            {sessionUser?.email ? ` (${sessionUser.email})` : ''}
          </p>
          <div className="action-row">
            <Button type="button" className="danger" onClick={confirmSignOut}>
              Confirm sign out
            </Button>
            <Button type="button" onClick={cancelSignOut}>
              Back to platform
            </Button>
          </div>
        </div>
      </section>
    </main>
  );
}
