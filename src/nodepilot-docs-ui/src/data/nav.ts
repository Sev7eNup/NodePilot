export interface NavPage {
  path: string // route + content key, e.g. "getting-started/introduction"
  title: string
}

export interface NavGroup {
  label: string
  items: NavPage[]
}

export const navGroups: NavGroup[] = [
  {
    label: 'Erste Schritte',
    items: [
      { path: 'getting-started/introduction', title: 'Einführung' },
      { path: 'getting-started/installation', title: 'Installation' },
      { path: 'getting-started/quickstart', title: 'Quickstart' },
      { path: 'getting-started/architecture', title: 'Architektur' },
    ],
  },
  {
    label: 'Konzepte',
    items: [
      { path: 'concepts/workflows', title: 'Workflows & Activities' },
      { path: 'concepts/activities', title: 'Activity-Typen & Scopes' },
      { path: 'concepts/workflow-json', title: 'Workflow-JSON-Format' },
      { path: 'concepts/data-bus', title: 'Datenbus & Variablen' },
      { path: 'concepts/edge-conditions', title: 'Edge-Bedingungen' },
      { path: 'concepts/sub-workflows', title: 'Sub-Workflows & Contract' },
    ],
  },
  {
    label: 'Workflow-Designer',
    items: [
      { path: 'designer/overview', title: 'Überblick' },
      { path: 'designer/canvas-nodes-edges', title: 'Canvas, Nodes & Edges' },
      { path: 'designer/properties-modes', title: 'Properties, Modi & Shortcuts' },
    ],
  },
  {
    label: 'Referenz',
    items: [
      { path: 'activities-reference', title: 'Activity-Referenz' },
      { path: 'triggers', title: 'Trigger' },
      { path: 'api/endpoints', title: 'API-Endpoints' },
      { path: 'api/authentication', title: 'Authentifizierung & Rollen' },
      { path: 'api/workflow-control', title: 'Workflow-Kontrollfluss' },
      { path: 'cli', title: 'CLI (np)' },
      { path: 'mcp-server', title: 'MCP-Server (nodepilot-mcp)' },
    ],
  },
  {
    label: 'Security',
    items: [
      { path: 'security/overview', title: 'Sicherheitsmodell' },
      { path: 'security/hardening', title: 'Hardening-Flags' },
      { path: 'security/audit-log', title: 'Audit-Log' },
    ],
  },
  {
    label: 'Enterprise',
    items: [
      { path: 'enterprise/high-availability', title: 'High Availability' },
      { path: 'enterprise/secrets-providers', title: 'Secret-Provider' },
      { path: 'enterprise/ldap-windows-sso', title: 'AD SSO Preview' },
      { path: 'enterprise/siem-logging', title: 'SIEM-Logging (ECS)' },
      { path: 'enterprise/folder-rbac', title: 'Folder-RBAC' },
    ],
  },
  {
    label: 'Konfiguration',
    items: [
      { path: 'configuration/appsettings', title: 'appsettings-Übersicht' },
      { path: 'configuration/database', title: 'Datenbank-Provider' },
      { path: 'configuration/remote-execution', title: 'Remote-Execution' },
      { path: 'configuration/logging', title: 'Logging' },
      { path: 'configuration/retention', title: 'Retention-Services' },
    ],
  },
  {
    label: 'Deployment & Mehr',
    items: [
      { path: 'deployment/production', title: 'Produktions-Rollout' },
      { path: 'ai-features', title: 'AI-Features' },
      { path: 'alerting', title: 'Alerting' },
      { path: 'observability', title: 'Observability' },
      { path: 'import-export', title: 'Import / Export & Backup' },
    ],
  },
]

export const allPages: NavPage[] = navGroups.flatMap((g) => g.items)

export const topNav = [
  { label: 'Erste Schritte', path: 'getting-started/introduction' },
  { label: 'Konzepte', path: 'concepts/workflows' },
  { label: 'Referenz', path: 'activities-reference' },
  { label: 'Enterprise', path: 'enterprise/high-availability' },
]

export function pageByPath(path: string): NavPage | undefined {
  return allPages.find((p) => p.path === path)
}

export function neighbors(path: string): { prev?: NavPage; next?: NavPage } {
  const idx = allPages.findIndex((p) => p.path === path)
  return { prev: idx > 0 ? allPages[idx - 1] : undefined, next: idx >= 0 && idx < allPages.length - 1 ? allPages[idx + 1] : undefined }
}
