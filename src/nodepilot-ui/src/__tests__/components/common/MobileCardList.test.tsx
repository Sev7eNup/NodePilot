import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MobileCardList } from '../../../components/common/MobileCardList';

interface Row { id: string; name: string; host: string; }
const ROWS: Row[] = [
  { id: 'a', name: 'Alpha', host: 'alpha.local' },
  { id: 'b', name: 'Bravo', host: 'bravo.local' },
];

describe('MobileCardList', () => {
  it('renders one card per item with title, fields and actions', () => {
    render(
      <MobileCardList
        items={ROWS}
        getKey={(r) => r.id}
        renderTitle={(r) => <span>{r.name}</span>}
        renderFields={(r) => [
          { label: 'Host', value: <span>{r.host}</span> },
          { label: 'Span', value: <span>full-{r.id}</span>, full: true },
        ]}
        renderActions={(r) => <button>edit-{r.id}</button>}
      />,
    );

    expect(screen.getByTestId('mobile-card-list')).toBeInTheDocument();
    expect(screen.getByText('Alpha')).toBeInTheDocument();
    expect(screen.getByText('Bravo')).toBeInTheDocument();
    expect(screen.getAllByText('Host')).toHaveLength(2); // one label per card
    expect(screen.getByText('alpha.local')).toBeInTheDocument();
    expect(screen.getByText('full-a')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'edit-a' })).toBeInTheDocument();
  });

  it('fires onRowClick when the card is tapped', () => {
    const onRowClick = vi.fn();
    render(
      <MobileCardList
        items={ROWS}
        getKey={(r) => r.id}
        renderTitle={(r) => <span>{r.name}</span>}
        renderFields={() => []}
        onRowClick={onRowClick}
      />,
    );
    fireEvent.click(screen.getByText('Alpha'));
    expect(onRowClick).toHaveBeenCalledWith(ROWS[0]);
  });

  it('does not fire onRowClick when an action button is tapped', () => {
    const onRowClick = vi.fn();
    const onAction = vi.fn();
    render(
      <MobileCardList
        items={[ROWS[0]]}
        getKey={(r) => r.id}
        renderTitle={(r) => <span>{r.name}</span>}
        renderFields={() => []}
        renderActions={() => <button onClick={onAction}>act</button>}
        onRowClick={onRowClick}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'act' }));
    expect(onAction).toHaveBeenCalledTimes(1);
    expect(onRowClick).not.toHaveBeenCalled();
  });
});
