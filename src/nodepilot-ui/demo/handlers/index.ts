/**
 * The demo's route table.
 *
 * Order is significant: the router matches in registration order, so specific paths are
 * registered before parameterised ones. Anything not listed falls through to the fallback in
 * `net/install.ts`, which logs `[demo] unhandled …` — the smoke test fails on those lines, so
 * a missing route is a test failure rather than a page that quietly renders wrong.
 */
import { route, type Route } from '../net/router';
import { json, notInDemo } from '../net/respond';
import { getWorld } from '../state/world';
import { bootRoutes } from './boot';
import { statsRoutes } from './stats';
import { workflowRoutes } from './workflows';
import { executionRoutes } from './executions';
import { catalogRoutes } from './catalog';
import { settingsRoutes } from './settings';
import { alertingRoutes } from './alerting';
import { auditRoutes } from './audit';
import { customActivityRoutes } from './customActivities';
import { diagnosticsRoutes } from './diagnostics';

/**
 * Endpoints that exist only because a real server does something the browser cannot: run
 * SQL, send mail, seal a backup, probe a directory. They answer with an explanation rather
 * than a plausible lie, so nobody mistakes the demo for a working install.
 */
const serverOnlyRoutes: Route[] = [
  // Counted from the world, so the section list is real even though sealing the archive is not.
  // An empty manifest selected nothing, and Export then failed the page's own "pick a section"
  // validation instead of reaching the explanation below.
  route('GET', '/backup/manifest', () => {
    const world = getWorld();
    return json({
      sections: [
        { section: 'workflows', count: world.workflows.length },
        { section: 'folders', count: world.folders.length },
        { section: 'folderGrants', count: world.folderPermissions.length },
        { section: 'machines', count: world.machines.length },
        { section: 'credentials', count: world.credentials.length },
        { section: 'globalVariables', count: world.globals.length },
        { section: 'customActivities', count: world.customActivities.length },
        { section: 'users', count: world.users.length },
        { section: 'settings', count: 1 },
      ],
    });
  }),
  route('POST', '/backup/export', () => notInDemo('Exporting a configuration backup')),
  route('POST', '/backup/preview', () => notInDemo('Reading a backup archive')),
  route('POST', '/backup/restore', () => notInDemo('Restoring a backup')),

  route('GET', '/dbadmin/info', () => json({ provider: 'PostgreSQL', tableCount: 0 })),
  route('GET', '/dbadmin/tables', () => json([])),
  route('POST', '/dbadmin/query', () => notInDemo('Running SQL')),

  // The support log and its events are derived from the history in handlers/diagnostics.ts.
  // A downloaded file is a claim about a real installation. Refusing is honest; handing over a
  // file whose whole content is `{}` is not.
  route('GET', '/diagnostics/support-log/download', () => notInDemo('Downloading the support log')),
  route('GET', '/diagnostics/support-events/export', () => notInDemo('Exporting support events')),

  route('POST', '/ai/chat', () => notInDemo('The AI assistant')),
  route('POST', '/ai/knowledge/ask', () => notInDemo('The AI assistant')),
  route('POST', '/ai/generate-script', () => notInDemo('AI script generation')),
  route('POST', '/ai/generate-workflow', () => notInDemo('AI workflow generation')),
  route('GET', '/ai/chat/activity/:workflowId', () => json([])),

];

export const demoRoutes: Route[] = [
  ...bootRoutes,
  ...settingsRoutes,
  ...alertingRoutes,
  ...auditRoutes,
  ...customActivityRoutes,
  ...diagnosticsRoutes,
  ...statsRoutes,
  ...workflowRoutes,
  ...executionRoutes,
  ...catalogRoutes,
  ...serverOnlyRoutes,
];
