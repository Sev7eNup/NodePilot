import type { AgentRunEvent } from '../types/agents';

export interface AgentCoverageItem {
  requirement: string;
  status: 'fulfilled' | 'unresolved';
  basis: string;
}

/** Only the latest final assessment describes the delivered result. */
export function agentRunCoverage(events: AgentRunEvent[]): AgentCoverageItem[] | null {
  const conclusion = events.filter(event => event.kind === 'run_conclusion')
    .reduce<AgentRunEvent | undefined>((latest, event) => !latest || event.sequence > latest.sequence ? event : latest, undefined);
  if (!conclusion) return null;
  try {
    const value: unknown = JSON.parse(conclusion.content);
    if (!value || typeof value !== 'object' || !('coverage' in value)) return null;
    const items = value.coverage;
    if (!Array.isArray(items) || items.length === 0 || items.length > 20) return null;
    if (!items.every(item => item && typeof item === 'object'
      && typeof item.requirement === 'string' && item.requirement.trim()
      && typeof item.basis === 'string' && item.basis.trim()
      && (item.status === 'fulfilled' || item.status === 'unresolved'))) return null;
    return items as AgentCoverageItem[];
  } catch { return null; }
}
