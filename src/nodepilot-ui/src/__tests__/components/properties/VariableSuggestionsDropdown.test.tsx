import { describe, it, expect, vi } from 'vitest';
import { useRef } from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { VariableSuggestionsDropdown } from '../../../components/designer/properties/VariableSuggestionsDropdown';
import type { VariableSuggestion } from '../../../components/designer/properties/useVariableAutocomplete';

/**
 * VariableSuggestionsDropdown is a portalled list anchored under an input. Covered here:
 *   - hidden when open=false
 *   - hidden when suggestions=[] even if open
 *   - renders one button per suggestion with code and label
 *   - the selectedIdx row gets the highlight class (bg-primary-fixed)
 *   - onMouseDown on a row calls onPick with the suggestion's expression
 *   - showHelp toggles the bottom hint
 */

function Harness({
  open,
  suggestions,
  selectedIdx = 0,
  onPick = vi.fn(),
  showHelp = true,
}: {
  open: boolean;
  suggestions: VariableSuggestion[];
  selectedIdx?: number;
  onPick?: (e: string) => void;
  showHelp?: boolean;
}) {
  const inputRef = useRef<HTMLInputElement>(null);
  return (
    <div>
      <input ref={inputRef} data-testid="anchor" />
      <VariableSuggestionsDropdown
        open={open}
        suggestions={suggestions}
        selectedIdx={selectedIdx}
        onPick={onPick}
        anchorRef={inputRef}
        showHelp={showHelp}
      />
    </div>
  );
}

describe('VariableSuggestionsDropdown', () => {
  it('open=false_rendersNothing', () => {
    render(<Harness open={false} suggestions={[{ expression: '{{a.output}}', label: 'a' }]} />);
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });

  it('emptySuggestions_rendersNothingEvenWhenOpen', () => {
    render(<Harness open={true} suggestions={[]} />);
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });

  it('rendersOneButtonPerSuggestion', () => {
    const suggestions: VariableSuggestion[] = [
      { expression: '{{a.output}}', label: 'Step A' },
      { expression: '{{b.output}}', label: 'Step B' },
    ];
    render(<Harness open={true} suggestions={suggestions} />);

    expect(screen.getByText('{{a.output}}')).toBeInTheDocument();
    expect(screen.getByText('Step A')).toBeInTheDocument();
    expect(screen.getByText('{{b.output}}')).toBeInTheDocument();
    expect(screen.getByText('Step B')).toBeInTheDocument();
  });

  it('selectedIdx_highlightsTheRow', () => {
    const suggestions: VariableSuggestion[] = [
      { expression: '{{a}}', label: 'A' },
      { expression: '{{b}}', label: 'B' },
    ];
    render(<Harness open={true} suggestions={suggestions} selectedIdx={1} />);

    const buttons = screen.getAllByRole('button');
    expect(buttons[1].className).toContain('bg-primary-fixed');
    expect(buttons[0].className).not.toContain('bg-primary-fixed');
  });

  it('mouseDown_callsOnPickWithExpression', () => {
    const onPick = vi.fn();
    render(
      <Harness
        open={true}
        suggestions={[{ expression: '{{x.output}}', label: 'X' }]}
        onPick={onPick}
      />
    );

    fireEvent.mouseDown(screen.getByText('{{x.output}}'));
    expect(onPick).toHaveBeenCalledWith('{{x.output}}');
  });

  it('mouseDown_isPreventedToPreventBlur', () => {
    // The dropdown uses onMouseDown (not onClick) and calls e.preventDefault() so the
    // anchored input does not lose focus before the pick fires.
    const onPick = vi.fn();
    render(
      <Harness
        open={true}
        suggestions={[{ expression: '{{x.output}}', label: 'X' }]}
        onPick={onPick}
      />
    );

    const evt = new MouseEvent('mousedown', { bubbles: true, cancelable: true });
    screen.getByText('{{x.output}}').dispatchEvent(evt);
    expect(evt.defaultPrevented).toBe(true);
  });

  it('showHelp=true_rendersHelpFooter', () => {
    render(
      <Harness
        open={true}
        suggestions={[{ expression: '{{x}}', label: 'x' }]}
        showHelp={true}
      />
    );
    expect(screen.getByText(/↑↓ navigate/)).toBeInTheDocument();
  });

  it('showHelp=false_omitsHelpFooter', () => {
    render(
      <Harness
        open={true}
        suggestions={[{ expression: '{{x}}', label: 'x' }]}
        showHelp={false}
      />
    );
    expect(screen.queryByText(/↑↓ navigate/)).not.toBeInTheDocument();
  });

  it('rendersAsListboxRole', () => {
    render(
      <Harness open={true} suggestions={[{ expression: '{{a}}', label: 'A' }]} />
    );
    expect(screen.getByRole('listbox')).toBeInTheDocument();
  });
});
