import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useRole } from '../../lib/rbac';
import { useAuthStore } from '../../stores/authStore';

describe('useRole (RBAC client mirror)', () => {
  beforeEach(() => {
    // Reset store to a known shape between tests so role transitions don't leak.
    useAuthStore.setState({
      username: null,
      role: null,
      isAuthenticated: null,
    });
  });

  it('admin_grantsAllPermissions', () => {
    useAuthStore.setState({ role: 'Admin' });
    const { result } = renderHook(() => useRole());

    expect(result.current.role).toBe('Admin');
    expect(result.current.isAdmin).toBe(true);
    expect(result.current.isOperator).toBe(false);
    expect(result.current.isViewer).toBe(false);
    expect(result.current.canWrite).toBe(true);
    expect(result.current.canDelete).toBe(true);
    expect(result.current.canAdmin).toBe(true);
  });

  it('operator_canWrite_butCannotDeleteOrAdmin', () => {
    // Mirror of the server matrix in CLAUDE.md §Autorisierung: Operators can create/edit
    // but DELETE and admin-only actions (users, audit, globals write) are Admin-only.
    useAuthStore.setState({ role: 'Operator' });
    const { result } = renderHook(() => useRole());

    expect(result.current.isOperator).toBe(true);
    expect(result.current.isAdmin).toBe(false);
    expect(result.current.canWrite).toBe(true);
    expect(result.current.canDelete).toBe(false);
    expect(result.current.canAdmin).toBe(false);
  });

  it('viewer_isReadOnly', () => {
    useAuthStore.setState({ role: 'Viewer' });
    const { result } = renderHook(() => useRole());

    expect(result.current.isViewer).toBe(true);
    expect(result.current.isAdmin).toBe(false);
    expect(result.current.isOperator).toBe(false);
    expect(result.current.canWrite).toBe(false);
    expect(result.current.canDelete).toBe(false);
    expect(result.current.canAdmin).toBe(false);
  });

  it('nullRole_defaultsToViewer', () => {
    // Pre-init / unauthenticated state: store role is null. The hook treats this as
    // Viewer so the UI renders the most-restrictive set of affordances during
    // the /auth/me probe — if we defaulted to Admin we'd flash sensitive controls.
    useAuthStore.setState({ role: null });
    const { result } = renderHook(() => useRole());

    expect(result.current.role).toBe('Viewer');
    expect(result.current.isViewer).toBe(true);
    expect(result.current.canWrite).toBe(false);
    expect(result.current.canDelete).toBe(false);
    expect(result.current.canAdmin).toBe(false);
  });

  it('roleTransition_updatesPermissions', () => {
    // Verify the hook re-evaluates when the store changes. Critical because the layout
    // re-renders on role change (e.g. user is demoted by an admin during a session).
    useAuthStore.setState({ role: 'Viewer' });
    const { result, rerender } = renderHook(() => useRole());

    expect(result.current.canWrite).toBe(false);

    useAuthStore.setState({ role: 'Admin' });
    rerender();

    expect(result.current.canWrite).toBe(true);
    expect(result.current.canDelete).toBe(true);
  });

  it('canWrite_isAlsoTrueForAdmin', () => {
    // Admin is implicitly a superset of Operator's write capability — the hook expresses
    // this by ORing the role checks in canWrite. Pin it so a refactor to a strict-equality
    // comparison would break this test, signaling the intent.
    useAuthStore.setState({ role: 'Admin' });
    const { result } = renderHook(() => useRole());

    expect(result.current.canWrite).toBe(true);
  });
});
