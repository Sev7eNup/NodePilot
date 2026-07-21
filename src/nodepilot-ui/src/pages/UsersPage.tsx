import {
  Add,
  Certificate,
  ChevronDown,
  ChevronUp,
  Edit,
  Password,
  Reset,
  Search,
  Security,
  TrashCan,
  UserFollow,
  UserProfile,
  View,
} from '@carbon/icons-react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useMemo, useRef, useState } from 'react';
import { useTranslation, Trans } from 'react-i18next';
import { api } from '../api/client';
import { ModalShell } from '../components/common/ModalShell';
import { MobileCardList } from '../components/common/MobileCardList';
import { useAuthStore } from '../stores/authStore';
import { toast } from '../stores/toastStore';
import { confirmDialog } from '../stores/confirmStore';
import { useIsMobile } from '../hooks/useMediaQuery';
import type { UserRow, CreateUserPayload, UpdateUserPayload } from '../types/api';
import { formatDateOnly, formatDate } from '../lib/format';

type DialogMode =
  | { kind: 'create' }
  | { kind: 'edit'; user: UserRow }
  | { kind: 'password'; user: UserRow }
  | null;

const ROLES: Array<UserRow['role']> = ['Admin', 'Operator', 'Viewer'];

// Role-precedence used as the sort key. Higher number = higher privilege, so
// sorting asc lists viewers first, desc lists admins first — both directions
// have intuitive meaning.
const ROLE_RANK: Record<UserRow['role'], number> = { Viewer: 0, Operator: 1, Admin: 2 };

// Mirrors MachinesPage: ColKey covers every sortable column; ResizableColKey
// drops the auto-flex column (username) which has no explicit width and no
// drag-handle. Username is the primary identifier and absorbs leftover space.
type ColKey = 'username' | 'identity' | 'role' | 'status' | 'directory' | 'created';
type ResizableColKey = Exclude<ColKey, 'username'>;

const ACTIONS_WIDTH = 130; // 3 buttons × ~28px + gap-1 + px-4 cell padding
const USERNAME_MIN_WIDTH = 220;
const DEFAULT_WIDTHS: Record<ResizableColKey, number> = {
  identity: 240, role: 120, status: 130, directory: 190, created: 150,
};

function isExternalUser(user: UserRow): boolean {
  return !!user.provider && user.provider.toLowerCase() !== 'local';
}

