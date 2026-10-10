import { CircleDash, Close, MagicWandFilled } from '@carbon/icons-react';
import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { aiApi, type AgentTeamIssue, type GenerateAgentTeamResponse } from '../../../api/ai';
import {
  applyResolutions, canPick, draftPatch, effectiveBinding, isTerminal, issueKey, needsTarget, openBlockingIssues,
  type IssueResolution,
} from '../../../lib/agentTeamDraft';
import { projectAgentMembers } from '../../../lib/agentTeamProjection';
import type { AgentDefinition, AgentToolSelection } from '../../../types/agents';
import type { Credential, MachineOption } from '../../../types/api';

interface Props {
  /** The node's current config; the server merges the draft onto it and validates the result. */
  config: Record<string, unknown>;
  /** Step-level target, inherited by members that name none. */
  step: { machineId?: string | null; credentialId?: string | null };
  machines: MachineOption[];
  credentials: Credential[];
  /** Receives the config patch and the id of the team lead, so the editor can select it. */
  onApply: (patch: Record<string, unknown>, leadId: string | undefined) => void;
  onClose: () => void;
}

/**
 * Two stages: describe the team, then review the draft. The review shows what each member would
 * really run with (including inherited targets) and holds the Apply button back until every
 * blocking issue is settled. Nothing is saved or published here; Apply only edits the node.
 */
