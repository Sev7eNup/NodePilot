import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useWorkflowContract } from '../../hooks/useWorkflowContract';

/**
 * Pin:
 * - Empty / variable expression → no fetch, contract=null
 * - GUID-shaped input → fetches /workflows/{id}/contract
 * - Name-shaped input → fetches /workflows/by-name/{name}/contract (encoded)
 * - 404 response → contract=null + isNotFound=true (no error throw)
 * - Debounce: rapid input changes only fire one request
 */

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  vi.spyOn(globalThis, 'fetch').mockImplementation((...args) => fetchMock(...args));
});

afterEach(() => { vi.restoreAllMocks(); });

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function notFoundResponse() {
  return new Response('', { status: 404, statusText: 'Not Found' });
}

function wrap(qc: QueryClient) {
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
}

describe('useWorkflowContract', () => {
  it('does not fetch when input is empty', () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderHook(() => useWorkflowContract(''), { wrapper: wrap(qc) });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('does not fetch when input is a variable expression', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result } = renderHook(() => useWorkflowContract('{{globals.WORKFLOW}}'), { wrapper: wrap(qc) });
    await new Promise((r) => setTimeout(r, 300));  // wait past debounce
    expect(fetchMock).not.toHaveBeenCalled();
    expect(result.current.isVariableExpression).toBe(true);
  });

  it('fetches by ID when input is a GUID', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({
      workflowId: '11111111-2222-3333-4444-555555555555',
      workflowName: 'Test',
      hasManualTrigger: true, hasReturnData: false, hasMultipleReturnDataNodes: false,
      inputs: [], outputs: [],
    }));

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result } = renderHook(
      () => useWorkflowContract('11111111-2222-3333-4444-555555555555'),
      { wrapper: wrap(qc) },
    );

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const url = fetchMock.mock.calls[0][0];
    expect(url).toContain('/workflows/11111111-2222-3333-4444-555555555555/contract');
    expect(url).not.toContain('by-name');

    await waitFor(() => expect(result.current.contract).toBeDefined());
    expect(result.current.contract?.workflowName).toBe('Test');
  });

  it('fetches by name when input is not a GUID, URL-encoding the name', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({
      workflowId: 'abc', workflowName: 'Daily Report',
      hasManualTrigger: true, hasReturnData: false, hasMultipleReturnDataNodes: false,
      inputs: [], outputs: [],
    }));

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderHook(() => useWorkflowContract('Daily Report'), { wrapper: wrap(qc) });

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const url = fetchMock.mock.calls[0][0];
    // Space must be URL-encoded
    expect(url).toContain('/workflows/by-name/Daily%20Report/contract');
  });

  it('returns null contract on 404 (workflow not found)', async () => {
    fetchMock.mockResolvedValueOnce(notFoundResponse());

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result } = renderHook(() => useWorkflowContract('NonExistent'), { wrapper: wrap(qc) });

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    await waitFor(() => expect(result.current.isNotFound).toBe(true));
    expect(result.current.contract).toBeNull();
    expect(result.current.error).toBeNull();
  });
});
