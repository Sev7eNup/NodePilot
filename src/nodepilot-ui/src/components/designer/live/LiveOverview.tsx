import { ChartBar, Terminal } from '@carbon/icons-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { LiveExecution } from '../../../hooks/useSignalR';
import { StatsStrip } from './StatsStrip';
import { LiveTimeline } from './LiveTimeline';
import { LiveConsole } from './LiveConsole';

interface Props {
  execution: LiveExecution;
  onSelectStep?: (stepId: string) => void;
  medianRunDurationMs?: number;
}

type SubTab = 'timeline' | 'console';

/**
 * Composite view shown in the right pane of the Live tab when no individual step is
 * selected. Stacks a stats header, a sub-tab switcher (Timeline / Console), and content.
 *
 * Selecting a node, bar, or console line bubbles up to the parent, which switches the
 * right pane to the existing per-step inspector, so this component never renders the
 * inspector itself.
 */
export function LiveOverview({ execution, onSelectStep, medianRunDurationMs }: Readonly<Props>) {
  const { t } = useTranslation('designer');
  // Timeline is the default tab; the console is the secondary lookup for output text,
  // since seeing run progress over time is usually the first thing users want.
  const [tab, setTab] = useState<SubTab>('timeline');

  return (
    <div className="flex flex-col h-full overflow-hidden">
      <StatsStrip execution={execution} medianRunDurationMs={medianRunDurationMs} />
      <div className="flex items-center gap-0.5 px-2 py-1 border-b border-outline-variant/10 shrink-0 bg-surface-low/30">
        <SubTabButton active={tab === 'timeline'} onClick={() => setTab('timeline')}>
          <ChartBar size={11} />
          {t('live.overview.timeline')}
        </SubTabButton>
        <SubTabButton active={tab === 'console'} onClick={() => setTab('console')}>
          <Terminal size={11} />
          {t('live.overview.console')}
        </SubTabButton>
      </div>
      <div className="flex-1 min-h-0 overflow-hidden">
        {tab === 'timeline' ? (
          <LiveTimeline execution={execution} onSelectStep={onSelectStep} />
        ) : (
          <LiveConsole execution={execution} onSelectStep={onSelectStep} />
        )}
      </div>
    </div>
  );
}

function SubTabButton({ active, onClick, children }: Readonly<{ active: boolean; onClick: () => void; children: React.ReactNode }>) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`flex items-center gap-1.5 px-2.5 py-1 rounded text-[11px] font-label font-semibold transition-colors ${
        active
          ? 'bg-primary/15 text-primary'
          : 'text-on-surface-variant hover:text-on-surface hover:bg-surface-high'
      }`}
    >
      {children}
    </button>
  );
}
