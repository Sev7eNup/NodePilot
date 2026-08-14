import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../../api/client';
import { getOperationsGraph, quarantineWorkflow } from '../../api/operations';
import { clearLocalAuthBoundary } from '../../security/authBoundary';

afterEach(() => {
  vi.restoreAllMocks();
});

describe('operations API', () => {
  it('requests the server default 30-minute window when no window is supplied', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue({} as never);

    await getOperationsGraph();

    expect(get).toHaveBeenCalledWith('/operations/graph?windowMinutes=30');
  });

  it('passes the selectable 60-minute window through unchanged', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue({} as never);

    await getOperationsGraph(60);

    expect(get).toHaveBeenCalledWith('/operations/graph?windowMinutes=60');
  });

  it('does not start cancel-all under a replacement identity after disable completes', async () => {
    let finishDisable!: () => void;
    const disableGate = new Promise<void>((resolve) => { finishDisable = resolve; });
    const post = vi.spyOn(api, 'post').mockImplementation((path: string) => {
      if (path.endsWith('/disable')) return disableGate as never;
      return Promise.resolve({ total: 1, signalled: 1 }) as never;
    });

    const result = quarantineWorkflow('user-a-workflow');
    await vi.waitFor(() => expect(post).toHaveBeenCalledTimes(1));
    clearLocalAuthBoundary();
    finishDisable();

    await expect(result).rejects.toMatchObject({ name: 'AbortError' });
    expect(post).toHaveBeenCalledTimes(1);
  });
});
