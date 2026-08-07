import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DatabaseOutageBanner } from '../../../components/layout/DatabaseOutageBanner';
import { resetDbHealth, useDbHealthStore } from '../../../stores/dbHealthStore';

const OUTAGE_STARTED_AT = '2026-08-07T10:00:00.000Z';

describe('DatabaseOutageBanner', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(OUTAGE_STARTED_AT));
    resetDbHealth();
  });

  afterEach(() => {
    cleanup();
    resetDbHealth();
    vi.useRealTimers();
  });

  it('escalates from reconnecting guidance when the outage reaches 60 seconds', () => {
    useDbHealthStore.setState({
      status: 'unavailable',
      sinceUtc: OUTAGE_STARTED_AT,
      reason: 'Unreachable',
    });
    render(<DatabaseOutageBanner />);

    expect(screen.getByText(/keeps checking and resumes on its own/i)).toBeInTheDocument();

    act(() => vi.advanceTimersByTime(59_999));
    expect(screen.queryByText(/check the database service/i)).not.toBeInTheDocument();

    act(() => vi.advanceTimersByTime(1));
    expect(screen.getByText(/check the database service/i)).toBeInTheDocument();
  });

  it('shows administrator guidance immediately when the database rejects the connection', () => {
    useDbHealthStore.setState({
      status: 'unavailable',
      sinceUtc: OUTAGE_STARTED_AT,
      reason: 'RejectedByServer',
    });
    render(<DatabaseOutageBanner />);

    expect(screen.getByText(/administrator has to fix the configuration/i)).toBeInTheDocument();
    expect(screen.queryByText(/keeps checking and resumes on its own/i)).not.toBeInTheDocument();
    expect(screen.queryByTitle(/keeps checking and resumes on its own/i)).not.toBeInTheDocument();

    act(() => vi.advanceTimersByTime(60_000));
    expect(screen.getByText(/administrator has to fix the configuration/i)).toBeInTheDocument();
  });
});
