import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  sharedFoldersApi,
  ACTIVE_DIRECTORY_AUTHORITY,
  type FolderPrincipalType,
  type SharedFolderPermission,
  type SharedFolderRole,
} from '../../api/sharedFolders';
import { confirmDialog } from '../../stores/confirmStore';
import {
  assertAuthBoundaryGenerationCurrent,
  captureAuthBoundaryGeneration,
  isAuthBoundaryGenerationCurrent,
} from '../../security/authBoundary';

/**
 * Admin-only modal: list/grant/revoke folder permissions for one
 * <c>SharedWorkflowFolder</c>. Grants can target a user or an authority-scoped directory group.
 * Both entry points (the folder-tree right-click entry and the button under the folder
 * card) only render when the folder's <c>capabilities.canAdmin</c> is true, so this modal
 * does not need to enforce admin-only itself; the API enforces 403 on Grant/Revoke
 * if the caller lacks permission.
 */
export interface SharedFolderPermissionsModalProps {
  folderId: string;
  folderPath: string;
  /** Available users for the principal-picker — caller passes the result of GET /api/users. */
  users: { id: string; username: string }[];
  onClose: () => void;
}

const ROLES: SharedFolderRole[] = ['FolderViewer', 'FolderOperator', 'FolderEditor', 'FolderAdmin'];