export function AgentTeamDraftDialog({ config, step, machines, credentials, onApply, onClose }: Readonly<Props>) {
  const { t } = useTranslation(['agents', 'common']);
  const [prompt, setPrompt] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<GenerateAgentTeamResponse | null>(null);
  const [resolutions, setResolutions] = useState<Record<string, IssueResolution>>({});
  const [replace, setReplace] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const existing = projectAgentMembers(config).length;

  useEffect(() => { textareaRef.current?.focus(); }, []);

  const generate = async () => {
    const text = prompt.trim();
    if (!text || busy) return;
    setBusy(true);
    setError(null);
    try {
      setDraft(await aiApi.generateAgentTeam({ prompt: text, currentConfig: config }));
      setResolutions({});
      setReplace(false);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  const open = draft ? openBlockingIssues(draft.issues, resolutions) : [];
  const canApply = !!draft && open.length === 0 && (existing === 0 || replace);
  const apply = () => {
    if (!draft || !canApply) return;
    const members = applyResolutions(draft.patch.members, draft.issues, resolutions);
    onApply(draftPatch(draft, members), members.find(m => m.isSupervisor)?.id);
    onClose();
  };

  return createPortal(
    <div className="fixed inset-0 z-[60] bg-black/30 backdrop-blur-sm flex items-center justify-center p-4" role="dialog"
      aria-modal="true" aria-labelledby="agent-team-draft-title"
      onKeyDown={e => { if (e.key === 'Escape' && !busy) onClose(); }}>
      <div className="bg-surface-lowest rounded-xl shadow-2xl ring-1 ring-outline-variant/20 w-full max-w-3xl max-h-[90vh] flex flex-col overflow-hidden">
        <div className="flex items-center justify-between px-4 py-3 bg-surface-low border-b border-outline-variant/20">
          <div className="flex items-center gap-2">
            <MagicWandFilled size={16} className="text-primary" />
            <span id="agent-team-draft-title" className="text-sm font-headline font-bold text-on-surface">
              {t(draft ? 'agents:teamDraft.reviewTitle' : 'agents:teamDraft.title')}
            </span>
          </div>
          <button type="button" onClick={onClose} disabled={busy} aria-label={t('common:close')}
            className="p-1 text-on-surface-variant hover:text-error rounded disabled:opacity-40"><Close size={14} /></button>
        </div>

        <div className="flex-1 overflow-y-auto px-4 py-4 space-y-3">
          {!draft && <>
            <p className="text-xs text-on-surface-variant">{t('agents:teamDraft.intro')}</p>
            <textarea ref={textareaRef} value={prompt} onChange={e => setPrompt(e.target.value)} rows={8} maxLength={8000}
              disabled={busy} aria-label={t('agents:teamDraft.promptLabel')} placeholder={t('agents:teamDraft.placeholder')}
              className="w-full input-field text-sm resize-y" />
            <p className="text-[10px] text-on-surface-variant">{t('agents:teamDraft.dataNote')}</p>
          </>}
          {draft && <DraftReview draft={draft} config={config} step={step} machines={machines} credentials={credentials}
            resolutions={resolutions} onResolve={(key, r) => setResolutions(prev => ({ ...prev, [key]: r }))} />}
          {error && <div role="alert" className="bg-error-container/20 border border-error/30 rounded px-2 py-1.5 text-xs whitespace-pre-wrap">{error}</div>}
        </div>

        <div className="flex items-center justify-end gap-3 px-4 py-3 bg-surface-low border-t border-outline-variant/20">
          {draft && existing > 0 && <label className="flex items-center gap-2 text-xs mr-auto">
            <input type="checkbox" checked={replace} onChange={e => setReplace(e.target.checked)} />
            {t('agents:teamDraft.replaceExisting', { count: existing })}</label>}
          {draft && open.length > 0 && <span className="text-xs text-error">{t('agents:teamDraft.openIssues', { count: open.length })}</span>}
          {draft && <button type="button" className="px-3 py-1.5 text-xs text-on-surface-variant" disabled={busy}
            onClick={() => { setDraft(null); setError(null); }}>{t('agents:teamDraft.back')}</button>}
          <button type="button" className="px-3 py-1.5 text-xs text-on-surface-variant" disabled={busy} onClick={onClose}>{t('common:cancel')}</button>
          {!draft && <button type="button" onClick={generate} disabled={busy || prompt.trim().length === 0}
            className="flex items-center gap-1.5 px-4 py-1.5 bg-gradient-to-br from-primary to-primary-container text-on-primary text-xs font-semibold rounded-md disabled:opacity-50">
            {busy ? <CircleDash size={12} className="animate-spin" /> : <MagicWandFilled size={12} />}
            {t(busy ? 'agents:teamDraft.generating' : 'agents:teamDraft.generate')}</button>}
          {draft && <button type="button" onClick={apply} disabled={!canApply}
            className="px-4 py-1.5 bg-gradient-to-br from-primary to-primary-container text-on-primary text-xs font-semibold rounded-md disabled:opacity-50">
            {t('agents:teamDraft.apply')}</button>}
        </div>
      </div>
    </div>, document.body);
}

function DraftReview({ draft, config, step, machines, credentials, resolutions, onResolve }: Readonly<{
  draft: GenerateAgentTeamResponse; config: Record<string, unknown>; step: Props['step']; machines: MachineOption[];
  credentials: Credential[]; resolutions: Record<string, IssueResolution>; onResolve: (key: string, r: IssueResolution) => void;
}>) {
  const { t } = useTranslation('agents');
  const members = applyResolutions(draft.patch.members, draft.issues, resolutions);
  const teamIssues = draft.issues.filter(i => i.field === 'team');
  return <div className="space-y-3">
    <p className="text-xs text-on-surface-variant">{t('teamDraft.reviewHint')}</p>
    {teamIssues.map(i => <p key={i.code} role="alert" className="text-xs text-error">{i.message}</p>)}
    <div className="rounded-lg border border-outline-variant p-3">
      <p className="text-xs font-semibold">{t('task')}</p>
      <p className="text-xs whitespace-pre-wrap">{draft.patch.task}</p>
      <p className="text-[10px] text-on-surface-variant mt-1">
        {t(String(config.task ?? '').trim() ? 'teamDraft.taskKept' : 'teamDraft.taskSet')}
        {draft.patch.maxParallelMembers != null && ` · ${t('maxParallelMembers')}: ${draft.patch.maxParallelMembers}`}</p>
    </div>
    {members.map(member => <MemberCard key={member.id} member={member} draft={draft} step={step} machines={machines}
      credentials={credentials} resolutions={resolutions} onResolve={onResolve} />)}
    <p className="text-[10px] text-on-surface-variant">
      {t('teamDraft.model')} <code>{draft.model}</code> · {t('teamDraft.duration', { ms: draft.durationMs })}</p>
  </div>;
}

function MemberCard({ member, draft, step, machines, credentials, resolutions, onResolve }: Readonly<{
  member: AgentDefinition; draft: GenerateAgentTeamResponse; step: Props['step']; machines: MachineOption[];
  credentials: Credential[]; resolutions: Record<string, IssueResolution>; onResolve: (key: string, r: IssueResolution) => void;
}>) {
  const { t } = useTranslation('agents');
  const issues = draft.issues.filter(i => i.memberId === member.id);
  const blocking = issues.filter(i => i.severity === 'blocking');
  const warnings = issues.filter(i => i.severity === 'warning');
  const binding = effectiveBinding(member, step, machines, credentials, draft.names);
  const showBinding = needsTarget(member) || !!member.targetMachineId || !!member.credentialId || member.useServiceIdentity;
  return <section className="rounded-lg border border-outline-variant p-3 space-y-2" aria-label={member.role}>
    <p className="text-sm font-semibold">{member.role}
      {member.isSupervisor && <span className="ml-2 text-[10px]">{t('teamLeadBadge')}</span>}
      {member.isReviewer && <span className="ml-2 text-[10px]">{t('reviewer')}</span>}</p>
    {member.instructions && <details><summary className="text-xs cursor-pointer">{t('instructions')}</summary>
      <p className="text-xs whitespace-pre-wrap mt-1">{member.instructions}</p></details>}

    {showBinding && <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
      <dt className="font-semibold">{t('machine')}</dt>
      <dd>{binding.machine.origin === 'localhost' ? t('teamDraft.localhost') : binding.machine.label}
        <span className="text-on-surface-variant"> · {t(`teamDraft.origin.${binding.machine.origin}`)}</span></dd>
      <dt className="font-semibold">{t('credential')}</dt>
      <dd>{member.useServiceIdentity ? <strong className="text-error">{t('teamDraft.serviceIdentityOn')}</strong>
        : binding.credential.label ?? <span className="text-error">{t('teamDraft.noCredential')}</span>}
        {!member.useServiceIdentity && binding.credential.origin !== 'none' &&
          <span className="text-on-surface-variant"> · {t(`teamDraft.credOrigin.${binding.credential.origin}`)}</span>}</dd>
    </dl>}

    {(member.tools ?? []).length > 0 && <ul className="text-xs space-y-1">
      {(member.tools ?? []).map(tool => <li key={`${tool.name}-${tool.mcpServerId ?? ''}-${tool.mcpToolName ?? ''}`}>
        <ToolLine tool={tool} names={draft.names} /></li>)}
    </ul>}
    {(member.skillIds ?? []).length > 0 && <p className="text-xs"><span className="font-semibold">{t('skills')}: </span>
      {member.skillIds.map(id => draft.names[id] ?? id).join(', ')}</p>}

    {blocking.map(issue => <BlockingIssue key={issueKey(issue) + issue.code} issue={issue}
      resolution={resolutions[issueKey(issue)]} onResolve={r => onResolve(issueKey(issue), r)} />)}
    {warnings.length > 0 && <ul className="text-xs text-amber-700 space-y-0.5">
      {warnings.map((w, i) => <li key={`${w.code}-${i}`}>{w.message}</li>)}</ul>}
  </section>;
}

function ToolLine({ tool, names }: Readonly<{ tool: AgentToolSelection; names: Record<string, string> }>) {
  const { t } = useTranslation('agents');
  const name = tool.name === 'mcp'
    ? `${t('toolNames.mcp', { defaultValue: 'MCP' })}: ${names[tool.mcpServerId ?? ''] ?? tool.mcpServerId} / ${tool.mcpToolName}`
    : t(`toolNames.${tool.name}`);
  return <>
    <span className="font-semibold">{name}</span>
    {(tool.allowedPaths ?? []).length > 0 && <span className="font-mono"> · {tool.allowedPaths!.join(', ')}</span>}
    {tool.name === 'http_request' && ((tool.allowedHosts ?? []).length > 0
      ? <span className="font-mono"> · {tool.allowedHosts!.join(', ')}</span>
      : <strong className="text-error"> · {t('teamDraft.allHosts')}</strong>)}
    {(tool.workflowIds ?? []).length > 0 && <span> · {tool.workflowIds!.map(id => names[id] ?? id).join(', ')}</span>}
  </>;
}

function BlockingIssue({ issue, resolution, onResolve }: Readonly<{
  issue: AgentTeamIssue; resolution: IssueResolution | undefined; onResolve: (r: IssueResolution) => void;
}>) {
  const { t } = useTranslation('agents');
  const id = `issue-${issueKey(issue)}`;
  if (isTerminal(issue)) return <p role="alert" className="text-xs text-error">{issue.message}</p>;
  const value = !resolution ? '' : resolution.kind === 'discard' ? 'discard' : `pick:${resolution.id}`;
  return <div className="rounded border border-error/40 bg-error-container/10 p-2 space-y-1">
    <label htmlFor={id} className="text-xs text-error block">{issue.message}</label>
    <select id={id} className="input-field text-xs" value={value} onChange={e => {
      const v = e.target.value;
      if (v === 'discard') onResolve({ kind: 'discard' });
      else if (v.startsWith('pick:')) onResolve({ kind: 'pick', id: v.slice(5) });
    }}>
      <option value="" disabled>{t('teamDraft.resolve')}</option>
      {canPick(issue) && issue.candidates.map(c => <option key={c.id} value={`pick:${c.id}`}>{c.detail ? `${c.name} (${c.detail})` : c.name}</option>)}
      <option value="discard">{t(`teamDraft.discard.${issue.field}`)}</option>
    </select>
  </div>;
}
