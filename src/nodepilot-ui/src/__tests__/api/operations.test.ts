import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../../api/client';
import { getOperationsGraph } from '../../api/operations';

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
});
