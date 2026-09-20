/**
 * Authored facts of the demo world: the things that cannot be derived from anything else.
 *
 * Counts and aggregates deliberately live elsewhere — machines get their usage numbers from
 * the seed graphs, folders their workflow counts from the workflow list, and the dashboard
 * everything from the execution history. Authoring those here is how a demo ends up showing
 * fourteen runs on one screen and nine on the next.
 */
import {
  ACTIVE_DIRECTORY_AUTHORITY,
  ROOT_FOLDER_ID,
  type SharedFolder,
  type SharedFolderPermission,
} from '../../src/api/sharedFolders';
// Global variables have their OWN root sentinel, a different GUID from the workflow-folder one.
// Using the wrong constant leaves the page filtering against a folder nothing belongs to, and
// the list renders empty while the API happily returns five rows.
import { ROOT_FOLDER_ID as GLOBALS_ROOT_FOLDER_ID, type GlobalFolder } from '../../src/api/globalFolders';
import type { Credential, ManagedMachine, UserRow } from '../../src/types/api';
import type { DemoGlobalVariable, DemoMaintenanceWindow } from '../state/world';
import { demoId } from '../state/ids';

/** The identity the demo runs as. Admin, so every surface is reachable. */
export const DEMO_USER = {
  id: demoId('user:demo-admin'),
  username: 'demo',
  role: 'Admin' as const,
};

export const DEMO_HOST = {
  machineName: 'NP-DEMO',
  fqdn: 'np-demo.contoso.example',
  domain: 'contoso.example',
  appVersion: __NP_DEMO_VERSION__,
};

/** Milliseconds, for readable relative timestamps below. */
const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** Everything in the world is dated against one instant, captured when the world is built. */
export function iso(offsetMs: number, now: number): string {
  return new Date(now + offsetMs).toISOString();
}

// --- folders ---------------------------------------------------------------

const FOLDER_SPECS = [
  { key: 'operations', name: 'Operations' },
  { key: 'provisioning', name: 'Provisioning' },
  { key: 'maintenance', name: 'Maintenance' },
];

export function folderIdOf(key: string): string {
  return demoId(`folder:${key}`);
}

export function buildFolders(now: number): SharedFolder[] {
  const capabilities = { canRead: true, canRun: true, canEdit: true, canDelete: true, canAdmin: true };
  const root: SharedFolder = {
    id: ROOT_FOLDER_ID,
    parentFolderId: null,
    name: 'Root',
    path: '/',
    depth: 0,
    createdAt: iso(-90 * DAY, now),
    createdByUserId: DEMO_USER.id,
    workflowCount: 0,
    capabilities,
  };
  const children = FOLDER_SPECS.map((spec) => ({
    id: folderIdOf(spec.key),
    parentFolderId: ROOT_FOLDER_ID,
    name: spec.name,
    path: `/${spec.name}`,
    depth: 1,
    createdAt: iso(-90 * DAY, now),
    createdByUserId: DEMO_USER.id,
    workflowCount: 0,
    capabilities,
  }));
  return [root, ...children];
}

// --- credentials -----------------------------------------------------------

const CREDENTIAL_SPECS = [
  { key: 'svc-automation', name: 'Service Automation', username: 'svc_nodepilot', domain: 'CONTOSO', expiresInDays: 148 },
  { key: 'sccm-admin', name: 'SCCM Site Admin', username: 'sccm_admin', domain: 'CONTOSO', expiresInDays: 21 },
  { key: 'local-fallback', name: 'Local Fallback', username: 'Administrator', domain: null, expiresInDays: null },
];

export function buildCredentials(now: number): Credential[] {
  return CREDENTIAL_SPECS.map((spec) => ({
    id: demoId(`credential:${spec.key}`),
    name: spec.name,
    username: spec.username,
    domain: spec.domain,
    expiresAt: spec.expiresInDays === null ? null : iso(spec.expiresInDays * DAY, now),
  }));
}

// --- machines --------------------------------------------------------------

