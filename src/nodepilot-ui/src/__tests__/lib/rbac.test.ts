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
    // Mirrors the server role matrix: Operators can create and edit, while delete and
    // admin-only actions (users, audit, globals write) need Admin.
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
    // Before init and while unauthenticated the store role is null. The hook treats that as
    // Viewer so the UI shows the most restrictive controls during the /auth/me probe instead
    // of briefly exposing sensitive ones.
    useAuthStore.setState({ role: null });
    const { result } = renderHook(() => useRole());

    expect(result.current.role).toBe('Viewer');
    expect(result.current.isViewer).toBe(true);
    expect(result.current.canWrite).toBe(false);
    expect(result.current.canDelete).toBe(false);
    expect(result.current.canAdmin).toBe(false);
  });

  it('roleTransition_updatesPermissions', () => {
    // The hook re-evaluates when the store changes, so the layout follows a role change
    // that happens during a session.
    useAuthStore.setState({ role: 'Viewer' });
    const { result, rerender } = renderHook(() => useRole());

    expect(result.current.canWrite).toBe(false);

    useAuthStore.setState({ role: 'Admin' });
    rerender();

    expect(result.current.canWrite).toBe(true);
    expect(result.current.canDelete).toBe(true);
  });

  it('canWrite_isAlsoTrueForAdmin', () => {
    // Admin includes Operator's write capability, so canWrite ors both role checks. Pinned
    // here so narrowing it to a strict equality check fails the test.
    useAuthStore.setState({ role: 'Admin' });
    const { result } = renderHook(() => useRole());

    expect(result.current.canWrite).toBe(true);
  });
});