export function SharedFolderPermissionsModal({
  folderId,
  folderPath,
  users,
  onClose,
}: Readonly<SharedFolderPermissionsModalProps>) {
  const { t } = useTranslation(['workflows', 'common']);
  const [permissions, setPermissions] = useState<SharedFolderPermission[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [principalType, setPrincipalType] = useState<Extract<FolderPrincipalType, 'User' | 'Group'>>('User');
  const [principalKey, setPrincipalKey] = useState('');
  const [groupAuthorityMode, setGroupAuthorityMode] = useState<'ad' | 'oidc'>('ad');
  const [groupAuthority, setGroupAuthority] = useState('');
  const [pickedRole, setPickedRole] = useState<SharedFolderRole>('FolderViewer');

  const reload = async (
    authBoundaryGeneration = captureAuthBoundaryGeneration(),
  ) => {
    setLoading(true);
    setError(null);
    try {
      const list = await sharedFoldersApi.listPermissions(folderId);
      assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
      setPermissions(list);
    } catch (e) {
      if (!isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
      setError((e as Error).message);
    } finally {
      if (isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) setLoading(false);
    }
  };

  useEffect(() => {
    reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [folderId]);

  const grant = async () => {
    const authBoundaryGeneration = captureAuthBoundaryGeneration();
    const key = principalKey.trim();
    if (!key) return;
    setBusy(true);
    setError(null);
    try {
      const authority = principalType === 'Group'
        ? (groupAuthorityMode === 'ad' ? ACTIVE_DIRECTORY_AUTHORITY : groupAuthority.trim())
        : undefined;
      await sharedFoldersApi.grantPermission(folderId, principalType, key, pickedRole, authority);
      assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
      setPrincipalKey('');
      setPickedRole('FolderViewer');
      await reload(authBoundaryGeneration);
    } catch (e) {
      if (!isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
      setError((e as Error).message);
    } finally {
      if (isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) setBusy(false);
    }
  };

  const updateRole = async (perm: SharedFolderPermission, role: SharedFolderRole) => {
    const authBoundaryGeneration = captureAuthBoundaryGeneration();
    setBusy(true);
    setError(null);
    try {
      await sharedFoldersApi.updatePermission(folderId, perm.id, role);
      assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
      await reload(authBoundaryGeneration);
    } catch (e) {
      if (!isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
      setError((e as Error).message);
    } finally {
      if (isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) setBusy(false);
    }
  };

  const revoke = async (perm: SharedFolderPermission) => {
    const authBoundaryGeneration = captureAuthBoundaryGeneration();
    const ok = await confirmDialog({
      message: t('workflows:folder.revokePermissionConfirm', { name: perm.principalDisplayName ?? perm.principalKey }),
      danger: true,
    });
    if (!ok || !isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
    setBusy(true);
    setError(null);
    try {
      await sharedFoldersApi.revokePermission(folderId, perm.id);
      assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
      await reload(authBoundaryGeneration);
    } catch (e) {
      if (!isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
      setError((e as Error).message);
    } finally {
      if (isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) setBusy(false);
    }
  };

  // Users that don't have a grant yet — keep the picker tidy.
  const unassignedUsers = users.filter(
    (u) => !permissions.some((p) => p.principalType === 'User' && p.principalKey === u.id),
  );
  const hasExistingPrincipal = permissions.some(
    (p) => p.principalType === principalType
      && p.principalKey.toLowerCase() === principalKey.trim().toLowerCase()
      && (principalType !== 'Group'
        || p.principalAuthority === (groupAuthorityMode === 'ad'
          ? ACTIVE_DIRECTORY_AUTHORITY
          : groupAuthority.trim())),
  );

  const selectClass =
    'rounded border border-outline-variant bg-surface-lowest px-2 py-1 text-sm text-on-surface focus:border-primary focus:outline-none disabled:opacity-50';

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/20 backdrop-blur-sm"
      data-testid="shared-folder-permissions-modal"
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
      onKeyDown={(e) => e.key === 'Escape' && onClose()}
      role="presentation"
      tabIndex={-1}
    >
      <div className="w-[600px] max-w-[90vw] rounded-lg bg-surface-lowest p-4 shadow-2xl border border-outline-variant/30">
        <div className="flex items-center justify-between border-b border-outline-variant/30 pb-2">
          <h2 className="text-lg font-semibold text-on-surface">{t('workflows:folder.permissionsFor', { path: folderPath })}</h2>
          <button
            type="button"
            className="rounded px-2 text-on-surface-variant hover:text-on-surface hover:bg-surface-high transition-colors"
            onClick={onClose}
          >
            ✕
          </button>
        </div>

        {error && (
          <div className="mt-2 rounded bg-error-container px-2 py-1 text-sm text-on-error-container">
            {error}
          </div>
        )}

        <div className="mt-3">
          <h3 className="mb-1 text-sm font-medium text-on-surface">{t('workflows:folder.perms.existing')}</h3>
          {loading ? (
            <div className="text-sm text-on-surface-variant">{t('common:loadingDots')}</div>
          ) : permissions.length === 0 ? (
            <div className="text-sm text-on-surface-variant">{t('workflows:folder.perms.none')}</div>
          ) : (
            <table className="w-full text-sm">
              <thead className="text-xs text-on-surface-variant">
                <tr>
                  <th className="text-left font-medium pb-1">{t('workflows:folder.perms.principal')}</th>
                  <th className="text-left font-medium pb-1">{t('workflows:folder.perms.role')}</th>
                  <th className="w-20"></th>
                </tr>
              </thead>
              <tbody>
                {permissions.map((p) => (
                  <tr key={p.id} className="border-t border-outline-variant/20">
                    <td className="py-1 text-on-surface">
                      {p.principalDisplayName ?? p.principalKey}
                      <span className="ml-2 text-xs text-outline">{p.principalType}</span>
                      {p.principalType === 'Group' && p.principalAuthority && (
                        <div className="text-[11px] text-outline break-all">{p.principalAuthority}</div>
                      )}
                    </td>
                    <td className="py-1">
                      <select
                        className={selectClass}
                        value={p.role}
                        disabled={busy}
                        onChange={(e) => updateRole(p, e.target.value as SharedFolderRole)}
                      >
                        {ROLES.map((r) => (
                          <option key={r} value={r}>
                            {r}
                          </option>
                        ))}
                      </select>
                    </td>
                    <td className="py-1">
                      <button
                        type="button"
                        className="text-xs text-error hover:underline disabled:opacity-50"
                        disabled={busy}
                        onClick={() => revoke(p)}
                        data-testid="shared-folder-perms-revoke-btn"
                      >
                        {t('workflows:folder.perms.revoke')}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div className="mt-4 border-t border-outline-variant/30 pt-3">
          <h3 className="mb-1 text-sm font-medium text-on-surface">{t('workflows:folder.perms.new')}</h3>
          <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr_auto_auto] items-center gap-2">
            <select
              className={selectClass}
              value={principalType}
              onChange={(e) => {
                setPrincipalType(e.target.value as 'User' | 'Group');
                setPrincipalKey('');
              }}
              disabled={busy}
              data-testid="shared-folder-perms-principal-type"
              aria-label={t('workflows:folder.perms.principalType')}
            >
              <option value="User">{t('workflows:folder.perms.typeUser')}</option>
              <option value="Group">{t('workflows:folder.perms.typeGroup')}</option>
            </select>
            {principalType === 'User' ? (
            <select
              className={`flex-1 ${selectClass}`}
              value={principalKey}
              onChange={(e) => setPrincipalKey(e.target.value)}
              disabled={busy}
              data-testid="shared-folder-perms-user-picker"
              aria-label={t('workflows:folder.perms.user')}
            >
              <option value="">{t('workflows:folder.perms.pickUser')}</option>
              {unassignedUsers.map((u) => (
                <option key={u.id} value={u.id}>
                  {u.username}
                </option>
              ))}
            </select>
            ) : (
              <div className="flex min-w-0 flex-col gap-1">
                <select
                  className={selectClass}
                  value={groupAuthorityMode}
                  onChange={(e) => {
                    setGroupAuthorityMode(e.target.value as 'ad' | 'oidc');
                    setGroupAuthority('');
                    setPrincipalKey('');
                  }}
                  disabled={busy}
                  data-testid="shared-folder-perms-group-authority-mode"
                  aria-label={t('workflows:folder.perms.directoryProvider')}
                >
                  <option value="ad">Active Directory</option>
                  <option value="oidc">OIDC / SCIM</option>
                </select>
                {groupAuthorityMode === 'oidc' && (
                  <input
                    type="url"
                    className={selectClass}
                    value={groupAuthority}
                    onChange={(e) => setGroupAuthority(e.target.value)}
                    disabled={busy}
                    placeholder="https://issuer.example/tenant"
                    data-testid="shared-folder-perms-group-authority"
                    aria-label={t('workflows:folder.perms.oidcIssuer')}
                  />
                )}
              <input
                type="text"
                className={`flex-1 ${selectClass}`}
                value={principalKey}
                onChange={(e) => setPrincipalKey(e.target.value)}
                disabled={busy}
                placeholder={groupAuthorityMode === 'ad'
                  ? 'S-1-5-21-...'
                  : t('workflows:folder.perms.groupIdPlaceholder')}
                data-testid="shared-folder-perms-group-key"
                aria-label={groupAuthorityMode === 'ad'
                  ? t('workflows:folder.perms.adGroupSid')
                  : t('workflows:folder.perms.oidcGroupId')}
              />
              </div>
            )}
            <select
              className={selectClass}
              value={pickedRole}
              onChange={(e) => setPickedRole(e.target.value as SharedFolderRole)}
              disabled={busy}
              data-testid="shared-folder-perms-role-picker"
            >
              {ROLES.map((r) => (
                <option key={r} value={r}>
                  {r}
                </option>
              ))}
            </select>
            <button
              type="button"
              className="rounded bg-primary px-3 py-1 text-sm text-on-primary hover:bg-primary-container disabled:opacity-50"
              onClick={grant}
              disabled={busy
                || !principalKey.trim()
                || (principalType === 'Group' && groupAuthorityMode === 'oidc' && !groupAuthority.trim())
                || hasExistingPrincipal}
              data-testid="shared-folder-perms-grant-btn"
            >
              {t('workflows:folder.perms.grant')}
            </button>
          </div>
          {hasExistingPrincipal && (
            <p className="mt-1 text-xs text-error" role="alert">
              {t('workflows:folder.perms.duplicate')}
            </p>
          )}
        </div>
      </div>
    </div>
  );
}
