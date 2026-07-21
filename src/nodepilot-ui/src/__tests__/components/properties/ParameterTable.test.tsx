import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ParameterTable } from '../../../components/designer/properties/ParameterTable';

/**
 * ParameterTable is a key/value editor used by startWorkflow + forEach configs. Pin:
 *   - Empty map renders the empty message and no rows
 *   - "+ Parameter" appends a blank row
 *   - Editing key/value emits the full updated map (preserving order)
 *   - Removing a row emits a map without that key
 *   - Custom addLabel is rendered when provided
 */

// VariableInsertField transitively imports GlobalVariablePicker which fans out to
// /api/global-variables — stub fetch so we don't need an MSW server here.
beforeEach(() => {
  vi.spyOn(globalThis, 'fetch').mockResolvedValue(
    new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }),
  );
});

function wrap(ui: React.ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

describe('ParameterTable', () => {
  it('emptyMap_rendersEmptyMessageAndNoRows', () => {
    wrap(
      <ParameterTable
        label="Parameter"
        emptyMessage="No params yet."
        parameters={{}}
        onChange={vi.fn()}
      />
    );

    expect(screen.getByText('No params yet.')).toBeInTheDocument();
    expect(screen.queryByPlaceholderText('key')).not.toBeInTheDocument();
  });

  it('rendersOneRowPerEntry', () => {
    wrap(
      <ParameterTable
        label="Parameter"
        emptyMessage="empty"
        parameters={{ foo: '1', bar: '2' }}
        onChange={vi.fn()}
      />
    );

    const keyInputs = screen.getAllByPlaceholderText('key') as HTMLInputElement[];
    expect(keyInputs).toHaveLength(2);
    expect(keyInputs.map((i) => i.value)).toEqual(['foo', 'bar']);
  });

  it('addButton_emitsMapWithEmptyKeyAppended', () => {
    const onChange = vi.fn();
    wrap(
      <ParameterTable
        label="Parameter"
        emptyMessage="empty"
        parameters={{ foo: '1' }}
        onChange={onChange}
      />
    );

    fireEvent.click(screen.getByText('+ Parameter'));

    expect(onChange).toHaveBeenCalledWith({ foo: '1', '': '' });
  });

  it('customAddLabel_isRendered', () => {
    wrap(
      <ParameterTable
        label="Headers"
        addLabel="+ Header"
        emptyMessage="empty"
        parameters={{}}
        onChange={vi.fn()}
      />
    );

    expect(screen.getByText('+ Header')).toBeInTheDocument();
    expect(screen.queryByText('+ Parameter')).not.toBeInTheDocument();
  });

  it('changingKey_emitsMapWithRenamedKey', () => {
    const onChange = vi.fn();
    wrap(
      <ParameterTable
        label="Parameter"
        emptyMessage="empty"
        parameters={{ oldKey: 'value' }}
        onChange={onChange}
      />
    );

    const keyInput = screen.getByPlaceholderText('key') as HTMLInputElement;
    fireEvent.change(keyInput, { target: { value: 'newKey' } });

    expect(onChange).toHaveBeenCalledWith({ newKey: 'value' });
  });

  it('removeRowButton_emitsMapWithoutThatKey', () => {
    const onChange = vi.fn();
    wrap(
      <ParameterTable
        label="Parameter"
        emptyMessage="empty"
        parameters={{ foo: '1', bar: '2' }}
        onChange={onChange}
      />
    );

    // Both rows have an "✕" remove button — click the first one.
    const removes = screen.getAllByTitle('Remove');
    fireEvent.click(removes[0]);

    expect(onChange).toHaveBeenCalledWith({ bar: '2' });
  });

  it('renamingPreservesOrder', () => {
    // Pin the iteration order — Object.entries on a plain object gives insertion order
    // for string keys. Without preserving it, the row would jump around in the UI.
    const onChange = vi.fn();
    wrap(
      <ParameterTable
        label="Parameter"
        emptyMessage="empty"
        parameters={{ a: '1', b: '2', c: '3' }}
        onChange={onChange}
      />
    );

    const keyInputs = screen.getAllByPlaceholderText('key') as HTMLInputElement[];
    fireEvent.change(keyInputs[1], { target: { value: 'BB' } });

    expect(onChange).toHaveBeenCalledWith({ a: '1', BB: '2', c: '3' });
    // Critical: keys are in original order, not "renamed key shoved to the end".
    expect(Object.keys(onChange.mock.calls[0][0])).toEqual(['a', 'BB', 'c']);
  });

  it('removingFirstRow_keepsRemainingOrder', () => {
    const onChange = vi.fn();
    wrap(
      <ParameterTable
        label="Parameter"
        emptyMessage="empty"
        parameters={{ a: '1', b: '2', c: '3' }}
        onChange={onChange}
      />
    );

    const removes = screen.getAllByTitle('Remove');
    fireEvent.click(removes[0]);

    expect(Object.keys(onChange.mock.calls[0][0])).toEqual(['b', 'c']);
  });
});
