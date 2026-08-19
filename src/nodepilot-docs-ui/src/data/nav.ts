import type { CarbonIconType } from '@carbon/icons-react'
import {
  Api, Apps, Archive, BareMetalServer, Catalog, ChartLine,
  ChartRelationship, Chat, CloudMonitoring, DataBase, DecisionTree, Deploy, Document,
  Download, Draw, Firewall, Flow, FlowModeler, Folder, Group, Idea, Json, Laptop,
  Layers, Lightning, ListChecked, Meter, Notification, Password, PlayFilledAlt, Plug,
  Replicate, Rocket, Screen, Security, SecurityServices, Settings, SettingsAdjust,
  Terminal, Time, UserRole, ValueVariable,
} from '@carbon/icons-react'

export interface NavPage {
  path: string // route + content key, e.g. "getting-started/introduction"
  /** Sidebar glyph. Required on purpose: `tsc` then fails if a new page ships without one. */
  icon: CarbonIconType
}

export interface NavGroup {
  /** Stable id — the translation key suffix, never rendered directly. */
  id: string
  items: NavPage[]
}

// Structure lives here exactly once; the visible titles live in `i18n/locales/*.json`
// under `nav.groups.<id>` / `nav.pages.<path>`. Adding a page therefore means adding it
// here plus one line per language — `navTitleKey()` derives the key from the path, so
// there is no third mapping table to keep in sync.
//
// Icons mirror `nodepilot-ui/src/lib/navigation.ts` wherever a docs page maps onto an
// app page (Workflows, Activities, CLI-Auth, Machines, Settings, …), so the two navs
// read as one product.
export const navGroups: NavGroup[] = [
  {
    id: 'getting-started',
    items: [
      { path: 'getting-started/introduction', icon: Idea },
      { path: 'getting-started/installation', icon: Download },
      { path: 'getting-started/quickstart', icon: Rocket },
      { path: 'getting-started/architecture', icon: Layers },
    ],
  },
  {
    id: 'concepts',
    items: [
      { path: 'concepts/workflows', icon: FlowModeler },
      { path: 'concepts/activities', icon: Apps },
      { path: 'concepts/workflow-json', icon: Json },
      { path: 'concepts/data-bus', icon: ValueVariable },
      { path: 'concepts/edge-conditions', icon: DecisionTree },
      { path: 'concepts/sub-workflows', icon: Flow },
    ],
  },
  {
    id: 'designer',
    items: [
      { path: 'designer/overview', icon: Draw },
      { path: 'designer/canvas-nodes-edges', icon: ChartRelationship },
      { path: 'designer/properties-modes', icon: SettingsAdjust },
    ],
  },
  {
    id: 'reference',
    items: [
      { path: 'activities-reference', icon: Catalog },
      { path: 'triggers', icon: Lightning },
      { path: 'api/endpoints', icon: Api },
      { path: 'api/authentication', icon: UserRole },
      { path: 'api/workflow-control', icon: PlayFilledAlt },
      { path: 'cli', icon: Terminal },
      { path: 'mcp-server', icon: Plug },
    ],
  },
  {
    id: 'security',
    items: [
      { path: 'security/overview', icon: Security },
      { path: 'security/hardening', icon: Firewall },
      { path: 'security/audit-log', icon: ListChecked },
    ],
  },
  {
    id: 'enterprise',
    items: [
      { path: 'enterprise/high-availability', icon: Replicate },
      { path: 'enterprise/secrets-providers', icon: Password },
      { path: 'enterprise/ldap-windows-sso', icon: Group },
      { path: 'enterprise/siem-logging', icon: CloudMonitoring },
      { path: 'enterprise/folder-rbac', icon: Folder },
    ],
  },
  {
    id: 'configuration',
    items: [
      { path: 'configuration/appsettings', icon: Settings },
      { path: 'configuration/database', icon: DataBase },
      { path: 'configuration/remote-execution', icon: Screen },
      { path: 'configuration/logging', icon: Document },
      { path: 'configuration/retention', icon: Time },
      { path: 'configuration/performance', icon: Meter },
    ],
  },
  {
    id: 'deployment',
    items: [
      { path: 'deployment/overview', icon: Deploy },
      { path: 'deployment/production', icon: BareMetalServer },
      { path: 'deployment/desktop', icon: Laptop },
      { path: 'deployment/av-exclusions', icon: SecurityServices },
      { path: 'ai-features', icon: Chat },
      { path: 'alerting', icon: Notification },
      { path: 'observability', icon: ChartLine },
      { path: 'import-export', icon: Archive },
    ],
  },
]

export const allPages: NavPage[] = navGroups.flatMap((g) => g.items)

/** Translation key for a page title. No page path contains a `.`, so the path can sit
 *  under `nav.pages` as a plain leaf without fighting i18next's key separator. */
export function navTitleKey(path: string): string {
  return `nav.pages.${path}`
}

export function navGroupKey(id: string): string {
  return `nav.groups.${id}`
}

export function pageByPath(path: string): NavPage | undefined {
  return allPages.find((p) => p.path === path)
}

/** Group id a page belongs to — the single source for the TopBar breadcrumb. */
export function groupOf(path: string): string | undefined {
  return navGroups.find((g) => g.items.some((i) => i.path === path))?.id
}

export function neighbors(path: string): { prev?: NavPage; next?: NavPage } {
  const idx = allPages.findIndex((p) => p.path === path)
  return { prev: idx > 0 ? allPages[idx - 1] : undefined, next: idx >= 0 && idx < allPages.length - 1 ? allPages[idx + 1] : undefined }
}
