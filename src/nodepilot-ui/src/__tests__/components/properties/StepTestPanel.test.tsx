import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StepTestPanel } from '../../../components/designer/properties/StepTestPanel';

/**
 * Covers StepTestPanel: rendering and switching all four mode tabs, "Run test" sending the
 * configOverride and the selected mocks, "Pick a run" keeping the Run button disabled until a
 * run is chosen, the manual mocks editor parsing key=value lines while skipping comments and
 * blank lines, and last-run mode forwarding the context variables without globals.
 */

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  vi.spyOn(globalThis, 'fetch').mockImplementation((...args) => fetchMock(...args));
});

afterEach(() => {
  vi.restoreAllMocks();
});

function wrap(ui: React.ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('StepTestPanel', () => {
  it('standardMode_rendersOneContextAwareTestAction', () => {
    fetchMock.mockResolvedValue(jsonResponse({ variables: [], executedAt: null, status: null }));
    wrap(
      <StepTestPanel
        workflowId="wf1"
        stepId="step1"
        liveConfig={{}}
        canRun={true}
        expertMode={false}
      />,
    );

    expect(screen.queryByRole('button', { name: /^Empty$/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Manual mocks$/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Test activity/i })).toBeInTheDocument();
  });

  it('renders all four mode tabs', () => {
    wrap(
      <StepTestPanel
        workflowId="wf1"
        stepId="step1"
        liveConfig={{ script: 'Get-Process' }}
        canRun={true}
      />,
    );
    // The Run button repeats the active mode in its label, so a loose /Empty/ would match it
    // too. The anchored patterns below match the mode pill exactly.
    expect(screen.getByRole('button', { name: /^Empty$/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Last run$/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Pick a run$/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Manual mocks$/ })).toBeInTheDocument();
  });

  it('hides Run button when canRun is false', () => {
    wrap(
      <StepTestPanel
        workflowId="wf1"
        stepId="step1"
        liveConfig={{}}
        canRun={false}
      />,
    );
    expect(screen.queryByRole('button', { name: /Run test/i })).not.toBeInTheDocument();
  });

  it('Empty mode test sends configOverride + no mockVariables', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({
      success: true, output: 'OK', errorOutput: null,
      outputParameters: {}, durationMs: 12, errorMessage: null,
    }));

    wrap(
      <StepTestPanel
        workflowId="wf1"
        stepId="step1"
        liveConfig={{ script: 'Get-Process', timeoutSeconds: 30 }}
        canRun={true}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Run test/i }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const call = fetchMock.mock.calls[0];
    expect(call[0]).toContain('/workflows/wf1/steps/step1/test');
    const body = JSON.parse((call[1] as RequestInit).body as string);
    expect(body.configOverride).toEqual({ script: 'Get-Process', timeoutSeconds: 30 });
    expect(body.mockVariables).toBeUndefined();

    expect(await screen.findByText(/Succeeded/i)).toBeInTheDocument();
  });

  it('Manual mocks editor parses key=value lines and ignores comments', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({
      success: true, output: null, errorOutput: null,
      outputParameters: {}, durationMs: 1, errorMessage: null,
    }));

    wrap(
      <StepTestPanel
        workflowId="wf1"
        stepId="step1"
        liveConfig={{}}
        canRun={true}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Manual mocks$/ }));
    const textarea = screen.getByPlaceholderText(/stepName.output=value/i);
    fireEvent.change(textarea, {
      target: {
        value: '# top comment\nfoo.output=bar\n\nbaz.param.x=42\n  # inline\n',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: /Run test/i }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const body = JSON.parse((fetchMock.mock.calls[0][1] as RequestInit).body as string);
    expect(body.mockVariables).toEqual({
      'foo.output': 'bar',
      'baz.param.x': '42',
    });
  });

  it('Last-run mode pulls context variables (excluding globals) into mockVariables', async () => {
    fetchMock.mockImplementation((url: string) => {
      if (url.includes('/test-context')) {
        return Promise.resolve(jsonResponse({
          executionId: 'exec1',
          executedAt: '2026-05-08T10:00:00Z',
          status: 'Succeeded',
          variables: [
            { key: 'a.output', origin: 'a', source: 'output', value: '7' },
            { key: 'a.success', origin: 'a', source: 'success', value: 'true' },
            { key: 'b.output', origin: 'b', source: 'output', value: null },
            { key: 'globals.ENV', origin: 'globals', source: 'global', value: 'stg' },
          ],
        }));
      }
      // Response for the /test call.
      return Promise.resolve(jsonResponse({
        success: true, output: 'OK', errorOutput: null,
        outputParameters: {}, durationMs: 5, errorMessage: null,
      }));
    });

    wrap(
      <StepTestPanel
        workflowId="wf1"
        stepId="step1"
        liveConfig={{}}
        canRun={true}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Last run$/ }));

    // Wait for the context fetch and render so the assertions see the latest `context` state.
    // The variable list shows the keys, so wait until "a.output" appears in the DOM.
    await screen.findByText(/a\.output/);

    fireEvent.click(screen.getByRole('button', { name: /Run test/i }));

    await waitFor(() => {
      const testCall = fetchMock.mock.calls.find(
        (c) => typeof c[0] === 'string' && c[0].endsWith('/test'),
      );
      expect(testCall).toBeDefined();
    });

    const testCall = fetchMock.mock.calls.find(
      (c) => typeof c[0] === 'string' && c[0].endsWith('/test'),
    )!;
    const body = JSON.parse((testCall[1] as RequestInit).body as string);
    // a.output and a.success have non-null values, so they are forwarded.
    expect(body.mockVariables).toEqual({
      'a.output': '7',
      'a.success': 'true',
    });
    // globals.* is never passed, and b.output is null so it is excluded too.
    expect(body.mockVariables['globals.ENV']).toBeUndefined();
    expect(body.mockVariables['b.output']).toBeUndefined();
  });

  it('Pick-a-run mode disables Run until a run is chosen', async () => {
    fetchMock.mockImplementation((url: string) => {
      if (url.includes('/test-context/runs')) {
        // An empty list keeps the auto-pick effect from enabling the button.
        return Promise.resolve(jsonResponse([]));
      }
      return Promise.resolve(jsonResponse({}));
    });

    wrap(
      <StepTestPanel
        workflowId="wf1"
        stepId="step1"
        liveConfig={{}}
        canRun={true}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /^Pick a run$/ }));

    // With an empty runs list no run is selected, so the button stays disabled.
    const runButton = screen.getByRole('button', { name: /Run test/i });
    expect(runButton).toBeDisabled();
  });
});
