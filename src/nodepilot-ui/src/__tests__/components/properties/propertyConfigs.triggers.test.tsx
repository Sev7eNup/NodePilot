import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ManualTriggerConfig } from '../../../components/designer/properties/triggers/ManualTriggerConfig';
import { FileWatcherTriggerConfig } from '../../../components/designer/properties/triggers/FileWatcherTriggerConfig';
import { DatabaseTriggerConfig } from '../../../components/designer/properties/triggers/DatabaseTriggerConfig';
import { EventLogTriggerConfig } from '../../../components/designer/properties/triggers/EventLogTriggerConfig';

beforeEach(() => {
  vi.spyOn(globalThis, 'fetch').mockResolvedValue(
    new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }),
  );
});

function wrap(ui: React.ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

describe('ManualTriggerConfig', () => {
  it('emptyConfig_showsHint_andNoParameterRows', () => {
    wrap(<ManualTriggerConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/No parameters/i)).toBeInTheDocument();
  });

  it('addParameter_appendsParamWithStringDefault', () => {
    // CLAUDE.md call-out: manualTrigger params must default to type:"string" + string defaults.
    // Pin that contract so a refactor that changes the default type breaks loudly.
    const onUpdate = vi.fn();
    wrap(<ManualTriggerConfig config={{ parameters: [] }} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.click(screen.getByRole('button', { name: 'Add' }));

    expect(onUpdate).toHaveBeenCalledWith({
      parameters: [{ name: '', type: 'string', required: false, default: '' }],
    });
  });

  it('parameterName_stripsWhitespace', () => {
    // Workflow templating uses {{step.param.name}} regex which doesn't match spaces.
    // The UI normalises whitespace to underscore on input so the user can't save a
    // parameter that templates would silently fail to resolve later.
    const onUpdate = vi.fn();
    const cfg = { parameters: [{ name: '', type: 'string', required: false, default: '' }] };
    wrap(<ManualTriggerConfig config={cfg} onUpdate={onUpdate} upstreamVars={[]} />);

    const nameInput = screen.getByPlaceholderText(/serverName/i) as HTMLInputElement;
    fireEvent.change(nameInput, { target: { value: 'server name' } });

    expect(onUpdate).toHaveBeenCalledWith({
      parameters: [expect.objectContaining({ name: 'server_name' })],
    });
  });

  it('parameterTypeSelect_emitsPatch', () => {
    const onUpdate = vi.fn();
    const cfg = { parameters: [{ name: 'count', type: 'string', required: false, default: '' }] };
    wrap(<ManualTriggerConfig config={cfg} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.change(screen.getByDisplayValue('String'), { target: { value: 'number' } });

    expect(onUpdate).toHaveBeenCalledWith({
      parameters: [expect.objectContaining({ type: 'number' })],
    });
  });

  it('removeParameter_emitsPatchWithoutThatRow', () => {
    const onUpdate = vi.fn();
    const cfg = {
      parameters: [
        { name: 'a', type: 'string', required: false, default: '' },
        { name: 'b', type: 'string', required: false, default: '' },
      ],
    };
    wrap(<ManualTriggerConfig config={cfg} onUpdate={onUpdate} upstreamVars={[]} />);

    // Both remove buttons render with title="Remove parameter" — the first one removes "a".
    const removeButtons = screen.getAllByTitle('Remove parameter');
    fireEvent.click(removeButtons[0]);

    expect(onUpdate).toHaveBeenCalledWith({
      parameters: [expect.objectContaining({ name: 'b' })],
    });
  });

  it('previewLine_appearsOnceParamHasName', () => {
    // Live preview of the {{varName.param.X}} access expression is the whole point of
    // the inline-template hint — it stops users from typing wrong template paths.
    const cfg = { parameters: [{ name: 'serverName', type: 'string', required: false, default: '' }] };
    wrap(<ManualTriggerConfig config={cfg} onUpdate={vi.fn()} upstreamVars={[]} />);

    expect(screen.getByText(/varName\.param\.serverName/i)).toBeInTheDocument();
  });
});

describe('FileWatcherTriggerConfig', () => {
  it('rendersAllFields_includingSubdirectoriesToggleOff', () => {
    wrap(<FileWatcherTriggerConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText('Directory')).toBeInTheDocument();
    expect(screen.getByText('File Filter')).toBeInTheDocument();
    expect(screen.getByText('Watch Type')).toBeInTheDocument();
    expect(screen.getByLabelText(/Include subdirectories/i)).not.toBeChecked();
  });

  it('watchTypeChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<FileWatcherTriggerConfig config={{ watchType: 'created' }} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.change(screen.getByDisplayValue('File Created'), { target: { value: 'any' } });
    expect(onUpdate).toHaveBeenCalledWith({ watchType: 'any' });
  });

  it('filterDefaults_toStarStar', () => {
    wrap(<FileWatcherTriggerConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('*.*')).toBeInTheDocument();
  });

  it('includeSubdirectories_togglesPatch', () => {
    const onUpdate = vi.fn();
    wrap(<FileWatcherTriggerConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.click(screen.getByLabelText(/Include subdirectories/i));
    expect(onUpdate).toHaveBeenCalledWith({ includeSubdirectories: true });
  });
});

describe('DatabaseTriggerConfig', () => {
  it('connectionRefDefault_isEmpty', () => {
    wrap(<DatabaseTriggerConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByPlaceholderText(/Trigger:Database:Connections/i)).toHaveValue('');
  });

  it('pollingIntervalDefault_is60', () => {
    wrap(<DatabaseTriggerConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('60')).toBeInTheDocument();
  });

  it('pollingIntervalChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<DatabaseTriggerConfig config={{ pollingIntervalSeconds: 60 }} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.change(screen.getByDisplayValue('60'), { target: { value: '30' } });
    expect(onUpdate).toHaveBeenCalledWith({ pollingIntervalSeconds: 30 });
  });
});

describe('EventLogTriggerConfig', () => {
  it('logNameDefault_isApplication', () => {
    wrap(<EventLogTriggerConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('Application')).toBeInTheDocument();
  });

  it('entryTypeChange_undefinedForAnyLevel', () => {
    // The "Any level" option emits `undefined` so the saved JSON drops the field
    // entirely — the backend uses presence-of-field as the filter signal.
    const onUpdate = vi.fn();
    wrap(<EventLogTriggerConfig config={{ entryType: 'Error' }} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.change(screen.getByDisplayValue('Error'), { target: { value: '' } });
    expect(onUpdate).toHaveBeenCalledWith({ entryType: undefined });
  });

  it('eventIdInput_emitsParsedNumber', () => {
    const onUpdate = vi.fn();
    wrap(<EventLogTriggerConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    const eventIdInput = screen.getByPlaceholderText(/4625/i) as HTMLInputElement;
    fireEvent.change(eventIdInput, { target: { value: '1000' } });

    expect(onUpdate).toHaveBeenCalledWith({ eventId: 1000 });
  });

  it('eventIdInput_emptyString_clearsField', () => {
    const onUpdate = vi.fn();
    wrap(<EventLogTriggerConfig config={{ eventId: 1000 }} onUpdate={onUpdate} upstreamVars={[]} />);

    const eventIdInput = screen.getByDisplayValue('1000') as HTMLInputElement;
    fireEvent.change(eventIdInput, { target: { value: '' } });

    expect(onUpdate).toHaveBeenCalledWith({ eventId: undefined });
  });

  it('lookbackDefault_is5Minutes', () => {
    wrap(<EventLogTriggerConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('5')).toBeInTheDocument();
  });
});
