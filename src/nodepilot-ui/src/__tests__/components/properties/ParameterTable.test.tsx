import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ParameterTable } from '../../../components/designer/properties/ParameterTable';

/**
 * ParameterTable is a key/value editor used by the startWorkflow and forEach configs. Covers
 * the empty state, adding a blank row, editing a key or value (which emits the full map in its
 * original order), removing a row, and rendering a custom addLabel.
 */

// VariableInsertField transitively imports GlobalVariablePicker, which calls
// /api/global-variables, so fetch is stubbed instead of running an MSW server.
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

    // Both rows carry a remove button, so click the first one.
    const removes = screen.getAllByTitle('Remove');
    fireEvent.click(removes[0]);

    expect(onChange).toHaveBeenCalledWith({ bar: '2' });
  });

  it('renamingPreservesOrder', () => {
    // Object.entries returns string keys in insertion order. Renaming a key must keep that
    // order, otherwise the row jumps to a different position in the UI.
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
    // Keys keep their original order; the renamed key does not move to the end.
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
