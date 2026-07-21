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

/**
 * Admin-only modal: list/grant/revoke folder permissions for one
 * <c>SharedWorkflowFolder</c>. Grants can target a user or an authority-scoped directory group.
 * The "Berechtigungen verwalten" button in the folder tree
 * only renders when the folder's <c>capabilities.canAdmin</c> is true, so this modal
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
  const { t } = useTranslation();
  const [permissions, setPermissions] = useState<SharedFolderPermission[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [principalType, setPrincipalType] = useState<Extract<FolderPrincipalType, 'User' | 'Group'>>('User');
  const [principalKey, setPrincipalKey] = useState('');
  const [groupAuthorityMode, setGroupAuthorityMode] = useState<'ad' | 'oidc'>('ad');
  const [groupAuthority, setGroupAuthority] = useState('');
  const [pickedRole, setPickedRole] = useState<SharedFolderRole>('FolderViewer');

  const reload = async () => {
    setLoading(true);
    setError(null);
    try {
      const list = await sharedFoldersApi.listPermissions(folderId);
      setPermissions(list);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [folderId]);

  const grant = async () => {
    const key = principalKey.trim();
    if (!key) return;
    setBusy(true);
    setError(null);
    try {
      const authority = principalType === 'Group'
        ? (groupAuthorityMode === 'ad' ? ACTIVE_DIRECTORY_AUTHORITY : groupAuthority.trim())
        : undefined;
      await sharedFoldersApi.grantPermission(folderId, principalType, key, pickedRole, authority);
      setPrincipalKey('');
      setPickedRole('FolderViewer');
      await reload();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const updateRole = async (perm: SharedFolderPermission, role: SharedFolderRole) => {
    setBusy(true);
    setError(null);
    try {
      await sharedFoldersApi.updatePermission(folderId, perm.id, role);
      await reload();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const revoke = async (perm: SharedFolderPermission) => {
    const ok = await confirmDialog({
      message: t('workflows:folder.revokePermissionConfirm', { name: perm.principalDisplayName ?? perm.principalKey }),
      danger: true,
    });
    if (!ok) return;
    setBusy(true);
    setError(null);
    try {
      await sharedFoldersApi.revokePermission(folderId, perm.id);
      await reload();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
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
          <h3 className="mb-1 text-sm font-medium text-on-surface">Bestehende Berechtigungen</h3>
          {loading ? (
            <div className="text-sm text-on-surface-variant">Lade …</div>
          ) : permissions.length === 0 ? (
            <div className="text-sm text-on-surface-variant">Keine expliziten Berechtigungen.</div>
          ) : (
            <table className="w-full text-sm">
              <thead className="text-xs text-on-surface-variant">
                <tr>
                  <th className="text-left font-medium pb-1">Principal</th>
                  <th className="text-left font-medium pb-1">Rolle</th>
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
                      >
                        Entfernen
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div className="mt-4 border-t border-outline-variant/30 pt-3">
          <h3 className="mb-1 text-sm font-medium text-on-surface">Neue Berechtigung</h3>
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
              aria-label="Principal-Typ"
            >
              <option value="User">User</option>
              <option value="Group">Directory-Gruppe</option>
            </select>
            {principalType === 'User' ? (
            <select
              className={`flex-1 ${selectClass}`}
              value={principalKey}
              onChange={(e) => setPrincipalKey(e.target.value)}
              disabled={busy}
              data-testid="shared-folder-perms-user-picker"
              aria-label="User"
            >
              <option value="">— User wählen —</option>
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
                  aria-label="Directory-Provider"
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
                    aria-label="OIDC-/SCIM-Issuer"
                  />
                )}
              <input
                type="text"
                className={`flex-1 ${selectClass}`}
                value={principalKey}
                onChange={(e) => setPrincipalKey(e.target.value)}
                disabled={busy}
                placeholder={groupAuthorityMode === 'ad' ? 'S-1-5-21-...' : 'Provider-stabile Gruppen-ID'}
                data-testid="shared-folder-perms-group-key"
                aria-label={groupAuthorityMode === 'ad' ? 'AD-Gruppen-SID' : 'OIDC-/SCIM-Gruppen-ID'}
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
              Vergeben
            </button>
          </div>
          {hasExistingPrincipal && (
            <p className="mt-1 text-xs text-error" role="alert">Dieser Principal hat bereits eine Berechtigung.</p>
          )}
        </div>
      </div>
    </div>
  );
}
