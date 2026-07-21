import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { resolveWorkflowRef } from '../../lib/resolveWorkflowRef';

const server = setupServer();

beforeEach(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => { server.resetHandlers(); server.close(); });

const mockWorkflow = {
  id: '11111111-1111-1111-1111-111111111111',
  name: 'Daily-Report',
  description: 'Some description',
  definitionJson: '{"nodes":[],"edges":[]}',
  version: 1,
  isEnabled: true,
  createdAt: '2026-04-01T00:00:00Z',
  updatedAt: '2026-04-01T00:00:00Z',
  createdBy: 'admin',
  updatedBy: 'admin',
};

describe('resolveWorkflowRef', () => {
  it('uses /api/workflows/{id} for GUID inputs', async () => {
    server.use(
      http.get('/api/workflows/11111111-1111-1111-1111-111111111111', () =>
        HttpResponse.json(mockWorkflow),
      ),
    );
    const result = await resolveWorkflowRef('11111111-1111-1111-1111-111111111111');
    expect(result).not.toBeNull();
    expect(result!.id).toBe('11111111-1111-1111-1111-111111111111');
  });

  it('uses /api/workflows/by-name/{name} for non-GUID inputs', async () => {
    server.use(
      http.get('/api/workflows/by-name/Daily-Report', () => HttpResponse.json(mockWorkflow)),
    );
    const result = await resolveWorkflowRef('Daily-Report');
    expect(result).not.toBeNull();
    expect(result!.name).toBe('Daily-Report');
  });

  it('URL-encodes names with special characters', async () => {
    let receivedPath = '';
    server.use(
      http.get('/api/workflows/by-name/:name', ({ request }) => {
        receivedPath = new URL(request.url).pathname;
        return HttpResponse.json(mockWorkflow);
      }),
    );
    await resolveWorkflowRef('Daily Report');
    expect(receivedPath).toContain('Daily%20Report');
  });

  it('returns null on 404 instead of throwing', async () => {
    server.use(
      http.get('/api/workflows/by-name/:name', () => new HttpResponse(null, { status: 404 })),
    );
    const result = await resolveWorkflowRef('not-found');
    expect(result).toBeNull();
  });

  it('throws on 500-class server errors', async () => {
    server.use(
      http.get('/api/workflows/by-name/:name', () => new HttpResponse(null, { status: 500 })),
    );
    await expect(resolveWorkflowRef('boom')).rejects.toThrow();
  });

  it('returns null for empty input without hitting the network', async () => {
    // No handler registered — if we call fetch we'd get an error
    server.use();
    expect(await resolveWorkflowRef('')).toBeNull();
    expect(await resolveWorkflowRef('   ')).toBeNull();
  });

  it('returns null for templated refs without hitting the network', async () => {
    server.use();
    expect(await resolveWorkflowRef('{{globals.WORKFLOW_NAME}}')).toBeNull();
  });

  it('handles uppercase GUIDs', async () => {
    const upperGuid = '22222222-2222-2222-2222-222222222222';
    let usedPath = '';
    server.use(
      http.get('/api/workflows/22222222-2222-2222-2222-222222222222', ({ request }) => {
        usedPath = new URL(request.url).pathname;
        return HttpResponse.json({ ...mockWorkflow, id: upperGuid });
      }),
    );
    await resolveWorkflowRef(upperGuid.toUpperCase());
    expect(usedPath).not.toContain('by-name');
  });
});
