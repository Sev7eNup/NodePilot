import { useContext, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../../../api/client';
import { randomUuid } from '../../../../lib/uuid';
import type { AgentDefinition, AgentMcpServer, AgentMcpTool, AgentSkill, AgentToolSelection } from '../../../../types/agents';
import { projectAgentMembers } from '../../../../lib/agentTeamProjection';
import { useAgentSelectionStore } from '../../../../stores/agentSelectionStore';
import { AgentAuthoringContext } from '../../agents/AgentAuthoringContext';
import { DynamicTargetField, Field, VariableInsertField, type ConfigProps } from '../shared';

const nativeTools = ['files_list', 'files_read', 'files_write', 'files_search', 'logs_collect', 'logs_search',
  'http_request', 'workflow_run', 'powershell', 'cmd', 'bash'];
export const newAgent = (id: string, role: string, isSupervisor = false): AgentDefinition =>
  ({ id, role, instructions: '', isSupervisor, tools: [], skillIds: [] });

export function AiAgentConfig(props: Readonly<ConfigProps>) { return <AgentConfig {...props} team={false} />; }
export function AiAgentTeamConfig(props: Readonly<ConfigProps>) { return <AgentConfig {...props} team />; }

function AgentConfig({ config, onUpdate, upstreamVars = [], stepId = '', team }: ConfigProps & { team: boolean }) {
  const { t } = useTranslation('agents');
  const selection = useAgentSelectionStore();
  const members = projectAgentMembers(config);
  const selected = members.find(m => selection.nodeId === stepId && m.id === selection.memberId) ?? members[0];
  const agent = team ? selected : (config.agent as AgentDefinition | undefined) ?? newAgent('agent', 'Assistant');
  const supervisor = members.find(m => m.isSupervisor);
  const removeHint = agent?.isSupervisor ? 'removeSupervisorHint' : members.length <= 2 ? 'minimumMembersHint' : null;
  const updateAgent = (patch: Partial<AgentDefinition>) => {
    if (!agent) return;
    if (team) onUpdate({ members: members.map(m => m.id === agent.id ? { ...m, ...patch } : m) });
    else onUpdate({ agent: { ...agent, ...patch } });
  };
  return <div className="space-y-3">
    <p className="text-xs text-on-surface-variant">{t('authorization')}</p>
    <VariableInsertField label={t('task')} value={String(config.task ?? '')}
      onChange={task => onUpdate({ task })} upstreamVars={upstreamVars} multiline rows={4} />
    {team && <div className="space-y-4">
      {members.length > 0 && <div className="rounded-lg border border-outline-variant bg-surface-low p-3 space-y-2">
        <label htmlFor={`team-lead-${stepId}`} className="block text-xs font-semibold">{t('teamLead')}</label>
        <select id={`team-lead-${stepId}`} className="input-field" value={supervisor?.id ?? ''}
          aria-describedby={`team-lead-hint-${stepId}`} onChange={e => {
            onUpdate({ members: members.map(m => ({ ...m, isSupervisor: m.id === e.target.value, isReviewer: m.id === e.target.value ? false : m.isReviewer })) });
            selection.select(stepId, e.target.value);
          }}>
          {!supervisor && <option value="" disabled>{t('choose')}</option>}
          {members.map(m => <option key={m.id} value={m.id}>{m.role}</option>)}
        </select>
        <p id={`team-lead-hint-${stepId}`} className="text-xs text-on-surface-variant">{t('teamHint')}</p>
      </div>}
      <div className="space-y-2">
        <p className="text-xs font-semibold">{t('members')}</p>
        <div className="flex flex-wrap gap-2" role="tablist" aria-label={t('members')}>
          {members.map(m => <button type="button" role="tab" aria-selected={m.id === agent?.id} key={m.id}
            className={`px-3 py-2 rounded-md border text-xs ${m.id === agent?.id ? 'border-primary bg-primary/15 text-primary' : 'border-outline-variant bg-surface-high'}`}
            onClick={() => selection.select(stepId, m.id)}>{m.role}
            {m.isSupervisor && <span className="ml-2 text-[10px]">{t('teamLeadBadge')}</span>}
            {m.isReviewer && <span className="ml-2 text-[10px]">{t('reviewer')}</span>}</button>)}
        </div>
        {members.length === 0 ? <button type="button" className="text-primary text-sm" onClick={() => onUpdate({
          members: [newAgent('supervisor', t('supervisor'), true), newAgent('specialist', t('newMemberRole', { number: 1 }))],
        })}>{t('createTeam')}</button> : <button type="button" className="text-primary text-xs" disabled={members.length >= 12}
          onClick={() => {
            let number = 1;
            while (members.some(m => m.role === t('newMemberRole', { number }))) number++;
            const m = newAgent(randomUuid(), t('newMemberRole', { number }));
            onUpdate({ members: [...members, m] }); selection.select(stepId, m.id);
          }}>
          {t('addMember')}</button>}
      </div>
    </div>}
    {agent && <div className={team ? 'rounded-lg border border-outline-variant p-3 space-y-3' : undefined}>
      {team && <p className="text-xs font-semibold">{t('memberSettings', { role: agent.role })}</p>}
      <AgentMemberEditor key={agent.id} agent={agent} update={updateAgent} nested={team} upstreamVars={upstreamVars} />
      {team && <div className="border-t border-outline-variant pt-3 space-y-2">
        <button type="button" className="text-xs text-error disabled:text-on-surface-variant disabled:opacity-50"
          disabled={!!removeHint} aria-describedby={removeHint ? `remove-member-hint-${stepId}` : undefined}
          onClick={() => {
            onUpdate({ members: members.filter(m => m.id !== agent.id) });
            selection.select(stepId, supervisor?.id ?? members.find(m => m.id !== agent.id)!.id);
          }}>{t('removeMember')}</button>
        {removeHint && <p id={`remove-member-hint-${stepId}`} className="text-xs text-on-surface-variant">{t(removeHint)}</p>}
      </div>}
    </div>}
    <Field label={t('resultFormat')}><select className="input-field" value={String(config.resultFormat ?? 'text')}
      onChange={e => onUpdate({ resultFormat: e.target.value })}><option value="text">{t('text')}</option><option value="json">JSON</option></select></Field>
    {config.resultFormat === 'json' && <JsonSchemaField value={config.resultSchema} onChange={resultSchema => onUpdate({ resultSchema })} />}
    <details><summary className="text-xs cursor-pointer">{t('budgets')}</summary>
      <p className="text-xs text-on-surface-variant my-2">{t(team ? 'teamBudgetHint' : 'singleBudgetHint')}</p>
      {(team ? ['maxModelCalls', 'maxToolCalls', 'maxDelegations', 'maxParallelMembers'] : ['maxModelCalls', 'maxToolCalls']).map(key =>
        <Field key={key} label={t(key)}><input className="input-field" type="number" min={1}
          value={typeof config[key] === 'number' ? config[key] as number : ''}
          onChange={e => onUpdate({ [key]: e.target.value === '' ? undefined : Number(e.target.value) })} /></Field>)}
    </details>
  </div>;
}

function JsonSchemaField({ value, onChange }: { value: unknown; onChange: (value: unknown) => void }) {
  const { t } = useTranslation('agents');
  const [draft, setDraft] = useState(() => value === undefined ? '' : JSON.stringify(value, null, 2));
  const [invalid, setInvalid] = useState(false);
  return <Field label={t('resultSchema')}><textarea className="input-field font-mono" rows={6} value={draft}
    aria-invalid={invalid} onChange={e => { setDraft(e.target.value); try { const v: unknown = JSON.parse(e.target.value); onChange(v); setInvalid(false); } catch { setInvalid(true); } }} />
    {invalid && <p role="alert" className="text-error text-xs">{t('invalidJson')}</p>}</Field>;
}

function AgentMemberEditor({ agent, update, nested, upstreamVars }: {
  agent: AgentDefinition; update: (patch: Partial<AgentDefinition>) => void; nested: boolean;
  upstreamVars: NonNullable<ConfigProps['upstreamVars']>;
}) {
  const { t } = useTranslation('agents');
  const binding = useContext(AgentAuthoringContext);
  const target = nested ? agent : binding.data;
  const updateTarget = nested ? update : binding.update;
  const { data: skills = [], error: skillError } = useQuery({ queryKey: ['agent-skills'], queryFn: () => api.get<AgentSkill[]>('/agents/skills') });
  const tools = agent.tools ?? [];
  const needsTarget = tools.some(tool => /^(files_|logs_collect$|powershell$|cmd$|bash$)/.test(tool.name));
  return <div className="space-y-3">
    {nested && <Field label={t('teamFunction')}>
      <select className="input-field" aria-label={t('teamFunction')} disabled={agent.isSupervisor}
        value={agent.isSupervisor ? 'supervisor' : agent.isReviewer ? 'reviewer' : 'specialist'}
        onChange={e => update({ isReviewer: e.target.value === 'reviewer' })}>
        {agent.isSupervisor && <option value="supervisor">{t('supervisor')}</option>}
        <option value="specialist">{t('specialist')}</option><option value="reviewer">{t('reviewer')}</option>
      </select>
      <p className="text-xs text-on-surface-variant">{t('reviewerHint')}</p>
    </Field>}
    <Field label={t('role')}><input aria-label={t('role')} className="input-field" maxLength={128} value={agent.role} onChange={e => update({ role: e.target.value })} /></Field>
    {nested && <p className="text-xs text-on-surface-variant">{t('roleHint')}</p>}
    <VariableInsertField label={t('instructions')} value={agent.instructions ?? ''} onChange={instructions => update({ instructions })}
      upstreamVars={upstreamVars} multiline rows={4}
      placeholder={t(nested && agent.isSupervisor ? 'supervisorInstructionsPlaceholder' : 'instructionsPlaceholder')} />
    <p className="text-xs text-on-surface-variant">{t('instructionsHint')}</p>
    <Field label={t('model')}><input className="input-field" value={agent.model ?? ''} placeholder={t('activeProfile')}
      onChange={e => update({ model: e.target.value || undefined })} /></Field>
    {nested && agent.isSupervisor && <p className="text-xs text-on-surface-variant">{t('delegationIncluded')}</p>}
    <ToolEditor tools={tools} update={tools => update({ tools })} />
    {needsTarget && <div className="space-y-3 border-t border-outline-variant pt-3">
      <DynamicTargetField label={t('machine')} value={String(target.targetMachineId ?? '')}
        onChange={targetMachineId => updateTarget({ targetMachineId: targetMachineId || null })}
        options={binding.machines.map(m => ({ id: m.id, label: `${m.name} (${m.hostname})` }))}
        placeholder="GUID" upstreamVars={nested ? [] : upstreamVars} emptyLabel={t('localMachine')} optionPickerLabel={t('choose')} />
      <label className="flex gap-2 text-xs"><input type="checkbox" checked={agent.useServiceIdentity === true}
        onChange={e => update({ useServiceIdentity: e.target.checked })} />{t('serviceIdentity')}</label>
      <p className="text-xs text-on-surface-variant">{t('identityHint')}</p>
      {!agent.useServiceIdentity && <DynamicTargetField label={t('credential')} value={String(target.credentialId ?? '')}
        onChange={credentialId => updateTarget({ credentialId: credentialId || null })}
        options={binding.credentials.map(c => ({ id: c.id, label: `${c.name} (${c.username})` }))}
        placeholder="GUID" upstreamVars={nested ? [] : upstreamVars} emptyLabel={t('credentialRequired')} optionPickerLabel={t('choose')} />}
      <Field label={t('workingDirectory')}><input className="input-field" value={agent.workingDirectory ?? ''} onChange={e => update({ workingDirectory: e.target.value || undefined })} /></Field>
      {tools.some(tool => tool.name === 'bash') && <Field label={t('bashPath')}><input className="input-field" value={agent.bashPath ?? ''} onChange={e => update({ bashPath: e.target.value || undefined })} /></Field>}
    </div>}
    <Field label={t('skills')}>
      {[...new Set(agent.skillIds ?? [])].filter(id => !skills.some(skill => skill.id === id)).map(id =>
        <div key={id} className="flex justify-between gap-2 text-xs py-1 text-error">
          <span>{t('missingSkill', { id })}</span>
          <button type="button" onClick={() => update({ skillIds: (agent.skillIds ?? []).filter(value => value !== id) })}>{t('remove')}</button>
        </div>)}
      {skills.filter(s => s.enabled || agent.skillIds?.includes(s.id)).map(skill => <label key={skill.id} className="flex gap-2 text-xs py-1" title={skill.description}>
        <input type="checkbox" checked={agent.skillIds?.includes(skill.id) ?? false} onChange={e => update({ skillIds: e.target.checked
          ? [...(agent.skillIds ?? []), skill.id] : (agent.skillIds ?? []).filter(id => id !== skill.id) })} />
        {skill.name} · {skill.version}{!skill.enabled && ` (${t('disabled')})`}</label>)}
      {skillError && <p role="alert" className="text-error text-xs">{skillError.message}</p>}
      {skills.length === 0 && <p className="text-xs text-on-surface-variant">{t('noSkills')}</p>}
    </Field>
  </div>;
}

function ToolEditor({ tools, update }: { tools: AgentToolSelection[]; update: (tools: AgentToolSelection[]) => void }) {
  const { t } = useTranslation('agents');
  const [serverId, setServerId] = useState('');
  const { data: servers = [], error } = useQuery({ queryKey: ['agent-mcp-servers'], queryFn: () => api.get<AgentMcpServer[]>('/agents/mcp-servers') });
  const { data: mcpTools = [], error: toolsError, isFetching } = useQuery({ queryKey: ['agent-mcp-tools', serverId], enabled: !!serverId,
    queryFn: () => api.get<AgentMcpTool[]>(`/agents/mcp-servers/${serverId}/tools`) });
  const change = (index: number, patch: Partial<AgentToolSelection>) => update(tools.map((tool, i) => i === index ? { ...tool, ...patch } : tool));
  return <Field label={t('tools')}>
    <div className="grid grid-cols-2 gap-1">{nativeTools.map(name => <label key={name} className="flex gap-2 text-xs py-1" title={t(`toolHints.${name}`)}>
      <input type="checkbox" checked={tools.some(tool => tool.name === name)} onChange={e => update(e.target.checked ? [...tools, { name }] : tools.filter(tool => tool.name !== name))} />
      {t(`toolNames.${name}`)}</label>)}</div>
    {tools.some(tool => ['powershell', 'cmd', 'bash'].includes(tool.name)) && <p className="text-xs text-on-surface-variant py-2">{t('shellHint')}</p>}
    {tools.map((tool, index) => <div key={`${tool.name}-${tool.mcpServerId ?? index}-${tool.mcpToolName ?? ''}`}>
      {(tool.name.startsWith('files_') || tool.name === 'logs_collect') && <Lines label={`${t(`toolNames.${tool.name}`)}: ${t('allowedPaths')}`} value={tool.allowedPaths ?? []} onChange={allowedPaths => change(index, { allowedPaths })} />}
      {tool.name === 'http_request' && <Lines label={t('allowedHosts')} value={tool.allowedHosts ?? []} onChange={allowedHosts => change(index, { allowedHosts })} />}
      {tool.name === 'workflow_run' && <Lines label={t('workflowIds')} value={tool.workflowIds ?? []} onChange={workflowIds => change(index, { workflowIds })} />}
      {tool.name === 'mcp' && <div className="flex justify-between text-xs py-1"><span>{servers.find(s => s.id === tool.mcpServerId)?.name ?? tool.mcpServerId}: {tool.mcpToolName}</span>
        <button type="button" onClick={() => update(tools.filter((_, i) => i !== index))}>{t('remove')}</button></div>}
    </div>)}
    <select className="input-field mt-2" aria-label={t('mcpServer')} value={serverId} onChange={e => setServerId(e.target.value)}>
      <option value="">{t('mcpServer')}</option>{servers.filter(s => s.enabled).map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
    </select>
    {isFetching && <p className="text-xs">{t('loading')}</p>}
    {(error || toolsError) && <p role="alert" className="text-error text-xs">{(error || toolsError)?.message}</p>}
    {mcpTools.map(tool => <label key={tool.name} className="flex gap-2 text-xs py-1" title={tool.description ?? ''}>
      <input type="checkbox" checked={tools.some(selected => selected.mcpServerId === serverId && selected.mcpToolName === tool.name)}
        onChange={e => update(e.target.checked ? [...tools, { name: 'mcp', mcpServerId: serverId, mcpToolName: tool.name }]
          : tools.filter(selected => selected.mcpServerId !== serverId || selected.mcpToolName !== tool.name))} />{tool.name}</label>)}
  </Field>;
}

function Lines({ label, value, onChange }: { label: string; value: string[]; onChange: (values: string[]) => void }) {
  const [draft, setDraft] = useState(value.join('\n'));
  return <Field label={label}><textarea className="input-field font-mono" rows={2} value={draft}
    onChange={e => { setDraft(e.target.value); onChange(e.target.value.split(/\r?\n/).map(v => v.trim()).filter(Boolean)); }} /></Field>;
}
