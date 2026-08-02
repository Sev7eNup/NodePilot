import type { CarbonIconType } from '@carbon/icons-react'
import {
  Api, Apps, Archive, BareMetalServer, Catalog, ChartLine,
  ChartRelationship, Chat, CloudMonitoring, DataBase, DecisionTree, Deploy, Document,
  Download, Draw, Firewall, Flow, FlowModeler, Folder, Group, Idea, Json, Laptop,
  Layers, Lightning, ListChecked, Notification, Password, PlayFilledAlt, Plug,
  Replicate, Rocket, Screen, Security, SecurityServices, Settings, SettingsAdjust,
  Terminal, Time, UserRole, ValueVariable,
} from '@carbon/icons-react'

export interface NavPage {
  path: string // route + content key, e.g. "getting-started/introduction"
  title: string
  /** Sidebar glyph. Required on purpose: `tsc` then fails if a new page ships without one. */
  icon: CarbonIconType
}

export interface NavGroup {
  label: string
  items: NavPage[]
}

// Icons mirror `nodepilot-ui/src/lib/navigation.ts` wherever a docs page maps onto an
// app page (Workflows, Activities, CLI-Auth, Machines, Settings, …), so the two navs
// read as one product.
export const navGroups: NavGroup[] = [
  {
    label: 'Erste Schritte',
    items: [
      { path: 'getting-started/introduction', title: 'Einführung', icon: Idea },
      { path: 'getting-started/installation', title: 'Installation', icon: Download },
      { path: 'getting-started/quickstart', title: 'Schnelleinstieg', icon: Rocket },
      { path: 'getting-started/architecture', title: 'Architektur', icon: Layers },
    ],
  },
  {
    label: 'Konzepte',
    items: [
      { path: 'concepts/workflows', title: 'Workflows & Activities', icon: FlowModeler },
      { path: 'concepts/activities', title: 'Activity-Typen & Scopes', icon: Apps },
      { path: 'concepts/workflow-json', title: 'Workflow-JSON-Format', icon: Json },
      { path: 'concepts/data-bus', title: 'Datenbus & Variablen', icon: ValueVariable },
      { path: 'concepts/edge-conditions', title: 'Edge-Bedingungen', icon: DecisionTree },
      { path: 'concepts/sub-workflows', title: 'Sub-Workflows & Contract', icon: Flow },
    ],
  },
  {
    label: 'Workflow-Designer',
    items: [
      { path: 'designer/overview', title: 'Überblick', icon: Draw },
      { path: 'designer/canvas-nodes-edges', title: 'Canvas, Nodes & Edges', icon: ChartRelationship },
      { path: 'designer/properties-modes', title: 'Properties, Modi & Shortcuts', icon: SettingsAdjust },
    ],
  },
  {
    label: 'Referenz',
    items: [
      { path: 'activities-reference', title: 'Activity-Referenz', icon: Catalog },
      { path: 'triggers', title: 'Trigger', icon: Lightning },
      { path: 'api/endpoints', title: 'API-Endpoints', icon: Api },
      { path: 'api/authentication', title: 'Authentifizierung & Rollen', icon: UserRole },
      { path: 'api/workflow-control', title: 'Workflow-Kontrollfluss', icon: PlayFilledAlt },
      { path: 'cli', title: 'CLI (np)', icon: Terminal },
      { path: 'mcp-server', title: 'MCP-Server (nodepilot-mcp)', icon: Plug },
    ],
  },
  {
    label: 'Security',
    items: [
      { path: 'security/overview', title: 'Sicherheitsmodell', icon: Security },
      { path: 'security/hardening', title: 'Hardening-Flags', icon: Firewall },
      { path: 'security/audit-log', title: 'Audit-Log', icon: ListChecked },
    ],
  },
  {
    label: 'Enterprise',
    items: [
      { path: 'enterprise/high-availability', title: 'High Availability', icon: Replicate },
      { path: 'enterprise/secrets-providers', title: 'Secret-Provider', icon: Password },
      { path: 'enterprise/ldap-windows-sso', title: 'AD SSO Preview', icon: Group },
      { path: 'enterprise/siem-logging', title: 'SIEM-Logging (ECS)', icon: CloudMonitoring },
      { path: 'enterprise/folder-rbac', title: 'Folder-RBAC', icon: Folder },
    ],
  },
  {
    label: 'Konfiguration',
    items: [
      { path: 'configuration/appsettings', title: 'appsettings-Übersicht', icon: Settings },
      { path: 'configuration/database', title: 'Datenbank-Provider', icon: DataBase },
      { path: 'configuration/remote-execution', title: 'Remote-Execution', icon: Screen },
      { path: 'configuration/logging', title: 'Logging', icon: Document },
      { path: 'configuration/retention', title: 'Retention-Services', icon: Time },
    ],
  },
  {
    label: 'Deployment & Mehr',
    items: [
      { path: 'deployment/overview', title: 'Betriebsarten', icon: Deploy },
      { path: 'deployment/production', title: 'Windows-Server', icon: BareMetalServer },
      { path: 'deployment/desktop', title: 'Desktop-App', icon: Laptop },
      { path: 'deployment/av-exclusions', title: 'Antiviren-Ausschlüsse', icon: SecurityServices },
      { path: 'ai-features', title: 'AI-Features', icon: Chat },
      { path: 'alerting', title: 'Alerting', icon: Notification },
      { path: 'observability', title: 'Observability', icon: ChartLine },
      { path: 'import-export', title: 'Import / Export & Backup', icon: Archive },
    ],
  },
]

export const allPages: NavPage[] = navGroups.flatMap((g) => g.items)

export function pageByPath(path: string): NavPage | undefined {
  return allPages.find((p) => p.path === path)
}

/** Group label a page belongs to — the single source for the TopBar breadcrumb. */
export function groupOf(path: string): string | undefined {
  return navGroups.find((g) => g.items.some((i) => i.path === path))?.label
}

export function neighbors(path: string): { prev?: NavPage; next?: NavPage } {
  const idx = allPages.findIndex((p) => p.path === path)
  return { prev: idx > 0 ? allPages[idx - 1] : undefined, next: idx >= 0 && idx < allPages.length - 1 ? allPages[idx + 1] : undefined }
}
