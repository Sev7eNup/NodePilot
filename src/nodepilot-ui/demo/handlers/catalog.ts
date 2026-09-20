/**
 * Reference data the designer and the surrounding pages read: machines, credentials, global
 * variables, folders, custom activities and users.
 *
 * Mutations are supported where they are cheap and the demo benefits (renaming a machine,
 * editing a global). Anything that would need a real server — a WinRM connectivity probe,
 * re-encrypting secrets — answers with an explanation instead.
 */
import { ROOT_FOLDER_ID, type SharedFolderPermission } from '../../src/api/sharedFolders';
// Globals use a different root sentinel than workflow folders; see seed/entities.ts.
import { ROOT_FOLDER_ID as GLOBALS_ROOT_FOLDER_ID, type GlobalFolder } from '../../src/api/globalFolders';
import type { UserRow } from '../../src/types/api';
import { route, type Route } from '../net/router';
import { conflict, json, noContent, notFound, notInDemo } from '../net/respond';
import { getWorld, type DemoGlobalVariable, type DemoMaintenanceWindow } from '../state/world';
import { runtimeId } from '../state/ids';
import { DEMO_USER } from '../seed/entities';

/** The shape both folder trees share; enough for the path arithmetic below. */
interface FolderLike {
  id: string;
  parentFolderId: string | null;
  name: string;
  path: string;
  depth: number;
}

/**
 * Recomputes `path` and `depth` for a folder and every descendant.
 *
 * A rename or a move changes the prefix of the whole subtree, not just the folder itself —
 * the product does the same (`SharedWorkflowFoldersController.RecomputePathsRecursive`).
 * Rewriting only the renamed folder leaves its children on the old prefix, and breadcrumbs
 * and folder pickers then disagree with the tree.
 */
function repathSubtree(folders: FolderLike[], folder: FolderLike): void {
  const parent = folders.find((f) => f.id === folder.parentFolderId);
  const parentPath = parent && parent.path !== '/' ? parent.path : '';
  folder.path = `${parentPath}/${folder.name}`;
  folder.depth = (parent?.depth ?? 0) + 1;
  for (const child of folders.filter((f) => f.parentFolderId === folder.id)) {
    repathSubtree(folders, child);
  }
}

/** True when `candidate` is `folder` or lies beneath it — a move into it would build a cycle. */
function isSelfOrDescendant(folders: FolderLike[], folder: FolderLike, candidateId: string): boolean {
  if (folder.id === candidateId) return true;
  return folders
    .filter((f) => f.parentFolderId === folder.id)
    .some((child) => isSelfOrDescendant(folders, child, candidateId));
}

