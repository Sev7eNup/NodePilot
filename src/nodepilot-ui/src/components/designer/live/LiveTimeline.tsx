import { useEffect, useState } from 'react';
import type { LiveExecution } from '../../../hooks/useSignalR';
import { GanttChart, type GanttRow } from '../timeline/GanttChart';

interface Props {
  execution: LiveExecution;
  /** Click a bar to select that step in the parent (which switches to inspector view). */
  onSelectStep?: (stepId: string) => void;
}

/**
 * Live wrapper around the shared `GanttChart`. Maps `StepUpdate[]` to `GanttRow[]` and
 * pumps a 250 ms now-ticker so running bars grow in real time. The actual rendering —
 * axis, rows, status colors — lives in `GanttChart` and is shared with the History tab.
 */
export function LiveTimeline({ execution, onSelectStep }: Readonly<Props>) {
  const isRunning = execution.status === 'Running' || execution.status === 'Pending'
    || execution.steps.some((s) => s.status === 'Running');
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!isRunning) return;
    const id = setInterval(() => setNow(Date.now()), 250);
    return () => clearInterval(id);
  }, [isRunning]);

  const rows: GanttRow[] = [...execution.steps]
    .sort((a, b) => {
      if (!a.startedAt) return 1;
      if (!b.startedAt) return -1;
      return new Date(a.startedAt).getTime() - new Date(b.startedAt).getTime();
    })
    .map((s) => ({
      id: s.stepId,
      name: s.stepName || s.stepId,
      status: s.status,
      startMs: s.startedAt ? new Date(s.startedAt).getTime() : null,
      endMs: s.completedAt ? new Date(s.completedAt).getTime() : null,
    }));

  return (
    <div className="flex flex-col h-full overflow-hidden p-2">
      <div className="flex-1 overflow-auto" data-testid="live-timeline-gantt">
        <GanttChart
          rows={rows}
          nowMs={isRunning ? now : undefined}
          onSelectRow={onSelectStep}
          minWidthPx={480}
        />
      </div>
    </div>
  );
}