function RoleBadge({ role }: Readonly<{ role: UserRow['role'] }>) {
  const meta =
    role === 'Admin'
      ? { icon: Certificate, cls: 'bg-rose-500/15 text-rose-600 dark:text-rose-400' }
      : role === 'Operator'
        ? { icon: Security, cls: 'bg-blue-100 text-blue-700' }
        : { icon: View, cls: 'bg-surface-container text-on-surface-variant' };
  const Icon = meta.icon;
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-medium ${meta.cls}`}>
      <Icon size={11} />
      {role}
    </span>
  );
}

function AccountStatusBadge({ user }: Readonly<{ user: UserRow }>) {
  const { t } = useTranslation('users');
  if (user.isTombstoned) {
    return (
      <span className="inline-flex px-2 py-0.5 rounded-full text-[11px] font-medium bg-red-500/15 text-red-700 dark:text-red-400">
        {t('statusTombstoned')}
      </span>
    );
  }
  return user.isActive ? (
    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-medium bg-green-500/15 text-green-600 dark:text-green-400">
      <UserFollow size={11} /> {t('statusActive')}
    </span>
  ) : (
    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-medium bg-surface-container text-on-surface-variant">
      <UserProfile size={11} /> {t('statusInactive')}
    </span>
  );
}

function IdentitySummary({ user }: Readonly<{ user: UserRow }>) {
  const { t } = useTranslation('users');
  const provider = user.provider || 'Local';
  return (
    <div className="min-w-0">
      <span className="inline-flex rounded bg-surface-container px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-on-surface-variant">
        {provider}
      </span>
      {user.authority && (
        <div className="mt-1 truncate text-[11px] text-on-surface-variant" title={user.authority}>
          {t('identityAuthority')}: {user.authority}
        </div>
      )}
      {user.subject && (
        <div className="truncate font-mono text-[10px] text-outline" title={user.subject}>
          {t('identitySubject')}: {user.subject}
        </div>
      )}
    </div>
  );
}

function DirectorySyncSummary({ user }: Readonly<{ user: UserRow }>) {
  const { t } = useTranslation('users');
  if (!isExternalUser(user)) return <span className="text-xs text-outline">{t('notApplicable')}</span>;
  const status = user.directorySyncStatus ?? 'Never';
  const statusClass = status === 'Healthy' || status === 'Current'
    ? 'bg-green-500/15 text-green-700 dark:text-green-400'
    : status === 'Stale'
      ? 'bg-amber-500/15 text-amber-700 dark:text-amber-400'
      : status === 'Failed'
        ? 'bg-red-500/15 text-red-700 dark:text-red-400'
        : 'bg-surface-container text-on-surface-variant';
  return (
    <div className="min-w-0">
      <span className={`inline-flex rounded-full px-2 py-0.5 text-[10px] font-medium ${statusClass}`}>
        {t(`syncStatus.${status}`, { defaultValue: status })}
      </span>
      <div className="mt-1 truncate text-[10px] text-outline" title={user.lastDirectorySyncAt ? formatDate(user.lastDirectorySyncAt) : undefined}>
        {user.lastDirectorySyncAt ? formatDate(user.lastDirectorySyncAt) : t('neverSynced')}
      </div>
    </div>
  );
}

export function UsersPage() {
  const { t } = useTranslation(['users', 'common']);
  const queryClient = useQueryClient();
  const currentUsername = useAuthStore((s) => s.username);
  const isMobile = useIsMobile();
  const [dialog, setDialog] = useState<DialogMode>(null);

  const [search, setSearch] = useState('');
  const [sortBy, setSortBy] = useState<ColKey | null>('username');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');

  // Column resizing (same pattern as MachinesPage). Username is the auto-flex
  // column, so it has no inline width and no drag-handle.
  const [colWidths, setColWidths] = useState(DEFAULT_WIDTHS);
  const tableMinWidth = useMemo(
    () => Object.values(colWidths).reduce((a, b) => a + b, 0) + ACTIONS_WIDTH + USERNAME_MIN_WIDTH,
    [colWidths],
  );
  const resizeRef = useRef<{ col: ResizableColKey; startX: number; startWidth: number } | null>(null);

  const startResize = (col: ResizableColKey, e: React.MouseEvent) => {
    e.preventDefault();
    resizeRef.current = { col, startX: e.clientX, startWidth: colWidths[col] };
    const onMove = (ev: MouseEvent) => {
      if (!resizeRef.current) return;
      const { col, startWidth, startX } = resizeRef.current;
      const w = Math.max(50, startWidth + ev.clientX - startX);
      setColWidths((prev) => ({ ...prev, [col]: w }));
    };
    const onUp = () => {
      resizeRef.current = null;
      globalThis.removeEventListener('mousemove', onMove);
      globalThis.removeEventListener('mouseup', onUp);
    };
    globalThis.addEventListener('mousemove', onMove);
    globalThis.addEventListener('mouseup', onUp);
  };

  const handleSort = (col: ColKey) => {
    if (sortBy === col) setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    else { setSortBy(col); setSortDir('asc'); }
  };

  const { data: users, isLoading } = useQuery({
    queryKey: ['users'],
    queryFn: () => api.get<UserRow[]>('/users'),
  });

  const createMutation = useMutation({
    mutationFn: (body: CreateUserPayload) => api.post<UserRow>('/users', body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] });
      setDialog(null);
    },
    onError: (err: Error) => toast.error(t('common:createFailed', { message: err.message })),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, body }: { id: string; body: UpdateUserPayload }) => api.put(`/users/${id}`, body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] });
      setDialog(null);
    },
    onError: (err: Error) => toast.error(t('common:updateFailed', { message: err.message })),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/users/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['users'] }),
    onError: (err: Error) => toast.error(t('common:deleteFailed', { message: err.message })),
  });

  const reactivateMutation = useMutation({
    mutationFn: (id: string) => api.post<void>(`/users/${id}/reactivate`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['users'] }),
    onError: (err: Error) => toast.error(t('common:updateFailed', { message: err.message })),
  });

  const reactivate = async (user: UserRow) => {
    if (await confirmDialog({ message: t('users:reactivateConfirm', { username: user.username }) })) {
      reactivateMutation.mutate(user.id);
    }
  };

  const filteredSorted = useMemo(() => {
    let list = users ?? [];
    const term = search.trim().toLowerCase();
    if (term) {
      list = list.filter((u) =>
        u.username.toLowerCase().includes(term)
        || u.role.toLowerCase().includes(term)
        || u.provider?.toLowerCase().includes(term)
        || u.authority?.toLowerCase().includes(term)
        || u.subject?.toLowerCase().includes(term)
        || u.directorySyncStatus?.toLowerCase().includes(term),
      );
    }
    if (!sortBy) return list;
    return [...list].sort((a, b) => {
      let cmp = 0;
      switch (sortBy) {
        case 'username': cmp = a.username.localeCompare(b.username); break;
        case 'identity': cmp = (a.provider ?? 'Local').localeCompare(b.provider ?? 'Local'); break;
        case 'role':     cmp = ROLE_RANK[a.role] - ROLE_RANK[b.role]; break;
        case 'status':   cmp = Number(!!b.isTombstoned) - Number(!!a.isTombstoned)
          || Number(b.isActive) - Number(a.isActive); break;
        case 'directory': cmp = (a.lastDirectorySyncAt ?? '').localeCompare(b.lastDirectorySyncAt ?? ''); break;
        case 'created':  cmp = a.createdAt.localeCompare(b.createdAt); break;
      }
      return sortDir === 'asc' ? cmp : -cmp;
    });
  }, [users, search, sortBy, sortDir]);

  const totalCount = users?.length ?? 0;

  return (
    <div className="max-w-7xl mx-auto np-fade-up">
      <div className="flex items-center justify-between mb-6">
        <div>
          {/* Trans rendert die <strong>-Tags aus dem i18n-String als echte React-Elemente,
              statt sie via dangerouslySetInnerHTML zu injizieren. Damit kann ein zukünftiger
              Translator keinen unbeabsichtigten XSS-Payload mehr einschmuggeln. */}
          <p className="text-sm text-on-surface-variant mt-1">
            <Trans i18nKey="users:subtitle" components={{ strong: <strong /> }} />
          </p>
        </div>
        <button
          onClick={() => setDialog({ kind: 'create' })}
          title={t('users:newUser')}
          className="flex items-center gap-2 px-3 py-2 sm:px-4 bg-blue-600 text-white rounded-md hover:bg-blue-700 text-sm"
        >
          <Add size={16} /> <span className="hidden sm:inline">{t('users:newUser')}</span>
        </button>
      </div>
      {/* Toolbar: full-width search box. Mirrors MachinesPage so both admin
          surfaces feel consistent. Hidden when the list is empty. */}
      {totalCount > 0 && (
        <div className="np-card p-3 mb-3 flex flex-wrap items-center gap-3">
          <div className="relative w-full sm:flex-1 sm:min-w-[220px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-outline" />
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder={t('users:searchPlaceholder')}
              className="w-full pl-8 pr-3 py-1.5 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
        </div>
      )}
      {isLoading ? (
        <p className="text-outline">{t('common:loadingDots')}</p>
      ) : !users || users.length === 0 ? (
        <div className="np-card p-8 text-center text-outline">
          {t('users:noUsers')}
        </div>
      ) : filteredSorted.length === 0 ? (
        <div className="np-card p-8 text-center text-outline">
          {t('users:noMatch')}
        </div>
      ) : isMobile ? (
        <MobileCardList
          items={filteredSorted}
          getKey={(u) => u.id}
          renderTitle={(u) => (
            <div className="flex items-center gap-2 min-w-0">
              <span className="text-sm font-semibold text-on-surface truncate">{u.username}</span>
              {u.username === currentUsername && (
                <span className="text-[10px] font-medium text-blue-600 bg-blue-50 rounded px-1.5 py-0.5 shrink-0">
                  {t('users:youBadge')}
                </span>
              )}
              {u.isBreakGlass && (
                <span className="text-[10px] font-medium text-amber-700 bg-amber-500/15 rounded px-1.5 py-0.5 shrink-0">
                  {t('users:breakGlassBadge')}
                </span>
              )}
            </div>
          )}
          renderFields={(u) => [
            { label: t('users:tableHeaders.identity'), value: <IdentitySummary user={u} /> },
            { label: t('users:tableHeaders.role'), value: <RoleBadge role={u.role} /> },
            {
              label: t('users:tableHeaders.status'),
              value: <AccountStatusBadge user={u} />,
            },
            { label: t('users:tableHeaders.directory'), value: <DirectorySyncSummary user={u} /> },
            {
              label: t('users:tableHeaders.created'),
              value: <span className="text-xs text-on-surface-variant" title={formatDate(u.createdAt)}>{formatDateOnly(u.createdAt)}</span>,
            },
          ]}
          renderActions={(u) => {
            const isSelf = u.username === currentUsername;
            if (u.isTombstoned) {
              return (
                <button
                  onClick={() => reactivate(u)}
                  disabled={reactivateMutation.isPending}
                  className="p-2 text-green-700 hover:bg-green-500/15 rounded-lg disabled:opacity-30"
                  title={t('users:reactivateTitle')}
                >
                  <Reset size={16} />
                </button>
              );
            }
            return (
              <>
                <button
                  onClick={() => setDialog({ kind: 'edit', user: u })}
                  className="p-2 text-blue-600 hover:bg-blue-50 rounded-lg"
                  title={t('users:editRoleActiveTitle')}
                >
                  <Edit size={16} />
                </button>
                {!isExternalUser(u) && (
                  <button
                    onClick={() => setDialog({ kind: 'password', user: u })}
                    className="p-2 text-amber-600 hover:bg-amber-500/15 rounded-lg"
                    title={t('users:resetPasswordTitle')}
                  >
                    <Password size={16} />
                  </button>
                )}
                <button
                  onClick={async () => {
                    if (isSelf) { toast.info(t('users:deleteSelfWarn')); return; }
                    if (await confirmDialog({ message: t('users:deleteConfirm', { username: u.username }), danger: true }))
                      deleteMutation.mutate(u.id);
                  }}
                  disabled={isSelf}
                  className="p-2 text-red-600 hover:bg-red-500/15 rounded-lg disabled:opacity-30 disabled:cursor-not-allowed"
                  title={isSelf ? t('users:cantDeleteSelf') : t('common:delete')}
                >
                  <TrashCan size={16} />
                </button>
              </>
            );
          }}
        />
      ) : (
        <div className="np-card overflow-hidden">
          <div className="overflow-x-auto">
          <table
            style={{
              tableLayout: 'fixed',
              width: '100%',
              minWidth: tableMinWidth,
            }}
          >
            <thead className="np-col-header text-left text-xs font-semibold uppercase tracking-wide text-on-surface-variant">
              <tr>
                {/* Username = auto-flex. No explicit width, no resize handle —
                    it absorbs whatever horizontal space the fixed columns leave. */}
                <th style={{ minWidth: USERNAME_MIN_WIDTH }} className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
                  <button
                    onClick={() => handleSort('username')}
                    className="flex items-center gap-1 hover:text-on-surface transition-colors"
                  >
                    {t('users:tableHeaders.username')}
                    {sortBy === 'username'
                      ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />)
                      : <span className="w-3" />}
                  </button>
                </th>
                {/* Fixed-width sortable + resizable columns. */}
                {([
                  ['identity', t('users:tableHeaders.identity')],
                  ['role', t('users:tableHeaders.role')],
                  ['status', t('users:tableHeaders.status')],
                  ['directory', t('users:tableHeaders.directory')],
                  ['created', t('users:tableHeaders.created')],
                ] as [ResizableColKey, string][]).map(([col, label]) => (
                  <th key={col} style={{ width: colWidths[col] }} className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
                    <button
                      onClick={() => handleSort(col)}
                      className="flex items-center gap-1 hover:text-on-surface transition-colors"
                    >
                      {label}
                      {sortBy === col
                        ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />)
                        : <span className="w-3" />}
                    </button>
                    <div
                      onMouseDown={(e) => startResize(col, e)}
                      className="absolute right-0 top-0 h-full w-px cursor-col-resize bg-on-surface-variant/20 hover:bg-blue-400/70 active:bg-blue-500/80 transition-colors"
                    />
                  </th>
                ))}
                <th style={{ width: ACTIONS_WIDTH }} className="px-4 py-2 text-left">{t('users:tableHeaders.actions')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-outline/30">
              {filteredSorted.map((u) => {
                const isSelf = u.username === currentUsername;
                return (
                  <tr key={u.id} className="hover:bg-surface-low">
                    <td className="px-4 py-2 overflow-hidden">
                      <div className="flex items-center gap-2 min-w-0">
                        <span className="text-sm font-semibold text-on-surface-variant truncate">{u.username}</span>
                        {isSelf && (
                          <span className="text-[10px] font-medium text-blue-600 bg-blue-50 rounded px-1.5 py-0.5 shrink-0">
                            {t('users:youBadge')}
                          </span>
                        )}
                        {u.isBreakGlass && (
                          <span className="text-[10px] font-medium text-amber-700 bg-amber-500/15 rounded px-1.5 py-0.5 shrink-0">
                            {t('users:breakGlassBadge')}
                          </span>
                        )}
                      </div>
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <IdentitySummary user={u} />
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <RoleBadge role={u.role} />
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <AccountStatusBadge user={u} />
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <DirectorySyncSummary user={u} />
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <span className="text-xs text-on-surface-variant truncate block" title={formatDate(u.createdAt)}>
                        {formatDateOnly(u.createdAt)}
                      </span>
                    </td>
                    <td className="px-4 py-2 overflow-hidden">
                      <div className="flex items-center gap-1 whitespace-nowrap">
                        {u.isTombstoned ? (
                          <button
                            onClick={() => reactivate(u)}
                            disabled={reactivateMutation.isPending}
                            className="p-1.5 text-green-700 hover:bg-green-500/15 rounded-lg disabled:opacity-30"
                            title={t('users:reactivateTitle')}
                          >
                            <Reset size={16} />
                          </button>
                        ) : (
                          <>
                        <button
                          onClick={() => setDialog({ kind: 'edit', user: u })}
                          className="p-1.5 text-blue-600 hover:bg-blue-50 rounded-lg"
                          title={t('users:editRoleActiveTitle')}
                        >
                          <Edit size={16} />
                        </button>
                        {!isExternalUser(u) && (
                          <button
                            onClick={() => setDialog({ kind: 'password', user: u })}
                            className="p-1.5 text-amber-600 hover:bg-amber-500/15 rounded-lg"
                            title={t('users:resetPasswordTitle')}
                          >
                            <Password size={16} />
                          </button>
                        )}
                        <button
                          onClick={async () => {
                            if (isSelf) {
                              toast.info(t('users:deleteSelfWarn'));
                              return;
                            }
                            if (await confirmDialog({ message: t('users:deleteConfirm', { username: u.username }), danger: true }))
                              deleteMutation.mutate(u.id);
                          }}
                          disabled={isSelf}
                          className="p-1.5 text-red-600 hover:bg-red-500/15 rounded-lg disabled:opacity-30 disabled:cursor-not-allowed"
                          title={isSelf ? t('users:cantDeleteSelf') : t('common:delete')}
                        >
                          <TrashCan size={16} />
                        </button>
                          </>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          </div>
        </div>
      )}
      {dialog?.kind === 'create' && (
        <CreateUserDialog
          onCancel={() => setDialog(null)}
          onSubmit={(body) => createMutation.mutate(body)}
          pending={createMutation.isPending}
        />
      )}
      {dialog?.kind === 'edit' && (
        <EditUserDialog
          user={dialog.user}
          isSelf={dialog.user.username === currentUsername}
          onCancel={() => setDialog(null)}
          onSubmit={(body) => updateMutation.mutate({ id: dialog.user.id, body })}
          pending={updateMutation.isPending}
        />
      )}
      {dialog?.kind === 'password' && (
        <PasswordDialog
          user={dialog.user}
          onCancel={() => setDialog(null)}
          onSubmit={(password) => updateMutation.mutate({ id: dialog.user.id, body: { password } })}
          pending={updateMutation.isPending}
        />
      )}
    </div>
  );
}

function DialogShell({ title, onCancel, children }: Readonly<{ title: string; onCancel: () => void; children: React.ReactNode }>) {
  return (
    <ModalShell onClose={onCancel}>
      <h3 className="text-lg font-semibold mb-4 text-on-surface">{title}</h3>
      {children}
    </ModalShell>
  );
}

function CreateUserDialog({
  onCancel, onSubmit, pending,
}: Readonly<{
  onCancel: () => void;
  onSubmit: (body: CreateUserPayload) => void;
  pending: boolean;
}>) {
  const { t } = useTranslation(['users', 'common']);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<UserRow['role']>('Viewer');
  const [isBreakGlass, setIsBreakGlass] = useState(false);

  const valid = username.trim().length >= 1 && password.length >= 8;

  return (
    <DialogShell title={t('users:createTitle')} onCancel={onCancel}>
      <div className="space-y-3">
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('users:fields.username')}</label>
          <input
            type="text"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            autoFocus
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('users:fields.password')}</label>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            autoComplete="new-password"
          />
          <p className="text-[11px] text-outline mt-0.5">{t('users:passwordMin')}</p>
        </div>
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('users:fields.role')}</label>
          <select
            value={role}
            onChange={(e) => setRole(e.target.value as UserRow['role'])}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            {ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
        </div>
        <label className="flex items-start gap-2 text-sm text-on-surface cursor-pointer">
          <input
            type="checkbox"
            checked={isBreakGlass}
            onChange={(e) => setIsBreakGlass(e.target.checked)}
            className="rounded mt-0.5"
          />
          <span>
            {t('users:fields.breakGlass')}
            <span className="block text-[11px] text-outline">{t('users:breakGlassHint')}</span>
          </span>
        </label>
      </div>
      <div className="flex justify-end gap-2 mt-5">
        <button
          onClick={onCancel}
          className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md"
        >
          {t('common:cancel')}
        </button>
        <button
          onClick={() => onSubmit({ username: username.trim(), password, role, isBreakGlass })}
          disabled={!valid || pending}
          className="px-4 py-2 bg-blue-600 text-white text-sm rounded-md hover:bg-blue-700 disabled:opacity-50"
        >
          {pending ? t('users:creating') : t('common:create')}
        </button>
      </div>
    </DialogShell>
  );
}

function EditUserDialog({
  user, isSelf, onCancel, onSubmit, pending,
}: Readonly<{
  user: UserRow;
  isSelf: boolean;
  onCancel: () => void;
  onSubmit: (body: UpdateUserPayload) => void;
  pending: boolean;
}>) {
  const { t } = useTranslation(['users', 'common']);
  const [role, setRole] = useState<UserRow['role']>(user.role);
  const [isActive, setIsActive] = useState(user.isActive);
  const [isBreakGlass, setIsBreakGlass] = useState(user.isBreakGlass ?? false);
  const isLocal = !isExternalUser(user);

  const changed = role !== user.role || isActive !== user.isActive
    || (isLocal && isBreakGlass !== (user.isBreakGlass ?? false));

  return (
    <DialogShell title={t('users:editTitle', { username: user.username })} onCancel={onCancel}>
      <div className="space-y-3">
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('users:fields.role')}</label>
          <select
            value={role}
            onChange={(e) => setRole(e.target.value as UserRow['role'])}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            {ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
          {isSelf && (
            <p className="text-[11px] text-amber-600 mt-0.5">{t('users:demoteSelfWarning')}</p>
          )}
        </div>
        <label className="flex items-center gap-2 text-sm text-on-surface cursor-pointer">
          <input
            type="checkbox"
            checked={isActive}
            onChange={(e) => setIsActive(e.target.checked)}
            className="rounded"
          />
          {t('users:fields.active')}
        </label>
        {isLocal && (
          <label className="flex items-start gap-2 text-sm text-on-surface cursor-pointer">
            <input
              type="checkbox"
              checked={isBreakGlass}
              onChange={(e) => setIsBreakGlass(e.target.checked)}
              className="rounded mt-0.5"
            />
            <span>
              {t('users:fields.breakGlass')}
              <span className="block text-[11px] text-outline">{t('users:breakGlassHint')}</span>
            </span>
          </label>
        )}
      </div>
      <div className="flex justify-end gap-2 mt-5">
        <button
          onClick={onCancel}
          className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md"
        >
          {t('common:cancel')}
        </button>
        <button
          onClick={() => onSubmit({ role, isActive, isBreakGlass: isLocal ? isBreakGlass : undefined })}
          disabled={!changed || pending}
          className="px-4 py-2 bg-blue-600 text-white text-sm rounded-md hover:bg-blue-700 disabled:opacity-50"
        >
          {pending ? t('users:saving') : t('common:save')}
        </button>
      </div>
    </DialogShell>
  );
}

function PasswordDialog({
  user, onCancel, onSubmit, pending,
}: Readonly<{
  user: UserRow;
  onCancel: () => void;
  onSubmit: (password: string) => void;
  pending: boolean;
}>) {
  const { t } = useTranslation(['users', 'common']);
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');

  const match = password === confirm;
  const valid = password.length >= 8 && match;

  return (
    <DialogShell title={t('users:passwordTitle', { username: user.username })} onCancel={onCancel}>
      <div className="space-y-3">
        <p className="text-xs text-on-surface-variant">
          {t('users:resetPasswordHint')}
        </p>
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('users:fields.newPassword')}</label>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            autoFocus
            autoComplete="new-password"
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('users:fields.confirmPassword')}</label>
          <input
            type="password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            autoComplete="new-password"
          />
          {confirm && !match && (
            <p className="text-[11px] text-red-600 mt-0.5">{t('users:passwordsDontMatch')}</p>
          )}
        </div>
      </div>
      <div className="flex justify-end gap-2 mt-5">
        <button
          onClick={onCancel}
          className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md"
        >
          {t('common:cancel')}
        </button>
        <button
          onClick={() => onSubmit(password)}
          disabled={!valid || pending}
          className="px-4 py-2 bg-amber-600 text-white text-sm rounded-md hover:bg-amber-700 disabled:opacity-50"
        >
          {pending ? t('users:saving') : t('users:resetPasswordButton')}
        </button>
      </div>
    </DialogShell>
  );
}
