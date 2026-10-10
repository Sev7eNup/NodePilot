import { Add, MagicWandFilled } from '@carbon/icons-react';
import { useContext, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../../../api/client';
import { randomUuid } from '../../../../lib/uuid';
import type { AgentDefinition, AgentMcpServer, AgentMcpTool, AgentSkill, AgentToolSelection } from '../../../../types/agents';
import { projectAgentMembers } from '../../../../lib/agentTeamProjection';
import { useAgentSelectionStore } from '../../../../stores/agentSelectionStore';
import { AgentAuthoringContext } from '../../agents/AgentAuthoringContext';
import { AgentTeamDraftDialog } from '../../agents/AgentTeamDraftDialog';
import { useAiCapabilities } from '../../../../hooks/useAiCapabilities';
import { useRole } from '../../../../lib/rbac';
import { DynamicTargetField, Field, VariableInsertField, type ConfigProps } from '../shared';
import { Callout, Initial, PanelBlock, Pill } from './AgentPanelParts';

const toolGroups: Array<{ key: string; tools: string[] }> = [
  { key: 'files', tools: ['files_list', 'files_read', 'files_search', 'logs_collect', 'logs_search', 'files_write'] },
  { key: 'shell', tools: ['powershell', 'cmd', 'bash'] },
  { key: 'network', tools: ['http_request', 'workflow_run'] },
];
export const newAgent = (id: string, role: string, isSupervisor = false): AgentDefinition =>
  ({ id, role, instructions: '', isSupervisor, tools: [], skillIds: [] });

const stringOrNull = (value: unknown) => typeof value === 'string' && value ? value : null;

export function AiAgentConfig(props: Readonly<ConfigProps>) { return <AgentConfig {...props} team={false} />; }
export function AiAgentTeamConfig(props: Readonly<ConfigProps>) { return <AgentConfig {...props} team />; }

function AgentConfig({ config, onUpdate, upstreamVars = [], stepId = '', team }: ConfigProps & { team: boolean }) {
  const { t } = useTranslation('agents');
  const selection = useAgentSelectionStore();
  const binding = useContext(AgentAuthoringContext);
  const [draftOpen, setDraftOpen] = useState(false);
  const { data: capabilities } = useAiCapabilities();
  const { isViewer } = useRole();
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
  const addMember = () => {
    let number = 1;
    while (members.some(m => m.role === t('newMemberRole', { number }))) number++;
    const m = newAgent(randomUuid(), t('newMemberRole', { number }));
    onUpdate({ members: [...members, m] }); selection.select(stepId, m.id);
  };
  const machineName = (id: string | null | undefined) => id ? binding.machines.find(m => m.id === id)?.name ?? null : null;
  return <div className="space-y-5">
    <Callout title={t('authorizationTitle')}>{t('authorization')}</Callout>

    <PanelBlock title={t('task')} hint={t(team ? 'taskHintTeam' : 'taskHint')} divider={false}>
      <VariableInsertField label="" value={String(config.task ?? '')}
        onChange={task => onUpdate({ task })} upstreamVars={upstreamVars} multiline rows={5} />
    </PanelBlock>

    {team && <PanelBlock title={t('teamBlock')} hint={t('teamBlockHint')}
      action={members.length > 0 ? <span className="font-label text-[10px] text-on-surface-variant">{t('memberCount', { count: members.length })}</span> : undefined}>
      {capabilities?.llm === true && !isViewer && <div className="flex items-start gap-3 rounded-lg border border-primary/30 bg-primary/10 p-3">
        <span aria-hidden="true" className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary/20 text-primary"><MagicWandFilled size={16} /></span>
        <div className="min-w-0 flex-1 space-y-2">
          <p className="text-xs leading-snug text-on-surface-variant">{t('teamDraft.buttonHint')}</p>
          <button type="button" onClick={() => setDraftOpen(true)}
            className="inline-flex items-center gap-1.5 rounded-md bg-primary px-3 py-1.5 text-xs font-semibold text-on-primary shadow-sm transition hover:brightness-110">
            <MagicWandFilled size={12} aria-hidden="true" />{t('teamDraft.button')}</button>
        </div>
      </div>}
      {draftOpen && <AgentTeamDraftDialog config={config} machines={binding.machines} credentials={binding.credentials}
        step={{ machineId: stringOrNull(binding.data.targetMachineId), credentialId: stringOrNull(binding.data.credentialId) }}
        onApply={(patch, leadId) => { onUpdate(patch); if (leadId) selection.select(stepId, leadId); }}
        onClose={() => setDraftOpen(false)} />}

      {members.length > 0 && <div className="space-y-1.5">
        <label htmlFor={`team-lead-${stepId}`} className="block font-label text-xs font-semibold text-on-surface-variant">{t('teamLead')}</label>
        <select id={`team-lead-${stepId}`} className="input-field" value={supervisor?.id ?? ''}
          aria-describedby={`team-lead-hint-${stepId}`} onChange={e => {
            onUpdate({ members: members.map(m => ({ ...m, isSupervisor: m.id === e.target.value, isReviewer: m.id === e.target.value ? false : m.isReviewer })) });
            selection.select(stepId, e.target.value);
          }}>
          {!supervisor && <option value="" disabled>{t('choose')}</option>}
          {members.map(m => <option key={m.id} value={m.id}>{m.role}</option>)}
        </select>
        <p id={`team-lead-hint-${stepId}`} className="text-xs leading-snug text-on-surface-variant">{t('teamHint')}</p>
      </div>}

      <div className="space-y-1.5">
        <p className="font-label text-xs font-semibold text-on-surface-variant">{t('members')}</p>
        {members.length > 0 && <div className="grid gap-1.5" role="tablist" aria-label={t('members')}>
          {members.map(m => {
            const active = m.id === agent?.id;
            const toolCount = (m.tools ?? []).length;
            const machine = machineName(m.targetMachineId);
            const summary = [t(toolCount === 0 ? 'memberToolsNone' : 'memberTools', { count: toolCount }),
              m.useServiceIdentity ? t('memberServiceIdentity') : null, machine].filter(Boolean).join(' · ');
            return <button type="button" role="tab" aria-selected={active} key={m.id} aria-labelledby={`member-role-${stepId}-${m.id}`}
              className={`flex w-full items-center gap-3 rounded-lg border px-3 py-2 text-left transition-colors ${active
                ? 'border-primary bg-primary/10' : 'border-outline-variant/40 bg-surface-low hover:bg-surface-high'}`}
              onClick={() => selection.select(stepId, m.id)}>
              <Initial text={m.role} active={active} />
              <span className="min-w-0 flex-1">
                <span id={`member-role-${stepId}-${m.id}`} className="block truncate text-sm font-semibold text-on-surface">{m.role}</span>
                <span className="block truncate text-[11px] text-on-surface-variant">{summary}</span>
              </span>
              {m.isSupervisor && <Pill tone="primary">{t('teamLeadBadge')}</Pill>}
              {m.isReviewer && <Pill>{t('reviewer')}</Pill>}
            </button>;
          })}
        </div>}
        {members.length === 0
          ? <button type="button" className="flex w-full items-center justify-center gap-1.5 rounded-lg border border-dashed border-outline-variant px-3 py-3 text-sm text-primary hover:bg-surface-high"
            onClick={() => onUpdate({ members: [newAgent('supervisor', t('supervisor'), true), newAgent('specialist', t('newMemberRole', { number: 1 }))] })}>
            <Add size={14} aria-hidden="true" />{t('createTeam')}</button>
          : <button type="button" disabled={members.length >= 12} onClick={addMember}
            className="flex w-full items-center justify-center gap-1.5 rounded-lg border border-dashed border-outline-variant px-3 py-2 text-xs text-primary hover:bg-surface-high disabled:opacity-50">
            <Add size={14} aria-hidden="true" />{t('addMember')}</button>}
      </div>
    </PanelBlock>}

    {agent && <PanelBlock title={team ? t('memberSettings', { role: agent.role }) : t('agentBlock')}>
      <AgentMemberEditor key={agent.id} agent={agent} update={updateAgent} nested={team} upstreamVars={upstreamVars} />
      {team && <div className="flex flex-wrap items-center gap-3 border-t border-outline-variant/20 pt-3">
        <button type="button" className="text-xs text-error disabled:text-on-surface-variant disabled:opacity-50"
          disabled={!!removeHint} aria-describedby={removeHint ? `remove-member-hint-${stepId}` : undefined}
          onClick={() => {
            onUpdate({ members: members.filter(m => m.id !== agent.id) });
            selection.select(stepId, supervisor?.id ?? members.find(m => m.id !== agent.id)!.id);
          }}>{t('removeMember')}</button>
        {removeHint && <p id={`remove-member-hint-${stepId}`} className="min-w-0 flex-1 text-xs text-on-surface-variant">{t(removeHint)}</p>}
      </div>}
    </PanelBlock>}

    <PanelBlock title={t('resultBlock')} hint={t('resultBlockHint')}>
      <Field label={t('resultFormat')}><select className="input-field" value={String(config.resultFormat ?? 'text')}
        onChange={e => onUpdate({ resultFormat: e.target.value })}><option value="text">{t('text')}</option><option value="json">JSON</option></select></Field>
      {config.resultFormat === 'json' && <JsonSchemaField value={config.resultSchema} onChange={resultSchema => onUpdate({ resultSchema })} />}
      <details className="rounded-lg border border-outline-variant/30 px-3 py-2">
        <summary className="cursor-pointer text-xs font-semibold text-on-surface-variant">{t('budgets')}</summary>
        <div className="space-y-3 pt-2">
          <p className="text-xs leading-snug text-on-surface-variant">{t(team ? 'teamBudgetHint' : 'singleBudgetHint')}</p>
          {(team ? ['maxModelCalls', 'maxToolCalls', 'maxDelegations', 'maxParallelMembers'] : ['maxModelCalls', 'maxToolCalls']).map(key =>
            <Field key={key} label={t(key)}><input className="input-field" type="number" min={1}
              value={typeof config[key] === 'number' ? config[key] as number : ''}
              onChange={e => onUpdate({ [key]: e.target.value === '' ? undefined : Number(e.target.value) })} /></Field>)}
        </div>
      </details>
    </PanelBlock>
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

/** Sub-heading inside the member card; lighter than a PanelBlock title. */
function SubBlock({ title, hint, children }: Readonly<{ title: string; hint?: string; children: React.ReactNode }>) {
  return <div className="space-y-2.5">
    <div>
      <p className="font-label text-xs font-bold text-on-surface">{title}</p>
      {hint && <p className="mt-0.5 text-xs leading-snug text-on-surface-variant">{hint}</p>}
    </div>
    {children}
  </div>;
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
  return <div className="space-y-5 rounded-lg border border-outline-variant/30 bg-surface-container/30 p-3">
    <SubBlock title={t('roleBlock')}>
      {nested && <Field label={t('teamFunction')}>
        <select className="input-field" aria-label={t('teamFunction')} disabled={agent.isSupervisor}
          value={agent.isSupervisor ? 'supervisor' : agent.isReviewer ? 'reviewer' : 'specialist'}
          onChange={e => update({ isReviewer: e.target.value === 'reviewer' })}>
          {agent.isSupervisor && <option value="supervisor">{t('supervisor')}</option>}
          <option value="specialist">{t('specialist')}</option><option value="reviewer">{t('reviewer')}</option>
        </select>
        <p className="text-xs leading-snug text-on-surface-variant">{t('reviewerHint')}</p>
      </Field>}
      <Field label={t('role')}><input aria-label={t('role')} className="input-field" maxLength={128} value={agent.role} onChange={e => update({ role: e.target.value })} /></Field>
      {nested && <p className="text-xs leading-snug text-on-surface-variant">{t('roleHint')}</p>}
      <VariableInsertField label={t('instructions')} value={agent.instructions ?? ''} onChange={instructions => update({ instructions })}
        upstreamVars={upstreamVars} multiline rows={5}
        placeholder={t(nested && agent.isSupervisor ? 'supervisorInstructionsPlaceholder' : 'instructionsPlaceholder')} />
      <p className="text-xs leading-snug text-on-surface-variant">{t('instructionsHint')}</p>
      <Field label={t('model')}><input className="input-field" value={agent.model ?? ''} placeholder={t('activeProfile')}
        onChange={e => update({ model: e.target.value || undefined })} /></Field>
    </SubBlock>

    <SubBlock title={t('tools')} hint={nested && agent.isSupervisor ? t('delegationIncluded') : undefined}>
      <ToolEditor tools={tools} update={tools => update({ tools })} />
    </SubBlock>

    {needsTarget && <SubBlock title={t('targetBlock')} hint={t('targetBlockHint')}>
      <DynamicTargetField label={t('machine')} value={String(target.targetMachineId ?? '')}
        onChange={targetMachineId => updateTarget({ targetMachineId: targetMachineId || null })}
        options={binding.machines.map(m => ({ id: m.id, label: `${m.name} (${m.hostname})` }))}
        placeholder="GUID" upstreamVars={nested ? [] : upstreamVars} emptyLabel={t('localMachine')} optionPickerLabel={t('choose')} />
      <label className="flex gap-2 text-xs"><input type="checkbox" checked={agent.useServiceIdentity === true}
        onChange={e => update({ useServiceIdentity: e.target.checked })} />{t('serviceIdentity')}</label>
      <p className="text-xs leading-snug text-on-surface-variant">{t('identityHint')}</p>
      {!agent.useServiceIdentity && <DynamicTargetField label={t('credential')} value={String(target.credentialId ?? '')}
        onChange={credentialId => updateTarget({ credentialId: credentialId || null })}
        options={binding.credentials.map(c => ({ id: c.id, label: `${c.name} (${c.username})` }))}
        placeholder="GUID" upstreamVars={nested ? [] : upstreamVars} emptyLabel={t('credentialRequired')} optionPickerLabel={t('choose')} />}
      <Field label={t('workingDirectory')}><input className="input-field" value={agent.workingDirectory ?? ''} onChange={e => update({ workingDirectory: e.target.value || undefined })} /></Field>
      {tools.some(tool => tool.name === 'bash') && <Field label={t('bashPath')}><input className="input-field" value={agent.bashPath ?? ''} onChange={e => update({ bashPath: e.target.value || undefined })} /></Field>}
    </SubBlock>}

    <SubBlock title={t('skills')}>
      <div className="space-y-1">
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
      </div>
    </SubBlock>
  </div>;
}

function ToolEditor({ tools, update }: { tools: AgentToolSelection[]; update: (tools: AgentToolSelection[]) => void }) {
  const { t } = useTranslation('agents');
  const [serverId, setServerId] = useState('');
  const { data: servers = [], error } = useQuery({ queryKey: ['agent-mcp-servers'], queryFn: () => api.get<AgentMcpServer[]>('/agents/mcp-servers') });
  const { data: mcpTools = [], error: toolsError, isFetching } = useQuery({ queryKey: ['agent-mcp-tools', serverId], enabled: !!serverId,
    queryFn: () => api.get<AgentMcpTool[]>(`/agents/mcp-servers/${serverId}/tools`) });
  const change = (index: number, patch: Partial<AgentToolSelection>) => update(tools.map((tool, i) => i === index ? { ...tool, ...patch } : tool));
  const hasSettings = tools.some(tool => tool.name.startsWith('files_') || ['logs_collect', 'http_request', 'workflow_run', 'mcp'].includes(tool.name));
  return <div className="space-y-3">
    {toolGroups.map(group => <div key={group.key} className="space-y-1.5">
      <p className="font-label text-[11px] text-on-surface-variant">{t(`toolGroups.${group.key}`)}</p>
      <div className="grid grid-cols-1 gap-1.5 @[380px]:grid-cols-2">
        {group.tools.map(name => <label key={name} title={t(`toolHints.${name}`)}
          className="flex cursor-pointer items-center gap-2 rounded-md border border-outline-variant/40 bg-surface-low px-2 py-1.5 text-xs transition-colors hover:bg-surface-high has-[:checked]:border-primary has-[:checked]:bg-primary/10">
          <input type="checkbox" checked={tools.some(tool => tool.name === name)}
            onChange={e => update(e.target.checked ? [...tools, { name }] : tools.filter(tool => tool.name !== name))} />
          {t(`toolNames.${name}`)}</label>)}
      </div>
    </div>)}
    {tools.some(tool => ['powershell', 'cmd', 'bash'].includes(tool.name)) && <p className="text-xs leading-snug text-on-surface-variant">{t('shellHint')}</p>}
    {hasSettings && <div className="space-y-2 rounded-md border border-outline-variant/30 p-2">
      {tools.map((tool, index) => <div key={`${tool.name}-${tool.mcpServerId ?? index}-${tool.mcpToolName ?? ''}`}>
        {(tool.name.startsWith('files_') || tool.name === 'logs_collect') && <Lines label={`${t(`toolNames.${tool.name}`)}: ${t('allowedPaths')}`} value={tool.allowedPaths ?? []} onChange={allowedPaths => change(index, { allowedPaths })} />}
        {tool.name === 'http_request' && <Lines label={t('allowedHosts')} value={tool.allowedHosts ?? []} onChange={allowedHosts => change(index, { allowedHosts })} />}
        {tool.name === 'workflow_run' && <Lines label={t('workflowIds')} value={tool.workflowIds ?? []} onChange={workflowIds => change(index, { workflowIds })} />}
        {tool.name === 'mcp' && <div className="flex justify-between text-xs py-1"><span>{servers.find(s => s.id === tool.mcpServerId)?.name ?? tool.mcpServerId}: {tool.mcpToolName}</span>
          <button type="button" onClick={() => update(tools.filter((_, i) => i !== index))}>{t('remove')}</button></div>}
      </div>)}
    </div>}
    <div className="space-y-1.5">
      <p className="font-label text-[11px] text-on-surface-variant">{t('toolGroups.mcp')}</p>
      <select className="input-field" aria-label={t('mcpServer')} value={serverId} onChange={e => setServerId(e.target.value)}>
        <option value="">{t('mcpServer')}</option>{servers.filter(s => s.enabled).map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
      </select>
      {isFetching && <p className="text-xs">{t('loading')}</p>}
      {(error || toolsError) && <p role="alert" className="text-error text-xs">{(error || toolsError)?.message}</p>}
      {mcpTools.map(tool => <label key={tool.name} className="flex gap-2 text-xs py-1" title={tool.description ?? ''}>
        <input type="checkbox" checked={tools.some(selected => selected.mcpServerId === serverId && selected.mcpToolName === tool.name)}
          onChange={e => update(e.target.checked ? [...tools, { name: 'mcp', mcpServerId: serverId, mcpToolName: tool.name }]
            : tools.filter(selected => selected.mcpServerId !== serverId || selected.mcpToolName !== tool.name))} />{tool.name}</label>)}
    </div>
  </div>;
}

function Lines({ label, value, onChange }: { label: string; value: string[]; onChange: (values: string[]) => void }) {
  const [draft, setDraft] = useState(value.join('\n'));
  return <Field label={label}><textarea className="input-field font-mono" rows={2} value={draft}
    onChange={e => { setDraft(e.target.value); onChange(e.target.value.split(/\r?\n/).map(v => v.trim()).filter(Boolean)); }} /></Field>;
}
