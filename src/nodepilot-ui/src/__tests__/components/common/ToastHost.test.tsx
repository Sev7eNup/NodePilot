import { describe, expect, it, beforeEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import { ToastHost } from '../../../components/common/ToastHost';
import { toast, useToastStore } from '../../../stores/toastStore';

describe('ToastHost', () => {
  beforeEach(() => {
    useToastStore.setState({ toasts: [] });
  });

  it('rendersNothing_whenQueueEmpty', () => {
    const { container } = render(<ToastHost />);
    expect(container).toBeEmptyDOMElement();
  });

  it('rendersToastMessage_whenPushed', () => {
    render(<ToastHost />);
    act(() => {
      toast.success('Import complete');
    });
    expect(screen.getByText('Import complete')).toBeInTheDocument();
    expect(screen.getByTestId('toast-success')).toBeInTheDocument();
  });

  it('dismissButton_removesToast', () => {
    render(<ToastHost />);
    act(() => {
      toast.error('kaputt');
    });
    expect(screen.getByText('kaputt')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByText('kaputt')).not.toBeInTheDocument();
  });

  it('stacksMultipleToasts', () => {
    render(<ToastHost />);
    act(() => {
      toast.info('one');
      toast.info('two');
    });
    expect(screen.getByText('one')).toBeInTheDocument();
    expect(screen.getByText('two')).toBeInTheDocument();
  });
});
