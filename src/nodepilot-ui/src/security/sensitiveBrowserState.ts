/**
 * Browser-persisted state which can contain customer data, SQL text, workflow definitions or
 * prompts. It may survive a component unmount/page reload, but must never survive an
 * authentication boundary in the same browser profile.
 */
export const AI_CHAT_STORAGE_KEY = 'nodepilot-aichat';
export const DB_ADMIN_QUERY_HISTORY_KEY = 'nodepilot.dbAdmin.queryHistory';
export const DB_ADMIN_QUERY_DRAFT_KEY = 'nodepilot.dbAdmin.queryDraft';
export const DB_ADMIN_QUERY_MODE_KEY = 'nodepilot.dbAdmin.queryMode';

const OWNER_KEY = 'nodepilot.sensitiveState.owner';
const SENSITIVE_KEYS = [
  AI_CHAT_STORAGE_KEY,
  DB_ADMIN_QUERY_HISTORY_KEY,
  DB_ADMIN_QUERY_DRAFT_KEY,
  DB_ADMIN_QUERY_MODE_KEY,
] as const;

function removeKeys(storage: Storage, includeOwner: boolean): void {
  for (const key of SENSITIVE_KEYS) storage.removeItem(key);
  if (includeOwner) storage.removeItem(OWNER_KEY);
}

/**
 * Removes both current session state and residue left in localStorage by older releases.
 * Storage access is best-effort because hardened/private browser profiles can disable it.
 */
export function clearSensitiveBrowserState(): void {
  try {
    removeKeys(globalThis.sessionStorage, true);
  } catch {
    // Persistence is optional; live stores are cleared separately by the auth store.
  }
  try {
    removeKeys(globalThis.localStorage, false);
  } catch {
    // Same policy for legacy localStorage cleanup.
  }
}

/** Remove only pre-fix localStorage residue without destroying a valid same-user tab session. */
export function clearLegacySensitiveLocalStorage(): void {
  try {
    removeKeys(globalThis.localStorage, false);
  } catch {
    // Best effort; new code never writes these values to localStorage.
  }
}

/**
 * Binds the session-persisted state to the authenticated user. Returns true when state was
 * discarded because there was no trustworthy owner marker or the identity changed.
 */
export function bindSensitiveBrowserStateToUser(userId: string): boolean {
  clearLegacySensitiveLocalStorage();

  try {
    const previousOwner = globalThis.sessionStorage.getItem(OWNER_KEY);
    const mustClear = previousOwner !== userId;
    if (mustClear) removeKeys(globalThis.sessionStorage, false);
    globalThis.sessionStorage.setItem(OWNER_KEY, userId);
    return mustClear;
  } catch {
    // If ownership cannot be proven, callers clear their in-memory sensitive stores too.
    return true;
  }
}
