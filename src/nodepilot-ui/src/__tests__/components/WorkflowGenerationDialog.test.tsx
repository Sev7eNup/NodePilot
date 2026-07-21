import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { WorkflowGenerationDialog } from '../../components/ai/WorkflowGenerationDialog';
import { aiApi, type GenerateWorkflowResponse } from '../../api/ai';

const SAMPLE_DEFINITION = JSON.stringify({
  nodes: [
    { id: 't', type: 'activity', data: { activityType: 'manualTrigger' } },
    { id: 's1', type: 'activity', data: { activityType: 'runScript' } },
    { id: 's2', type: 'activity', data: { activityType: 'runScript' } },
    { id: 's3', type: 'activity', data: { activityType: 'log' } },
  ],
  edges: [
    { id: 'e1', source: 't', target: 's1' },
    { id: 'e2', source: 's1', target: 's2' },
    { id: 'e3', source: 's2', target: 's3' },
  ],
});

const SAMPLE_RESPONSE: GenerateWorkflowResponse = {
  definitionJson: SAMPLE_DEFINITION,
  suggestedName: 'Disk Cleanup Daily',
  suggestedDescription: 'Bereinigt täglich um 6 Uhr die Logs.',
  nodeCount: 4,
  edgeCount: 3,
  retried: false,
  durationMs: 1234,
  model: 'qwen2.5-coder:32b',
};

