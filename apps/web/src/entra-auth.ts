export const ENTRA_AUTO_ATTEMPT_KEY = 'codeinsights-entra-auto-attempt-v1';
export const ENTRA_AUTO_SUPPRESSED_KEY = 'codeinsights-entra-auto-suppressed-v1';

export type SilentSignInState = {
  signedIn: boolean;
  hasCallback: boolean;
  entraEnabled: boolean;
  autoSignInEnabled: boolean;
  claimed: boolean;
  attemptedInTab: boolean;
  suppressedInTab: boolean;
};

export function shouldAttemptSilentEntra(state: SilentSignInState): boolean {
  return !state.signedIn && !state.hasCallback && state.entraEnabled && state.autoSignInEnabled && !state.claimed && !state.attemptedInTab && !state.suppressedInTab;
}