const MACHINE_SPECS = [
  { key: 'app01', name: 'APP01', hostname: 'app01.contoso.example', tags: 'production,iis', reachable: true },
  { key: 'app02', name: 'APP02', hostname: 'app02.contoso.example', tags: 'production,iis', reachable: true },
  { key: 'sccm01', name: 'SCCM01', hostname: 'sccm01.contoso.example', tags: 'production,sccm', reachable: true },
  { key: 'dc01', name: 'DC01', hostname: 'dc01.contoso.example', tags: 'production,domain-controller', reachable: true },
  { key: 'file01', name: 'FILE01', hostname: 'file01.contoso.example', tags: 'production,storage', reachable: true },
  { key: 'lab01', name: 'LAB01', hostname: 'lab01.contoso.example', tags: 'lab', reachable: false },
];

export function machineIds(): string[] {
  return MACHINE_SPECS.map((spec) => demoId(`machine:${spec.key}`));
}

export function credentialIds(): string[] {
  return CREDENTIAL_SPECS.map((spec) => demoId(`credential:${spec.key}`));
}

/**
 * Machines with their activity counters left at zero. The world builder fills
 * `usedByWorkflowCount` and the step counters from the seed graphs and executions.
 */
export function buildMachines(now: number, credentials: Credential[]): ManagedMachine[] {
  return MACHINE_SPECS.map((spec, index) => ({
    id: demoId(`machine:${spec.key}`),
    name: spec.name,
    hostname: spec.hostname,
    winRmPort: 5986,
    useSsl: true,
    defaultCredentialId: credentials[index % credentials.length].id,
    tags: spec.tags,
    lastConnectivityCheck: iso(spec.reachable ? -7 * MINUTE : -3 * DAY, now),
    isReachable: spec.reachable,
    usedByWorkflowCount: 0,
    recentStepCount: 0,
    recentFailedStepCount: 0,
    activeRunCount: 0,
  }));
}

// --- global variables ------------------------------------------------------

const GLOBAL_SPECS = [
  { key: 'smtp-host', name: 'SmtpHost', value: 'smtp.contoso.example', secret: false, description: 'Relay used by notification steps.', folderKey: 'integration' },
  { key: 'ops-mailbox', name: 'OpsMailbox', value: 'ops@contoso.example', secret: false, description: 'Distribution list for operational alerts.', folderKey: null },
  { key: 'sccm-site', name: 'SccmSiteCode', value: 'P01', secret: false, description: 'Primary site code.', folderKey: null },
  { key: 'retention-days', name: 'ReportRetentionDays', value: '30', secret: false, description: 'How long generated reports are kept.', folderKey: 'reporting' },
  { key: 'api-token', name: 'MonitoringApiToken', value: null, secret: true, description: 'Bearer token for the monitoring REST API.', folderKey: 'integration' },
];

export function buildGlobals(now: number): DemoGlobalVariable[] {
  return GLOBAL_SPECS.map((spec) => ({
    id: demoId(`global:${spec.key}`),
    name: spec.name,
    value: spec.secret ? null : spec.value,
    isSecret: spec.secret,
    description: spec.description,
    folderId: spec.folderKey ? globalFolderIdOf(spec.folderKey) : GLOBALS_ROOT_FOLDER_ID,
    createdAt: iso(-60 * DAY, now),
    updatedAt: iso(-12 * DAY, now),
    updatedBy: DEMO_USER.username,
  }));
}

// --- global variable folders -----------------------------------------------

/** Two folders beside Root, so the tree has something to rename, move and delete. */
const GLOBAL_FOLDER_SPECS = [
  { key: 'integration', name: 'Integration' },
  { key: 'reporting', name: 'Reporting' },
];

export function globalFolderIdOf(key: string): string {
  return demoId(`global-folder:${key}`);
}

export function buildGlobalFolders(now: number): GlobalFolder[] {
  const root: GlobalFolder = {
    id: GLOBALS_ROOT_FOLDER_ID,
    parentFolderId: null,
    name: 'Root',
    path: '/',
    depth: 0,
    createdAt: iso(-90 * DAY, now),
    createdByUserId: DEMO_USER.id,
    variableCount: 0,
  };
  return [
    root,
    ...GLOBAL_FOLDER_SPECS.map((spec) => ({
      id: globalFolderIdOf(spec.key),
      parentFolderId: GLOBALS_ROOT_FOLDER_ID,
      name: spec.name,
      path: `/${spec.name}`,
      depth: 1,
      createdAt: iso(-90 * DAY, now),
      createdByUserId: DEMO_USER.id,
      variableCount: 0,
    })),
  ];
}

