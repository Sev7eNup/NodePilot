import { useContext } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import { useTranslation } from 'react-i18next';
import { projectAgentMembers, memberStatus } from '../../../lib/agentTeamProjection';
import { useAgentSelectionStore } from '../../../stores/agentSelectionStore';
import { useAgentEvents, useAgentRuns } from '../../../hooks/useAgentRuns';
import { AgentCanvasExecutionContext } from '../agents/AgentRunPanel';

export function AgentTeamNode({ id, data, selected, isConnectable }: NodeProps) {
  const { t } = useTranslation('agents');
  const selection = useAgentSelectionStore();
  const scope = useContext(AgentCanvasExecutionContext);
  const { data: runs = [] } = useAgentRuns(scope.executionId, scope.active);
  const run = runs.filter(run => run.stepId === id).at(-1);
  const { data: allEvents = [] } = useAgentEvents(run);
  const events = scope.scrubTimeMs === null ? allEvents : allEvents.filter(event => Date.parse(event.timestamp) <= scope.scrubTimeMs!);
  const members = projectAgentMembers((data.config ?? {}) as Record<string, unknown>);
  const ordered = [...members.filter(m => m.isSupervisor), ...members.filter(m => !m.isSupervisor)];
  return <div className={`rounded-xl border-2 bg-surface-lowest shadow-md text-on-surface ${selected ? 'border-primary' : 'border-outline-variant'}`}
    style={{ width: 300 }} data-testid="agent-team-node">
    <Handle id="left" type="target" position={Position.Left} isConnectable={isConnectable} />
    <Handle id="right" type="source" position={Position.Right} isConnectable={isConnectable} />
    <div className="px-3 py-2 border-b border-outline-variant font-semibold text-sm">{String(data.label || t('members'))}
      {!!data.__liveStatus && <span className="ml-2 text-xs font-normal">{t(`status.${String(data.__liveStatus)}`)}</span>}</div>
    <div className="p-3 space-y-2">
      {ordered.map(member => {
        let status = memberStatus(events, member.id) ?? 'Idle';
        if (scope.scrubTimeMs === null && run && run.status !== 'Running' && (status === 'Running' || member.isSupervisor)) status = run.status;
        return <button key={member.id} type="button" data-agent-member-id={member.id}
          className={`nodrag nopan block w-full text-left rounded border px-2 py-2 ${selection.nodeId === id && selection.memberId === member.id ? 'border-primary bg-primary/10' : 'border-outline-variant bg-surface-low'}`}
          onClick={() => selection.select(id, member.id)}>
          <span className="flex justify-between text-xs"><strong>{member.isSupervisor ? '★ ' : '↳ '}{member.role}</strong>
            <span className={status === 'Running' ? 'text-running animate-pulse' : status === 'Failed' ? 'text-error' : 'text-on-surface-variant'}>{t(`status.${status}`)}</span></span>
          <span className="text-[10px] text-on-surface-variant">{member.isSupervisor ? t('supervisor') + ' · ' : member.isReviewer ? t('reviewer') + ' · ' : ''}{t('tools')}: {member.tools?.length ?? 0}</span>
        </button>;
      })}
      {members.length === 0 && <p className="text-xs text-on-surface-variant">{t('createTeam')}</p>}
    </div>
  </div>;
}
