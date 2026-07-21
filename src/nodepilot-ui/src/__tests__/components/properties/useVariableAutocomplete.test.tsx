import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useRef } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useVariableAutocomplete } from '../../../components/designer/properties/useVariableAutocomplete';
import type { UpstreamVariable } from '../../../lib/upstreamVariables';

/**
 * useVariableAutocomplete is the brain behind the `{{`-driven dropdown. We pin the
 * cursor-aware open/close logic, the filter behavior, the keyboard handling, and the
 * pick splice — these are easy to break in subtle ways.
 */

beforeEach(() => {
  // The hook fetches /global-variables; stub fetch so React Query doesn't 404 in jsdom.
  vi.spyOn(globalThis, 'fetch').mockResolvedValue(
    new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }),
  );
});

function setup({
  initialValue = '',
  upstreamVars = [],
  enabled = true,
  cursor,
}: {
  initialValue?: string;
  upstreamVars?: UpstreamVariable[];
  enabled?: boolean;
  cursor?: number;
} = {}) {
  let value = initialValue;
  const onChange = vi.fn((v: string) => { value = v; });

  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );

  const { result, rerender } = renderHook(
    () => {
      const inputRef = useRef<HTMLInputElement>(null);
      // Provide a stub so the hook's selectionStart/setSelectionRange paths don't crash.
      if (!inputRef.current) {
        const stub = {
          selectionStart: cursor ?? value.length,
          selectionEnd: cursor ?? value.length,
          focus: () => {},
          setSelectionRange: () => {},
        } as unknown as HTMLInputElement;
        (inputRef as unknown as { current: HTMLInputElement }).current = stub;
      }
      return {
        api: useVariableAutocomplete({
          inputRef,
          value,
          onChange,
          upstreamVars,
          enabled,
        }),
        getValue: () => value,
      };
    },
    { wrapper },
  );

  return { result, rerender, onChange, getValue: () => value };
}

const sampleUpstream: UpstreamVariable[] = [
  { stepId: 'a', label: 'Step A', variable: 'a', expression: '{{a.output}}', type: 'string' },
  { stepId: 'b', label: 'Step B', variable: 'b', expression: '{{b.output}}', type: 'string' },
];

