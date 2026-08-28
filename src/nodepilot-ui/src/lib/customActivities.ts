import { create } from 'zustand';

/**
 * Runtime catalog of user-authored custom activities ("Custom Nodes"). The static
 * `activityCatalog.generated.ts` is parity-locked against the backend and cannot carry these,
 * so they are fetched from `GET /api/custom-activities` and held here.
 *
 * Two access shapes:
 *  - a Zustand store for React components that re-render when the catalog loads or changes;
 *  - a synchronous module-level cache (kept current by the store's setter) for the plain helper
 *    functions `getActivityLabel` and `describeNodeOutputs` that run outside React.
 */

const CUSTOM_PREFIX = 'custom:';

export interface CustomActivityInputParameter {
  name: string;
  label: string;
  type: 'string' | 'number' | 'boolean' | 'select' | 'multiline';
  required?: boolean;
  default?: string | null;
  options?: string[] | null;
  description?: string | null;
}

export interface CustomActivityOutputParameter {
  name: string;
  type: 'string' | 'number' | 'boolean' | 'object' | 'array';
}

/** Mirrors the API's CustomActivityCatalogEntry: palette facts, no script. */
export interface CustomActivityCatalogEntry {
  id: string;
  key: string;
  type: string; // custom:<key>
  name: string;
  description?: string | null;
  icon: string;
  color?: string | null;
  runsRemote: boolean;
  timeout: string; // "always"
  inputs: CustomActivityInputParameter[];
  outputs: CustomActivityOutputParameter[];
  isEnabled: boolean;
  version: number;
}

export function isCustomActivityType(type?: string | null): boolean {
  return typeof type === 'string' && type.startsWith(CUSTOM_PREFIX);
}

export function customActivityKeyOf(type?: string | null): string | null {
  return isCustomActivityType(type) ? type!.slice(CUSTOM_PREFIX.length) : null;
}

// --- synchronous module cache (read by non-React helpers) ------------------
let moduleCatalog: CustomActivityCatalogEntry[] = [];
const byType = new Map<string, CustomActivityCatalogEntry>();

function syncModuleCache(entries: CustomActivityCatalogEntry[]): void {
  moduleCatalog = entries;
  byType.clear();
  for (const e of entries) byType.set(e.type, e);
}

/** Facts for a single custom type, or undefined. Synchronous, so it is safe outside React. */
export function getCustomActivityFacts(type: string): CustomActivityCatalogEntry | undefined {
  return byType.get(type);
}

/** Enabled custom entries for the palette (synchronous snapshot of the module cache). */
export function getEnabledCustomActivities(): CustomActivityCatalogEntry[] {
  return moduleCatalog.filter((e) => e.isEnabled);
}

// --- Zustand store (reactive) ----------------------------------------------
interface CustomActivityCatalogState {
  catalog: CustomActivityCatalogEntry[];
  setCatalog: (entries: CustomActivityCatalogEntry[]) => void;
}

export const useCustomActivityCatalogStore = create<CustomActivityCatalogState>((set) => ({
  catalog: [],
  setCatalog: (entries) => {
    syncModuleCache(entries);
    set({ catalog: entries });
  },
}));