// --- folder permissions ----------------------------------------------------

/**
 * Two grants on the Operations folder. Without them the permissions dialog opens on an empty
 * list, and the one thing a visitor can do in it is the thing they cannot judge the result of.
 */
export function buildFolderPermissions(now: number): SharedFolderPermission[] {
  return [
    {
      id: demoId('folder-permission:alice-operations'),
      folderId: folderIdOf('operations'),
      principalType: 'User',
      principalKey: demoId('user:alice'),
      principalAuthority: null,
      principalDisplayName: 'alice',
      role: 'FolderEditor',
      grantedAt: iso(-45 * DAY, now),
      grantedByUserId: DEMO_USER.id,
    },
    {
      id: demoId('folder-permission:servicedesk-operations'),
      folderId: folderIdOf('operations'),
      principalType: 'Group',
      principalKey: 'CONTOSO\\Service Desk',
      principalAuthority: ACTIVE_DIRECTORY_AUTHORITY,
      principalDisplayName: 'Service Desk',
      role: 'FolderOperator',
      grantedAt: iso(-30 * DAY, now),
      grantedByUserId: DEMO_USER.id,
    },
  ];
}

// --- maintenance windows ---------------------------------------------------

export function buildMaintenanceWindows(now: number): DemoMaintenanceWindow[] {
  const base = {
    createdAt: iso(-50 * DAY, now),
    updatedAt: iso(-9 * DAY, now),
    updatedBy: DEMO_USER.username,
    oneTimeStartUtc: null,
    oneTimeEndUtc: null,
    weeklyDaysMask: 0,
    weeklyStartMinuteOfDay: null,
    weeklyEndMinuteOfDay: null,
    cronExpression: null,
    durationMinutes: null,
    timeZoneId: 'W. Europe Standard Time',
  };
  return [
    {
      ...base,
      id: demoId('maintenance-window:patch-night'),
      name: 'Patch night',
      description: 'No scheduled runs while the monthly patch ring is applied.',
      isEnabled: true,
      mode: 'Blackout',
      scopeKind: 'Folders',
      recurrence: 'Weekly',
      // Saturday, 22:00–02:00.
      weeklyDaysMask: 64,
      weeklyStartMinuteOfDay: 22 * 60,
      weeklyEndMinuteOfDay: 2 * 60,
      targets: [{ targetKind: 'Folder' as const, targetId: folderIdOf('operations') }],
    },
    {
      ...base,
      id: demoId('maintenance-window:year-end'),
      name: 'Year-end freeze',
      description: 'Change freeze across the estate.',
      isEnabled: false,
      mode: 'Blackout',
      scopeKind: 'Global',
      recurrence: 'OneTime',
      oneTimeStartUtc: iso(40 * DAY, now),
      oneTimeEndUtc: iso(54 * DAY, now),
      targets: [],
    },
  ];
}

// Custom activities live in seed/customActivities.ts: the world stores their full
// definitions, because the edit dialog reads more than the palette catalog carries.

// --- users -----------------------------------------------------------------

export function buildUsers(now: number): UserRow[] {
  return [
    {
      id: DEMO_USER.id,
      username: DEMO_USER.username,
      role: 'Admin',
      isActive: true,
      createdAt: iso(-90 * DAY, now),
      provider: 'Local',
    },
    {
      id: demoId('user:alice'),
      username: 'alice',
      role: 'Operator',
      isActive: true,
      createdAt: iso(-75 * DAY, now),
      provider: 'Ldap',
      authority: 'contoso.example',
    },
    {
      id: demoId('user:bob'),
      username: 'bob',
      role: 'Viewer',
      isActive: true,
      createdAt: iso(-40 * DAY, now),
      provider: 'Ldap',
      authority: 'contoso.example',
    },
  ];
}
