/**
 * First-run decisions of the shell, kept free of Electron so they can be unit-tested.
 *
 * The installer leaves a setup-token handoff only while NodePilot has no administrator. As long
 * as it exists, every way into the app (launch, second launch, tray) must lead to the setup page:
 * the regular login page cannot create the first account.
 */
export type AppWindowKind = 'setup' | 'main';

export function appWindowFor(handoffExists: boolean): AppWindowKind {
  return handoffExists ? 'setup' : 'main';
}

export interface SetupFailure {
  error: string;
  /** The handoff can never succeed again and must be removed, so the login page becomes reachable. */
  discardHandoff: boolean;
}

/** Maps a non-200 answer of the bootstrap login to what the setup page shows. */
export function describeSetupFailure(status: number): SetupFailure {
  if (status === 401) {
    return {
      error:
        'NodePilot did not accept the setup token: an administrator already exists, or the token ' +
        'belongs to an earlier installation. Close this window and sign in with your existing account.',
      discardHandoff: true,
    };
  }
  if (status === 400) {
    return {
      error: 'The password does not meet the requirements: at least 8 characters.',
      discardHandoff: false,
    };
  }
  return { error: `Setup failed (HTTP ${status}). Please try again.`, discardHandoff: false };
}