export const catalogRoutes: Route[] = [
  // --- machines ------------------------------------------------------------

  route('GET', '/machines', () => json(getWorld().machines)),

  route('GET', '/machines/options', () =>
    json(
      getWorld().machines.map((m) => ({
        id: m.id,
        name: m.name,
        hostname: m.hostname,
        winRmPort: m.winRmPort,
        useSsl: m.useSsl,
        defaultCredentialId: m.defaultCredentialId,
        tags: m.tags,
        lastConnectivityCheck: m.lastConnectivityCheck,
        isReachable: m.isReachable,
      })),
    )),

  route('POST', '/machines', async (ctx) => {
    const body = await ctx.body<{ name?: string; hostname?: string; winRmPort?: number; useSsl?: boolean; tags?: string | null; defaultCredentialId?: string | null }>();
    const machine = {
      id: runtimeId('machine'),
      name: body?.name?.trim() || 'New Machine',
      hostname: body?.hostname?.trim() || 'host.contoso.example',
      winRmPort: body?.winRmPort ?? 5986,
      useSsl: body?.useSsl ?? true,
      defaultCredentialId: body?.defaultCredentialId ?? null,
      tags: body?.tags ?? null,
      lastConnectivityCheck: null,
      isReachable: false,
      usedByWorkflowCount: 0,
      recentStepCount: 0,
      recentFailedStepCount: 0,
      activeRunCount: 0,
    };
    getWorld().machines.push(machine);
    return json(machine, 201);
  }),

  route('PUT', '/machines/:id', async (ctx) => {
    const machine = getWorld().machines.find((m) => m.id === ctx.params.id);
    if (!machine) return notFound('Machine');
    const body = await ctx.body<Partial<typeof machine>>();
    Object.assign(machine, body, { id: machine.id });
    return json(machine);
  }),

  route('DELETE', '/machines/:id', (ctx) => {
    const world = getWorld();
    if (!world.machines.some((m) => m.id === ctx.params.id)) return notFound('Machine');
    world.machines = world.machines.filter((m) => m.id !== ctx.params.id);
    return noContent();
  }),

  route('POST', '/machines/:id/test', () => notInDemo('Testing a WinRM connection')),

  // --- credentials ---------------------------------------------------------

  route('GET', '/credentials', () => json(getWorld().credentials)),

  route('POST', '/credentials', async (ctx) => {
    const body = await ctx.body<{ name?: string; username?: string; domain?: string | null; expiresAt?: string | null }>();
    const credential = {
      id: runtimeId('credential'),
      name: body?.name?.trim() || 'New Credential',
      username: body?.username?.trim() || 'user',
      domain: body?.domain ?? null,
      expiresAt: body?.expiresAt ?? null,
    };
    getWorld().credentials.push(credential);
    return json(credential, 201);
  }),

  route('PUT', '/credentials/:id', async (ctx) => {
    const credential = getWorld().credentials.find((c) => c.id === ctx.params.id);
    if (!credential) return notFound('Credential');
    // The editor sends a password; the product never returns one. Copying the body wholesale
    // would put it back on the next list read, which is not the shape the product has.
    const { password: _password, ...rest } = await ctx.body<Partial<typeof credential> & { password?: string }>() ?? {};
    Object.assign(credential, rest, { id: credential.id });
    return json(credential);
  }),

  route('DELETE', '/credentials/:id', (ctx) => {
    const world = getWorld();
    if (!world.credentials.some((c) => c.id === ctx.params.id)) return notFound('Credential');
    world.credentials = world.credentials.filter((c) => c.id !== ctx.params.id);
    return noContent();
  }),

  route('POST', '/secrets/reencrypt', () => notInDemo('Re-encrypting stored secrets')),

  // --- global variables ----------------------------------------------------

  route('GET', '/global-variables', () => json(getWorld().globals)),

  route('POST', '/global-variables', async (ctx) => {
    const body = await ctx.body<Partial<DemoGlobalVariable>>();
    const now = new Date().toISOString();
    const variable: DemoGlobalVariable = {
      id: runtimeId('global'),
      name: body?.name?.trim() || 'NewVariable',
      value: body?.isSecret ? null : body?.value ?? null,
      isSecret: body?.isSecret ?? false,
      description: body?.description ?? null,
      folderId: body?.folderId ?? GLOBALS_ROOT_FOLDER_ID,
      createdAt: now,
      updatedAt: now,
      updatedBy: DEMO_USER.username,
    };
    getWorld().globals.push(variable);
    return json(variable, 201);
  }),

  route('PUT', '/global-variables/:id', async (ctx) => {
    const variable = getWorld().globals.find((g) => g.id === ctx.params.id);
    if (!variable) return notFound('Global variable');
    const body = await ctx.body<Partial<DemoGlobalVariable>>();
    Object.assign(variable, body, { id: variable.id, updatedAt: new Date().toISOString(), updatedBy: DEMO_USER.username });
    if (variable.isSecret) variable.value = null;
    return json(variable);
  }),

  route('DELETE', '/global-variables/:id', (ctx) => {
    const world = getWorld();
    if (!world.globals.some((g) => g.id === ctx.params.id)) return notFound('Global variable');
    world.globals = world.globals.filter((g) => g.id !== ctx.params.id);
    return noContent();
  }),

  route('POST', '/global-variables/:id/move-folder', async (ctx) => {
    const world = getWorld();
    const variable = world.globals.find((g) => g.id === ctx.params.id);
    if (!variable) return notFound('Global variable');
    const body = await ctx.body<{ folderId?: string | null }>();
    const folderId = body?.folderId ?? GLOBALS_ROOT_FOLDER_ID;
    if (!world.globalFolders.some((f) => f.id === folderId)) return notFound('Folder');
    variable.folderId = folderId;
    variable.updatedAt = new Date().toISOString();
    variable.updatedBy = DEMO_USER.username;
    return noContent();
  }),

  // --- global variable folders ----------------------------------------------

  route('GET', '/global-variable-folders', () => {
    const world = getWorld();
    // Counted here rather than stored, like every other counter in the demo.
    for (const folder of world.globalFolders) {
      folder.variableCount = world.globals.filter((g) => g.folderId === folder.id).length;
    }
    return json(world.globalFolders);
  }),

  route('POST', '/global-variable-folders', async (ctx) => {
    const world = getWorld();
    const body = await ctx.body<{ name?: string; parentFolderId?: string | null }>();
    const parent = world.globalFolders.find((f) => f.id === (body?.parentFolderId ?? GLOBALS_ROOT_FOLDER_ID));
    const name = body?.name?.trim() || 'New Folder';
    const folder: GlobalFolder = {
      id: runtimeId('global-folder'),
      parentFolderId: parent?.id ?? GLOBALS_ROOT_FOLDER_ID,
      name,
      path: `${parent && parent.path !== '/' ? parent.path : ''}/${name}`,
      depth: (parent?.depth ?? 0) + 1,
      createdAt: new Date().toISOString(),
      createdByUserId: DEMO_USER.id,
      variableCount: 0,
    };
    world.globalFolders.push(folder);
    return json(folder, 201);
  }),

  route('PUT', '/global-variable-folders/:id', async (ctx) => {
    const world = getWorld();
    const folder = world.globalFolders.find((f) => f.id === ctx.params.id);
    if (!folder) return notFound('Folder');
    const body = await ctx.body<{ name?: string }>();
    if (body?.name?.trim()) {
      folder.name = body.name.trim();
      repathSubtree(world.globalFolders, folder);
    }
    return noContent();
  }),

  route('POST', '/global-variable-folders/:id/move', async (ctx) => {
    const world = getWorld();
    const folder = world.globalFolders.find((f) => f.id === ctx.params.id);
    if (!folder) return notFound('Folder');
    if (folder.id === GLOBALS_ROOT_FOLDER_ID) return conflict('ROOT_FOLDER', 'The root folder cannot be moved.');
    const body = await ctx.body<{ newParentFolderId?: string | null }>();
    const targetId = body?.newParentFolderId ?? GLOBALS_ROOT_FOLDER_ID;
    if (!world.globalFolders.some((f) => f.id === targetId)) return notFound('Folder');
    if (isSelfOrDescendant(world.globalFolders, folder, targetId)) {
      return conflict('FOLDER_CYCLE', 'A folder cannot be moved into itself.');
    }
    folder.parentFolderId = targetId;
    repathSubtree(world.globalFolders, folder);
    return noContent();
  }),

  route('DELETE', '/global-variable-folders/:id', (ctx) => {
    const world = getWorld();
    const folder = world.globalFolders.find((f) => f.id === ctx.params.id);
    if (!folder || folder.id === GLOBALS_ROOT_FOLDER_ID) return notFound('Folder');

    const subtree = new Set<string>();
    const collect = (id: string) => {
      if (subtree.has(id)) return;
      subtree.add(id);
      for (const child of world.globalFolders) if (child.parentFolderId === id) collect(child.id);
    };
    collect(folder.id);

    const doomed = world.globals.filter((g) => subtree.has(g.folderId));
    const recursive = ctx.query.get('recursive') === 'true';
    if ((doomed.length > 0 || subtree.size > 1) && !recursive) {
      return conflict('FOLDER_NOT_EMPTY', 'The folder is not empty.');
    }

    world.globals = world.globals.filter((g) => !subtree.has(g.folderId));
    world.globalFolders = world.globalFolders.filter((f) => !subtree.has(f.id));
    return json({ deletedFolders: subtree.size, deletedVariables: doomed.length });
  }),

  // --- shared workflow folders ---------------------------------------------

  route('GET', '/shared-workflow-folders', () => json(getWorld().folders)),

  route('POST', '/shared-workflow-folders', async (ctx) => {
    const body = await ctx.body<{ name?: string; parentFolderId?: string | null }>();
    const parent = getWorld().folders.find((f) => f.id === (body?.parentFolderId ?? ROOT_FOLDER_ID));
    const name = body?.name?.trim() || 'New Folder';
    const folder = {
      id: runtimeId('folder'),
      parentFolderId: parent?.id ?? ROOT_FOLDER_ID,
      name,
      path: `${parent && parent.path !== '/' ? parent.path : ''}/${name}`,
      depth: (parent?.depth ?? 0) + 1,
      createdAt: new Date().toISOString(),
      createdByUserId: DEMO_USER.id,
      workflowCount: 0,
      capabilities: { canRead: true, canRun: true, canEdit: true, canDelete: true, canAdmin: true },
    };
    getWorld().folders.push(folder);
    return json(folder, 201);
  }),

  route('PUT', '/shared-workflow-folders/:id', async (ctx) => {
    const folder = getWorld().folders.find((f) => f.id === ctx.params.id);
    if (!folder) return notFound('Folder');
    const body = await ctx.body<{ name?: string }>();
    if (body?.name?.trim()) {
      folder.name = body.name.trim();
      repathSubtree(getWorld().folders, folder);
    }
    return json(folder);
  }),

  route('POST', '/shared-workflow-folders/:id/move', async (ctx) => {
    const world = getWorld();
    const folder = world.folders.find((f) => f.id === ctx.params.id);
    if (!folder) return notFound('Folder');
    if (folder.id === ROOT_FOLDER_ID) return conflict('ROOT_FOLDER', 'The root folder cannot be moved.');
    const body = await ctx.body<{ newParentFolderId?: string | null }>();
    const targetId = body?.newParentFolderId ?? ROOT_FOLDER_ID;
    if (!world.folders.some((f) => f.id === targetId)) return notFound('Folder');
    if (isSelfOrDescendant(world.folders, folder, targetId)) {
      return conflict('FOLDER_CYCLE', 'A folder cannot be moved into itself.');
    }
    folder.parentFolderId = targetId;
    repathSubtree(world.folders, folder);
    return noContent();
  }),

  route('DELETE', '/shared-workflow-folders/:id', (ctx) => {
    const world = getWorld();
    const folder = world.folders.find((f) => f.id === ctx.params.id);
    if (!folder || folder.id === ROOT_FOLDER_ID) return notFound('Folder');

    // The whole subtree, not just the selected folder: a recursive delete that leaves
    // sub-folders behind also leaves their workflows, and then their executions reference
    // workflows that no longer exist — the dashboard renders those as "Unknown".
    const subtree = new Set<string>();
    const collect = (id: string) => {
      if (subtree.has(id)) return;
      subtree.add(id);
      for (const child of world.folders) if (child.parentFolderId === id) collect(child.id);
    };
    collect(folder.id);

    const doomed = world.workflows.filter((w) => w.folderId && subtree.has(w.folderId));
    const recursive = ctx.query.get('recursive') === 'true';
    if ((doomed.length > 0 || subtree.size > 1) && !recursive) {
      return conflict('FOLDER_NOT_EMPTY', 'The folder is not empty.');
    }

    const doomedWorkflowIds = new Set(doomed.map((w) => w.id));
    const doomedExecutionIds = new Set(
      world.executions.filter((e) => doomedWorkflowIds.has(e.workflowId)).map((e) => e.id),
    );
    for (const executionId of doomedExecutionIds) world.steps.delete(executionId);
    for (const workflowId of doomedWorkflowIds) world.versions.delete(workflowId);
    world.executions = world.executions.filter((e) => !doomedExecutionIds.has(e.id));
    world.workflows = world.workflows.filter((w) => !doomedWorkflowIds.has(w.id));
    world.folders = world.folders.filter((f) => !subtree.has(f.id));

    return json({ deletedFolders: subtree.size, deletedWorkflows: doomed.length });
  }),

  route('GET', '/shared-workflow-folders/:id/permissions', (ctx) =>
    json(getWorld().folderPermissions.filter((p) => p.folderId === ctx.params.id))),

  route('POST', '/shared-workflow-folders/:id/permissions', async (ctx) => {
    const world = getWorld();
    if (!world.folders.some((f) => f.id === ctx.params.id)) return notFound('Folder');
    const body = await ctx.body<Partial<SharedFolderPermission>>();
    const principalKey = body?.principalKey?.trim() ?? '';
    if (!principalKey) return conflict('PRINCIPAL_REQUIRED', 'A principal is required.');
    const duplicate = world.folderPermissions.some(
      (p) => p.folderId === ctx.params.id && p.principalKey === principalKey,
    );
    if (duplicate) return conflict('PERMISSION_EXISTS', 'That principal already has a grant on this folder.');
    const permission: SharedFolderPermission = {
      id: runtimeId('folder-permission'),
      folderId: ctx.params.id,
      principalType: body?.principalType ?? 'User',
      principalKey,
      principalAuthority: body?.principalAuthority ?? null,
      // The product resolves the display name server-side; the demo knows its own users.
      principalDisplayName:
        world.users.find((u) => u.id === principalKey)?.username ?? principalKey,
      role: body?.role ?? 'FolderViewer',
      grantedAt: new Date().toISOString(),
      grantedByUserId: DEMO_USER.id,
    };
    world.folderPermissions.push(permission);
    return json(permission, 201);
  }),

  route('PUT', '/shared-workflow-folders/:id/permissions/:permissionId', async (ctx) => {
    const permission = getWorld().folderPermissions.find((p) => p.id === ctx.params.permissionId);
    if (!permission) return notFound('Permission');
    const body = await ctx.body<{ role?: SharedFolderPermission['role'] }>();
    if (body?.role) permission.role = body.role;
    return noContent();
  }),

  route('DELETE', '/shared-workflow-folders/:id/permissions/:permissionId', (ctx) => {
    const world = getWorld();
    if (!world.folderPermissions.some((p) => p.id === ctx.params.permissionId)) {
      return notFound('Permission');
    }
    world.folderPermissions = world.folderPermissions.filter((p) => p.id !== ctx.params.permissionId);
    return noContent();
  }),

  // Custom activities live in handlers/customActivities.ts.

  // --- users ---------------------------------------------------------------

  route('GET', '/users', () => json(getWorld().users)),

  route('POST', '/users', async (ctx) => {
    const world = getWorld();
    const body = await ctx.body<{ username?: string; role?: UserRow['role'] }>();
    const username = body?.username?.trim() ?? '';
    if (!username) return conflict('USERNAME_REQUIRED', 'A username is required.');
    if (world.users.some((u) => u.username.toLowerCase() === username.toLowerCase())) {
      return conflict('USERNAME_TAKEN', `A user named '${username}' already exists.`);
    }
    const user: UserRow = {
      id: runtimeId('user'),
      username,
      role: body?.role ?? 'Viewer',
      isActive: true,
      createdAt: new Date().toISOString(),
      provider: 'Local',
    };
    world.users.push(user);
    return json(user, 201);
  }),

  route('PUT', '/users/:id', async (ctx) => {
    const user = getWorld().users.find((u) => u.id === ctx.params.id);
    if (!user) return notFound('User');
    // Password resets route through this endpoint too. The demo stores no passwords, so the
    // field is read and dropped rather than kept somewhere it would only leak into a response.
    const body = await ctx.body<{ username?: string; role?: UserRow['role']; isActive?: boolean }>();
    if (body?.username?.trim()) user.username = body.username.trim();
    if (body?.role) user.role = body.role;
    if (typeof body?.isActive === 'boolean') user.isActive = body.isActive;
    return json(user);
  }),

  route('DELETE', '/users/:id', (ctx) => {
    const world = getWorld();
    const user = world.users.find((u) => u.id === ctx.params.id);
    if (!user) return notFound('User');
    if (user.id === DEMO_USER.id) {
      // Mirrors the product: the account you are signed in with cannot delete itself.
      return conflict('SELF_DELETE', 'You cannot delete the account you are signed in with.');
    }
    world.users = world.users.filter((u) => u.id !== user.id);
    return noContent();
  }),

  route('POST', '/users/:id/reactivate', (ctx) => {
    const user = getWorld().users.find((u) => u.id === ctx.params.id);
    if (!user) return notFound('User');
    user.isActive = true;
    return json(user);
  }),

  // --- maintenance windows -------------------------------------------------

  route('GET', '/maintenance-windows', () => json(getWorld().maintenanceWindows)),

  route('GET', '/maintenance-windows/affecting/:workflowId', (ctx) => {
    const world = getWorld();
    const workflow = world.workflows.find((w) => w.id === ctx.params.workflowId);
    return json(
      world.maintenanceWindows.filter((window) => {
        if (!window.isEnabled) return false;
        if (window.scopeKind === 'Global') return true;
        return window.targets.some((t) =>
          t.targetKind === 'Workflow' ? t.targetId === workflow?.id : t.targetId === workflow?.folderId,
        );
      }),
    );
  }),

  route('POST', '/maintenance-windows', async (ctx) => {
    const body = await ctx.body<Partial<DemoMaintenanceWindow>>();
    const now = new Date().toISOString();
    const window: DemoMaintenanceWindow = {
      id: runtimeId('maintenance-window'),
      name: body?.name?.trim() || 'New window',
      description: body?.description ?? null,
      isEnabled: body?.isEnabled ?? true,
      mode: body?.mode ?? 'Blackout',
      scopeKind: body?.scopeKind ?? 'Global',
      recurrence: body?.recurrence ?? 'Weekly',
      oneTimeStartUtc: body?.oneTimeStartUtc ?? null,
      oneTimeEndUtc: body?.oneTimeEndUtc ?? null,
      weeklyDaysMask: body?.weeklyDaysMask ?? 0,
      weeklyStartMinuteOfDay: body?.weeklyStartMinuteOfDay ?? null,
      weeklyEndMinuteOfDay: body?.weeklyEndMinuteOfDay ?? null,
      cronExpression: body?.cronExpression ?? null,
      durationMinutes: body?.durationMinutes ?? null,
      timeZoneId: body?.timeZoneId ?? 'UTC',
      targets: body?.targets ?? [],
      createdAt: now,
      updatedAt: now,
      updatedBy: DEMO_USER.username,
    };
    getWorld().maintenanceWindows.push(window);
    return json(window, 201);
  }),

  route('PUT', '/maintenance-windows/:id', async (ctx) => {
    const window = getWorld().maintenanceWindows.find((w) => w.id === ctx.params.id);
    if (!window) return notFound('Maintenance window');
    const body = await ctx.body<Partial<DemoMaintenanceWindow>>();
    Object.assign(window, body, {
      id: window.id,
      createdAt: window.createdAt,
      updatedAt: new Date().toISOString(),
      updatedBy: DEMO_USER.username,
    });
    return json(window);
  }),

  route('DELETE', '/maintenance-windows/:id', (ctx) => {
    const world = getWorld();
    if (!world.maintenanceWindows.some((w) => w.id === ctx.params.id)) {
      return notFound('Maintenance window');
    }
    world.maintenanceWindows = world.maintenanceWindows.filter((w) => w.id !== ctx.params.id);
    return noContent();
  }),

  // Alerting and the audit log live in handlers/alerting.ts and handlers/audit.ts.
];