describe('WorkflowGenerationDialog', () => {
  let generateSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    generateSpy = vi.spyOn(aiApi, 'generateWorkflow');
  });
  afterEach(() => {
    vi.restoreAllMocks();
  });

  // ---- Stage 1: Prompt -----------------------------------------------------------

  it('renders prompt stage initially with autofocused textarea', () => {
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);

    expect(screen.getByText(/Workflow per KI generieren/i)).toBeInTheDocument();
    const textarea = screen.getByLabelText('Workflow prompt') as HTMLTextAreaElement;
    expect(document.activeElement).toBe(textarea);
    expect(screen.getByRole('button', { name: /^generieren$/i })).toBeDisabled();
  });

  it('Generate button enables when prompt has content', () => {
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'do a thing' } });
    expect(screen.getByRole('button', { name: /^generieren$/i })).not.toBeDisabled();
  });

  it('Cancel calls onClose', () => {
    const onClose = vi.fn();
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={onClose} />);

    fireEvent.click(screen.getByRole('button', { name: /abbrechen/i }));
    expect(onClose).toHaveBeenCalled();
  });

  it('Escape closes the dialog', () => {
    const onClose = vi.fn();
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={onClose} />);

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
  });

  // ---- Generate → Preview transition ---------------------------------------------

  it('successful generate transitions to preview stage with suggested name + description', async () => {
    generateSpy.mockResolvedValue(SAMPLE_RESPONSE);
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));

    expect(await screen.findByText(/Generierten Workflow überprüfen/i)).toBeInTheDocument();
    expect((screen.getByLabelText('Workflow name') as HTMLInputElement).value).toBe('Disk Cleanup Daily');
    expect((screen.getByLabelText('Workflow description') as HTMLTextAreaElement).value)
      .toBe('Bereinigt täglich um 6 Uhr die Logs.');
    expect(generateSpy).toHaveBeenCalledWith({ prompt: 'cleanup' });
  });

  it('generate failure stays on prompt stage with error message', async () => {
    generateSpy.mockRejectedValue(new Error('LLM unreachable'));
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('LLM unreachable');
    expect(screen.queryByText(/Generierten Workflow überprüfen/i)).not.toBeInTheDocument();
    // Textarea should still be there
    expect(screen.getByLabelText('Workflow prompt')).toBeInTheDocument();
  });

  it('Ctrl+Enter triggers generate from prompt stage', async () => {
    generateSpy.mockResolvedValue(SAMPLE_RESPONSE);
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Enter', ctrlKey: true });

    expect(await screen.findByText(/Generierten Workflow überprüfen/i)).toBeInTheDocument();
  });

  it('shows loading indicator while generate is pending', async () => {
    let resolve!: (v: GenerateWorkflowResponse) => void;
    const pending = new Promise<GenerateWorkflowResponse>((r) => { resolve = r; });
    generateSpy.mockReturnValue(pending);

    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);
    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));

    expect(await screen.findByRole('button', { name: /generiere/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /abbrechen/i })).toBeDisabled();

    resolve(SAMPLE_RESPONSE);
    await screen.findByText(/Generierten Workflow überprüfen/i);
  });

  // ---- Stage 2: Preview ----------------------------------------------------------

  it('preview stage shows node/edge counts and activity histogram', async () => {
    generateSpy.mockResolvedValue(SAMPLE_RESPONSE);
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));

    await screen.findByText(/Generierten Workflow überprüfen/i);
    expect(screen.getByText('Nodes')).toBeInTheDocument();
    expect(screen.getByText('Edges')).toBeInTheDocument();
    // 2× runScript dominates the activity-type histogram
    expect(screen.getByText(/2× runScript/)).toBeInTheDocument();
    expect(screen.getByText(/1× manualTrigger/)).toBeInTheDocument();
    expect(screen.getByText(/1× log/)).toBeInTheDocument();
  });

  it('retried flag surfaces a warning indicator', async () => {
    generateSpy.mockResolvedValue({ ...SAMPLE_RESPONSE, retried: true });
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));

    expect(await screen.findByText(/retried/i)).toBeInTheDocument();
  });

  it('JSON preview is collapsed by default and toggles on click', async () => {
    generateSpy.mockResolvedValue(SAMPLE_RESPONSE);
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));
    await screen.findByText(/Generierten Workflow überprüfen/i);

    expect(screen.queryByTestId('workflow-definition-json')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Definition JSON/i }));
    expect(screen.getByTestId('workflow-definition-json')).toBeInTheDocument();
    expect(screen.getByTestId('workflow-definition-json').textContent).toContain('manualTrigger');
  });

  // ---- Create -------------------------------------------------------------------

  it('Erstellen calls onCreate with edited name + description + raw definitionJson', async () => {
    generateSpy.mockResolvedValue(SAMPLE_RESPONSE);
    const onCreate = vi.fn().mockResolvedValue(undefined);
    render(<WorkflowGenerationDialog onCreate={onCreate} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));
    await screen.findByText(/Generierten Workflow überprüfen/i);

    fireEvent.change(screen.getByLabelText('Workflow name'), { target: { value: 'My Edited Name' } });
    fireEvent.change(screen.getByLabelText('Workflow description'), { target: { value: 'short desc' } });
    fireEvent.click(screen.getByRole('button', { name: /erstellen & öffnen/i }));

    await waitFor(() => expect(onCreate).toHaveBeenCalledWith({
      name: 'My Edited Name',
      description: 'short desc',
      definitionJson: SAMPLE_DEFINITION,
    }));
  });

  it('Erstellen disabled when name is empty', async () => {
    generateSpy.mockResolvedValue(SAMPLE_RESPONSE);
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));
    await screen.findByText(/Generierten Workflow überprüfen/i);

    fireEvent.change(screen.getByLabelText('Workflow name'), { target: { value: '   ' } });
    expect(screen.getByRole('button', { name: /erstellen & öffnen/i })).toBeDisabled();
  });

  it('create failure surfaces error and stays on preview stage', async () => {
    generateSpy.mockResolvedValue(SAMPLE_RESPONSE);
    const onCreate = vi.fn().mockRejectedValue(new Error('Conflict: name in use'));
    render(<WorkflowGenerationDialog onCreate={onCreate} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));
    await screen.findByText(/Generierten Workflow überprüfen/i);

    fireEvent.click(screen.getByRole('button', { name: /erstellen & öffnen/i }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Conflict: name in use');
    // Stage stays in preview
    expect(screen.getByText(/Generierten Workflow überprüfen/i)).toBeInTheDocument();
  });

  it('Zurück returns from preview to prompt stage and clears error', async () => {
    generateSpy.mockResolvedValue(SAMPLE_RESPONSE);
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));
    await screen.findByText(/Generierten Workflow überprüfen/i);

    fireEvent.click(screen.getByRole('button', { name: /^zurück$/i }));
    expect(screen.getByText(/Workflow per KI generieren/i)).toBeInTheDocument();
    // Original prompt is still in the textarea
    expect((screen.getByLabelText('Workflow prompt') as HTMLTextAreaElement).value).toBe('cleanup');
  });

  it('Verwerfen on preview stage closes the dialog', async () => {
    generateSpy.mockResolvedValue(SAMPLE_RESPONSE);
    const onClose = vi.fn();
    render(<WorkflowGenerationDialog onCreate={async () => {}} onClose={onClose} />);

    fireEvent.change(screen.getByLabelText('Workflow prompt'), { target: { value: 'cleanup' } });
    fireEvent.click(screen.getByRole('button', { name: /^generieren$/i }));
    await screen.findByText(/Generierten Workflow überprüfen/i);

    fireEvent.click(screen.getByRole('button', { name: /^verwerfen$/i }));
    expect(onClose).toHaveBeenCalled();
  });
});
