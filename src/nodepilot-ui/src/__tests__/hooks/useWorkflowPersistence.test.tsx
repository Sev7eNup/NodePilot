import * as React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

// Controls the react-router `useBlocker` return value per test. The hook owns the discard
// confirm for in-app navigation, and WorkflowEditorPage.test.tsx pins useBlocker to
// 'unblocked', so the blocked, confirm and proceed/reset branch is covered here.
const routerMock = vi.hoisted(() => ({
  blocker: { state: 'unblocked' as string, proceed: vi.fn(), reset: vi.fn() },
}));
vi.mock('react-router', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router')>();
  return { ...actual, useBlocker: () => routerMock.blocker };
});

// The store-driven confirm replaces the native confirm() and resolves true by default.
vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../stores/confirmStore';

vi.mock('../../api/client', () => ({
  api: { put: vi.fn(), post: vi.fn() },
}));
import { api } from '../../api/client';

import { useWorkflowPersistence } from '../../hooks/useWorkflowPersistence';

function makeWrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
}

function renderPersistence() {
  return renderHook(
    () => useWorkflowPersistence({ workflowId: 'wf-1', workflow: undefined, nodes: [], edges: [] }),
    { wrapper: makeWrapper() },
  );
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

function editableNode(label: string) {
  return { id: 'step-1', position: { x: 0, y: 0 }, data: { label } };
}

describe('useWorkflowPersistence — revision-safe requests', () => {
  beforeEach(() => {
    routerMock.blocker = { state: 'unblocked', proceed: vi.fn(), reset: vi.fn() };
    vi.mocked(api.put).mockReset();
    vi.mocked(api.post).mockReset();
  });

  it('saves an immutable snapshot, then follows up when the canvas changed in flight', async () => {
    const first = deferred<unknown>();
    const second = deferred<unknown>();
    vi.mocked(api.put)
      .mockImplementationOnce(() => first.promise)
      .mockImplementationOnce(() => second.promise);
    const wrapper = makeWrapper();
    const { result, rerender } = renderHook(
      ({ label }) => useWorkflowPersistence({
        workflowId: 'wf-1', workflow: undefined, nodes: [editableNode(label)], edges: [],
      }),
      { wrapper, initialProps: { label: 'revision-a' } },
    );

    act(() => { expect(result.current.syncFromServer('Initial name')).toBe(true); });
    act(() => { result.current.markDirty(); result.current.save(); });
    await waitFor(() => expect(api.put).toHaveBeenCalledTimes(1));
    expect(vi.mocked(api.put).mock.calls[0][1]).toMatchObject({
      definitionJson: expect.stringContaining('revision-a'),
    });

    rerender({ label: 'revision-b' });
    act(() => result.current.markDirty());
    await act(async () => first.resolve({}));

    await waitFor(() => expect(api.put).toHaveBeenCalledTimes(2));
    expect(vi.mocked(api.put).mock.calls[1][1]).toMatchObject({
      definitionJson: expect.stringContaining('revision-b'),
    });
    // The stale success must neither mark the newer edit clean nor let a refetch replace it.
    expect(result.current.isDirty).toBe(true);
    expect(result.current.syncFromServer('Stale server name')).toBe(false);

    await act(async () => second.resolve({}));
    await waitFor(() => expect(result.current.isDirty).toBe(false));
  });

  it('does not clear or overwrite a newer revision when publish completes', async () => {
    const publish = deferred<unknown>();
    vi.mocked(api.post).mockImplementationOnce(() => publish.promise);
    const wrapper = makeWrapper();
    const { result, rerender } = renderHook(
      ({ label }) => useWorkflowPersistence({
        workflowId: 'wf-1', workflow: undefined, nodes: [editableNode(label)], edges: [],
      }),
      { wrapper, initialProps: { label: 'published-snapshot' } },
    );

    act(() => { expect(result.current.syncFromServer('Initial name')).toBe(true); });
    act(() => { result.current.rename('Published name'); result.current.publish(); });
    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1));
    rerender({ label: 'newer-local-revision' });
    act(() => result.current.rename('Newer local name'));
    await act(async () => publish.resolve({}));

    await waitFor(() => expect(result.current.isDirty).toBe(true));
    expect(result.current.syncFromServer('Stale server name')).toBe(false);
    expect(result.current.name).toBe('Newer local name');
    expect(vi.mocked(api.post).mock.calls[0][1]).toMatchObject({
      name: 'Published name',
      definitionJson: expect.stringContaining('published-snapshot'),
    });
  });

  it('applies one async graph token at most once and exposes it to a same-tick Save', async () => {
    vi.mocked(api.put).mockResolvedValue({});
    const { result } = renderPersistence();
    act(() => { expect(result.current.syncFromServer('Initial')).toBe(true); });
    const token = result.current.beginAsyncGraphEdit();

    act(() => {
      expect(result.current.applyAsyncGraphEdit(token!, [editableNode('applied-layout')], [])).toBe(true);
      expect(result.current.applyAsyncGraphEdit(token!, [editableNode('duplicate-layout')], [])).toBe(false);
      result.current.save();
    });

    await waitFor(() => expect(api.put).toHaveBeenCalledTimes(1));
    expect(vi.mocked(api.put).mock.calls[0][1]).toMatchObject({
      definitionJson: expect.stringContaining('applied-layout'),
    });
    expect(vi.mocked(api.put).mock.calls[0][1]).not.toMatchObject({
      definitionJson: expect.stringContaining('duplicate-layout'),
    });
    await waitFor(() => expect(result.current.isDirty).toBe(false));
  });

  it('applies an async graph result atomically and rejects its token after a workflow switch', () => {
    const wrapper = makeWrapper();
    const { result, rerender } = renderHook(
      ({ workflowId }) => useWorkflowPersistence({
        workflowId, workflow: undefined, nodes: [editableNode('initial')], edges: [],
      }),
      { wrapper, initialProps: { workflowId: 'wf-1' } },
    );
    act(() => { expect(result.current.syncFromServer('First')).toBe(true); });
    const staleToken = result.current.beginAsyncGraphEdit();

    rerender({ workflowId: 'wf-2' });
    act(() => { expect(result.current.syncFromServer('Second')).toBe(true); });
    act(() => {
      expect(result.current.applyAsyncGraphEdit(staleToken!, [editableNode('stale-layout')], [])).toBe(false);
    });
    expect(result.current.isDirty).toBe(false);
  });

  it('invalidates a pending async graph token as soon as Publish is queued', async () => {
    const publish = deferred<unknown>();
    vi.mocked(api.post).mockImplementationOnce(() => publish.promise);
    const { result } = renderPersistence();
    act(() => { expect(result.current.syncFromServer('Initial')).toBe(true); });
    const token = result.current.beginAsyncGraphEdit();

    act(() => result.current.publish());
    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1));
    act(() => {
      expect(result.current.applyAsyncGraphEdit(token!, [editableNode('late-layout')], [])).toBe(false);
    });

    await act(async () => publish.resolve({}));
  });
});

describe('useWorkflowPersistence — useBlocker discard guard', () => {
  beforeEach(() => {
    // Fresh spies and a blocked navigation per test; the effect fires on mount.
    routerMock.blocker = { state: 'blocked', proceed: vi.fn(), reset: vi.fn() };
    vi.mocked(confirmDialog).mockClear();
  });
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('confirming the discard proceeds with the blocked navigation', async () => {
    renderPersistence();
    // confirmDialog resolves asynchronously, so the blocker stays 'blocked' until proceed().
    await waitFor(() => expect(routerMock.blocker.proceed).toHaveBeenCalledTimes(1));
    expect(confirmDialog).toHaveBeenCalledTimes(1);
    expect(routerMock.blocker.reset).not.toHaveBeenCalled();
  });

  it('cancelling the discard resets the blocker and stays put', async () => {
    vi.mocked(confirmDialog).mockResolvedValueOnce(false);
    renderPersistence();
    await waitFor(() => expect(routerMock.blocker.reset).toHaveBeenCalledTimes(1));
    expect(confirmDialog).toHaveBeenCalledTimes(1);
    expect(routerMock.blocker.proceed).not.toHaveBeenCalled();
  });
});
