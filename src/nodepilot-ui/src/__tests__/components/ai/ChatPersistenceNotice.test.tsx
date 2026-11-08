import { beforeEach, describe, expect, it } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { ChatPersistenceNotice } from '../../../components/ai/ChatPersistenceNotice';
import { useChatPersistenceStatus } from '../../../stores/chatPersistenceStatus';

describe('ChatPersistenceNotice', () => {
  beforeEach(() => useChatPersistenceStatus.setState({ unavailable: false, trimmed: false }));

  it('renders nothing while persistence works', () => {
    const { container } = render(<ChatPersistenceNotice />);

    expect(container).toBeEmptyDOMElement();
  });

  it('warns that storage is unavailable once a write failed', () => {
    render(<ChatPersistenceNotice />);

    act(() => useChatPersistenceStatus.setState({ unavailable: true }));

    expect(screen.getByRole('status')).toHaveTextContent(/unavailable or full/i);
  });

  it('shows the trimmed hint when only the history was cut down', () => {
    render(<ChatPersistenceNotice />);

    act(() => useChatPersistenceStatus.setState({ trimmed: true }));

    expect(screen.getByRole('status')).toHaveTextContent(/only recent chat history/i);
  });

  it('prefers the unavailable warning when both flags are set', () => {
    useChatPersistenceStatus.setState({ unavailable: true, trimmed: true });

    render(<ChatPersistenceNotice />);

    expect(screen.getByRole('status')).toHaveTextContent(/unavailable or full/i);
  });
});
