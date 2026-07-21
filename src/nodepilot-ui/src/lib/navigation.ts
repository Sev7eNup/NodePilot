import type { CarbonIconType } from '@carbon/icons-react';
import {
  Activity, Apps, Archive, Calendar, ChartLine, Chat, Dashboard, DataBase, Document,
  FlowModeler, Group, Notification, PlayFilledAlt, Screen, Security, Settings, ValueVariable,
} from '@carbon/icons-react';

export type BadgeKind = 'workflows' | 'running' | 'machines' | 'alerts' | 'live';

/** Live capability flag (from GET /api/ai/knowledge/capabilities) that gates a nav item's
 *  visibility beyond the coarse `adminOnly`. Currently only the AI-Chat entry uses it. */
export type CapabilityKey = 'enabled';

export type NavItem = {
  to: string;
  icon: CarbonIconType;
  key: string;
  adminOnly?: boolean;
  badge?: BadgeKind;
  /** When set, the item is shown only if the matching AI-knowledge capability is true. */
  capabilityKey?: CapabilityKey;
};

export type NavGroup = { labelKey: string; items: NavItem[] };

/** Shared source of truth for the sidebar and the app-header breadcrumb. */
export const navGroups: NavGroup[] = [
  {
    labelKey: 'groupWorkspace',
    items: [
      { to: '/', icon: Dashboard, key: 'dashboard' },
      { to: '/workflows', icon: FlowModeler, key: 'workflows', badge: 'workflows' },
      { to: '/executions', icon: PlayFilledAlt, key: 'executions', badge: 'running' },
      { to: '/operations', icon: Activity, key: 'operations', badge: 'live' },
      { to: '/ai-chat', icon: Chat, key: 'aiChat', capabilityKey: 'enabled' },
    ],
  },
  {
    labelKey: 'groupInfrastructure',
    items: [
      { to: '/machines', icon: Screen, key: 'machines', badge: 'machines' },
      { to: '/global-variables', icon: ValueVariable, key: 'globals' },
      { to: '/custom-activities', icon: Apps, key: 'customNodes' },
      { to: '/maintenance-windows', icon: Calendar, key: 'maintenance' },
    ],
  },
  {
    labelKey: 'groupMonitoring',
    items: [
      { to: '/metrics', icon: ChartLine, key: 'metrics' },
      { to: '/alerts', icon: Notification, key: 'alerts', badge: 'alerts' },
      { to: '/support-log', icon: Document, key: 'supportLog', adminOnly: true },
    ],
  },
  {
    labelKey: 'groupAdmin',
    items: [
      { to: '/users', icon: Group, key: 'users', adminOnly: true },
      { to: '/audit', icon: Security, key: 'auditLog', adminOnly: true },
      { to: '/database', icon: DataBase, key: 'database', adminOnly: true },
      { to: '/backup', icon: Archive, key: 'backup', adminOnly: true },
      { to: '/settings', icon: Settings, key: 'settings' },
    ],
  },
];

export const metricsSections = [
  { id: 'mission-control', labelKey: 'missionControl', dashboardId: 'nodepilot-mission-control' },
  { id: 'workflows', labelKey: 'workflows', dashboardId: 'nodepilot-workflows-v2' },
  { id: 'activities', labelKey: 'activities', dashboardId: 'nodepilot-activities' },
  { id: 'winrm', labelKey: 'winrm', dashboardId: 'nodepilot-winrm' },
  { id: 'triggers', labelKey: 'triggers', dashboardId: 'nodepilot-triggers' },
  { id: 'api', labelKey: 'api', dashboardId: 'nodepilot-api' },
  { id: 'runtime', labelKey: 'runtime', dashboardId: 'nodepilot-runtime' },
  { id: 'security', labelKey: 'security', dashboardId: 'nodepilot-security' },
  { id: 'ai', labelKey: 'ai', dashboardId: 'nodepilot-ai' },
  { id: 'database', labelKey: 'database', dashboardId: 'nodepilot-database' },
] as const;

export type MetricsSectionId = (typeof metricsSections)[number]['id'];