describe('useVariableAutocomplete', () => {
  it('initiallyClosed', () => {
    const { result } = setup({ upstreamVars: sampleUpstream });
    expect(result.current.api.open).toBe(false);
    expect(result.current.api.filtered).toEqual([]);
  });

  it('refresh_opensWhenCursorIsAfterUnclosedDoubleBrace', () => {
    const value = '{{a';
    const { result } = setup({ initialValue: value, upstreamVars: sampleUpstream, cursor: value.length });

    act(() => result.current.api.refresh());

    expect(result.current.api.open).toBe(true);
    // Filter "a" matches both upstream vars (their expressions contain 'a')
    expect(result.current.api.filtered.length).toBeGreaterThan(0);
  });

  it('refresh_doesNotOpen_whenLastBraceIsClosed', () => {
    const value = '{{a.output}}';
    const { result } = setup({ initialValue: value, upstreamVars: sampleUpstream, cursor: value.length });

    act(() => result.current.api.refresh());

    expect(result.current.api.open).toBe(false);
  });

  it('refresh_doesNotOpen_whenPartialContainsInvalidChar', () => {
    // The hook only opens when the partial after `{{` matches /^[\w.-]*$/ — a space or
    // bracket should close it.
    const value = '{{a b';
    const { result } = setup({ initialValue: value, upstreamVars: sampleUpstream, cursor: value.length });

    act(() => result.current.api.refresh());

    expect(result.current.api.open).toBe(false);
  });

  it('refresh_respectsEnabledFlag', () => {
    const { result } = setup({ initialValue: '{{', upstreamVars: sampleUpstream, enabled: false });

    act(() => result.current.api.refresh());

    expect(result.current.api.open).toBe(false);
  });

  it('filtersUpstreamSuggestions_byPartialMatch', () => {
    const value = '{{a';
    const { result } = setup({ initialValue: value, upstreamVars: sampleUpstream, cursor: value.length });

    act(() => result.current.api.refresh());

    // Filter is "a" — both 'a.output' and labels containing 'a' may match. Pin that the
    // filtered list is non-empty and contains the 'a.output' suggestion.
    const expressions = result.current.api.filtered.map((s) => s.expression);
    expect(expressions).toContain('{{a.output}}');
  });

  it('pick_replacesPartialWithChosenExpression', () => {
    const value = 'before {{a after';
    const cursor = 'before {{a'.length;
    const { result, getValue } = setup({ initialValue: value, upstreamVars: sampleUpstream, cursor });

    act(() => result.current.api.refresh());
    expect(result.current.api.open).toBe(true);

    act(() => result.current.api.pick('{{a.output}}'));

    // Splices in `{{a.output}}` from openIdx through cursor — the trailing " after" stays.
    expect(getValue()).toBe('before {{a.output}} after');
  });

  it('handleKeyDown_arrowDown_movesSelectedIdx', () => {
    const value = '{{';
    const { result } = setup({ initialValue: value, upstreamVars: sampleUpstream, cursor: value.length });

    act(() => result.current.api.refresh());

    const initial = result.current.api.selectedIdx;
    act(() =>
      result.current.api.handleKeyDown({
        key: 'ArrowDown',
        preventDefault: vi.fn(),
      } as unknown as React.KeyboardEvent<HTMLInputElement>)
    );

    expect(result.current.api.selectedIdx).toBe(initial + 1);
  });

  it('handleKeyDown_arrowUp_clampsAtZero', () => {
    const value = '{{';
    const { result } = setup({ initialValue: value, upstreamVars: sampleUpstream, cursor: value.length });
    act(() => result.current.api.refresh());

    act(() =>
      result.current.api.handleKeyDown({
        key: 'ArrowUp',
        preventDefault: vi.fn(),
      } as unknown as React.KeyboardEvent<HTMLInputElement>)
    );

    expect(result.current.api.selectedIdx).toBe(0);
  });

  it('handleKeyDown_escape_closesDropdown', () => {
    const value = '{{';
    const { result } = setup({ initialValue: value, upstreamVars: sampleUpstream, cursor: value.length });
    act(() => result.current.api.refresh());
    expect(result.current.api.open).toBe(true);

    act(() =>
      result.current.api.handleKeyDown({
        key: 'Escape',
        preventDefault: vi.fn(),
      } as unknown as React.KeyboardEvent<HTMLInputElement>)
    );

    expect(result.current.api.open).toBe(false);
  });

  it('handleKeyDown_enter_picksHighlightedExpression', () => {
    const value = '{{';
    const cursor = value.length;
    const { result, getValue } = setup({ initialValue: value, upstreamVars: sampleUpstream, cursor });
    act(() => result.current.api.refresh());

    act(() =>
      result.current.api.handleKeyDown({
        key: 'Enter',
        preventDefault: vi.fn(),
      } as unknown as React.KeyboardEvent<HTMLInputElement>)
    );

    // The first suggestion (selectedIdx=0) is picked → the partial "{{" is replaced.
    expect(getValue()).not.toBe(value);
    expect(getValue()).toMatch(/^\{\{/);
  });

  it('close_setsOpenFalse', () => {
    const value = '{{';
    const { result } = setup({ initialValue: value, upstreamVars: sampleUpstream, cursor: value.length });
    act(() => result.current.api.refresh());

    act(() => result.current.api.close());

    expect(result.current.api.open).toBe(false);
  });

  it('handleKeyDown_doesNothingWhenClosed', () => {
    const { result } = setup({ upstreamVars: sampleUpstream });

    const preventDefault = vi.fn();
    act(() =>
      result.current.api.handleKeyDown({
        key: 'Enter',
        preventDefault,
      } as unknown as React.KeyboardEvent<HTMLInputElement>)
    );

    // Closed dropdown → handler bails out before preventDefault.
    expect(preventDefault).not.toHaveBeenCalled();
  });
});
