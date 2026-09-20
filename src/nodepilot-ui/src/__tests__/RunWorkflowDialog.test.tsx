import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { RunWorkflowDialog } from '../components/common/RunWorkflowDialog';

it('preserves user edits when last-run defaults arrive after the dialog opens', () => {
  const onExecute = vi.fn();
  const props = { workflowName: 'File delivery', triggerTitle: 'Start', triggerDescription: null, parameters: [
    { name: 'interval', type: 'string', required: true, default: '30' },
    { name: 'file', type: 'string', required: true, default: 'settings.json' },
  ], onExecute, onCancel: vi.fn() };
  const { rerender } = render(<RunWorkflowDialog {...props} />);
  fireEvent.change(screen.getByPlaceholderText('30'), { target: { value: '60' } });
  rerender(<RunWorkflowDialog {...props} parameters={[...props.parameters]} lastRunParams={{ interval: '15', file: 'previous.json' }} />);
  expect(screen.getByPlaceholderText('30')).toHaveValue('60');
  expect(screen.getByPlaceholderText('settings.json')).toHaveValue('previous.json');
  fireEvent.submit(screen.getByRole('dialog').querySelector('form')!);
  expect(onExecute).toHaveBeenCalledWith({ interval: '60', file: 'previous.json' });
});
