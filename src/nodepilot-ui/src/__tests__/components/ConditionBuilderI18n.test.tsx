import { describe, it, expect, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import i18n from '../../i18n';
import { ConditionBuilder, type ExprNode } from '../../components/designer/ConditionBuilder';

const COMPARISON: ExprNode = {
  type: 'comparison',
  left: { kind: 'literal', value: '5' },
  op: '<',
  right: { kind: 'literal', value: '10' },
};

function renderBuilder(value: ExprNode | null, eventFields?: { name: string; label: string }[]) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ConditionBuilder value={value} upstreamVars={[]} eventFields={eventFields} onChange={() => {}} />
    </QueryClientProvider>,
  );
}

afterEach(async () => { await i18n.changeLanguage('en'); });

describe('ConditionBuilder — i18n', () => {
  it('ConditionBuilder_GermanUi_RendersGermanLabels', async () => {
    await i18n.changeLanguage('de');
    renderBuilder(COMPARISON);
    expect(screen.getByText(/Vergleiche Output-Werte vorheriger Steps/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Bedingung/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Gruppe/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /NICHT/ })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'kleiner als' })).toBeInTheDocument();
    expect(screen.getAllByPlaceholderText('fester Wert oder {{globals.X}}').length).toBeGreaterThan(0);
  });

  it('ConditionBuilder_EnglishUi_HasNoGermanIntro', async () => {
    await i18n.changeLanguage('en');
    renderBuilder(COMPARISON);
    expect(screen.getByText(/Compare output values of previous steps/)).toBeInTheDocument();
    expect(screen.queryByText(/Vergleiche/)).toBeNull();
    expect(screen.getByRole('option', { name: 'less than' })).toBeInTheDocument();
  });

  it('ConditionBuilder_EventMode_UsesFilterWordingNotEdgeWording', async () => {
    await i18n.changeLanguage('de');
    renderBuilder(null, [{ name: 'workflowName', label: 'Workflow' }]);
    expect(screen.getByText(/Vergleiche Event-Felder/)).toBeInTheDocument();
    expect(screen.getByText('Kein Filter gesetzt — die Regel greift bei jedem passenden Ereignis.')).toBeInTheDocument();
    expect(screen.queryByText(/Kante ist immer aktiv/)).toBeNull();
  });
});
