import { useId, useState, type ReactNode } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import { useAuthStore } from '../../stores/authStore';
import { useSectionForm, ErrorsAndSave } from './SectionFormHelpers';
import type { AgentMcpServer, AgentMcpTool, AgentMcpReadGrant, AgentSkill } from '../../types/agents';
import { randomUuid } from '../../lib/uuid';
import { assertAuthBoundaryGenerationCurrent, captureAuthBoundaryGeneration } from '../../security/authBoundary';

const defaultLimits = {
  enabled: true, powerMode: false, allowServiceIdentity: false, maxConcurrentRuns: 2,
  singleModelCalls: 20, singleToolCalls: 40, singleTimeoutSeconds: 1200,
  teamModelCalls: 100, teamToolCalls: 500, teamDelegations: 20, teamMaxParallelMembers: 3, teamTimeoutSeconds: 1800,
  modelCallTimeoutSeconds: 180, modelMaxOutputTokens: 250000,
  maxContextCharacters: 250000, maxToolOutputCharacters: 16000, maxResultCharacters: 64000,
  readOnlyMcpTools: [] as AgentMcpReadGrant[],
};

const limitGroups = [
  { title: 'general', keys: ['enabled', 'allowServiceIdentity', 'maxConcurrentRuns'] },
  { title: 'single', keys: ['singleModelCalls', 'singleToolCalls', 'singleTimeoutSeconds'] },
  { title: 'team', keys: ['teamModelCalls', 'teamToolCalls', 'teamDelegations', 'teamMaxParallelMembers', 'teamTimeoutSeconds'] },
  { title: 'model', keys: ['modelCallTimeoutSeconds', 'modelMaxOutputTokens', 'maxContextCharacters', 'maxToolOutputCharacters', 'maxResultCharacters'] },
] as const;
const powerModeLimits = new Set<string>(['singleModelCalls', 'singleToolCalls', 'singleTimeoutSeconds',
  'teamModelCalls', 'teamToolCalls', 'teamDelegations', 'teamTimeoutSeconds']);

function RegistryList({ title, query, onQuery, total, count, loading, failed, children }: {
  title: string; query: string; onQuery: (query: string) => void; total: number; count: number;
  loading: boolean; failed: boolean; children: ReactNode;
}) {
  const { t } = useTranslation('agents');
  return <>
    <div className="flex flex-wrap items-center justify-between gap-3">
      <h3 className="font-semibold">{title} <span className="text-xs font-normal text-on-surface-variant">({total})</span></h3>
      <input type="search" className="input-field w-full sm:!w-64" aria-label={t('registry.search', { title })}
        placeholder={t('registry.search', { title })} value={query} onChange={e => onQuery(e.target.value)} />
    </div>
    {loading ? <p role="status" className="text-sm text-on-surface-variant">{t('loading')}</p> : !failed && <>
      {query.trim() && <p role="status" className="text-xs text-on-surface-variant">{t('registry.matches', { count, total })}</p>}
      {count === 0 ? <p className="py-4 text-sm text-on-surface-variant">{t(total ? 'registry.noMatches' : 'registry.empty')}</p>
        : <ul aria-label={title} tabIndex={0} className="max-h-80 overflow-y-auto overscroll-contain divide-y divide-outline-variant rounded border border-outline-variant focus-visible:outline-2 focus-visible:outline-primary">{children}</ul>}
    </>}
  </>;
}

function RegistryDetails({ children }: { children: ReactNode }) {
  const { t } = useTranslation('agents');
  const [open, setOpen] = useState(false);
  const id = useId();
  return <div className="mt-1">
    <button type="button" className="text-xs text-primary" aria-expanded={open} aria-controls={id}
      onClick={() => setOpen(!open)}>{t(open ? 'registry.hideDetails' : 'registry.details')}</button>
    <div id={id} hidden={!open} className="mt-2 space-y-2 break-words text-xs text-on-surface-variant">{children}</div>
  </div>;
}

export function AgentsSection() {
  const { t } = useTranslation('agents');
  const admin = useAuthStore(s => s.role === 'Admin');
  return <div className="space-y-5">
    <p className="text-sm text-on-surface-variant">{t('registryHint')}</p>
    {admin && <LimitsCard />}
    <McpServersCard admin={admin} />
    <SkillsCard admin={admin} />
  </div>;
}

function LimitsCard() {
  const { t } = useTranslation('agents');
  const section = useSectionForm<typeof defaultLimits>('Agents', defaultLimits);
  if (section.loading) return <p>{t('loading')}</p>;
  return <section className="np-card p-5 space-y-4"><h3 className="font-semibold">{t('limits')}</h3>
    <p className="text-xs text-on-surface-variant">{t('restartHint')}</p>
    <div className="rounded border border-outline-variant bg-surface-low p-4 space-y-2">
      <button type="button" className="flex w-full items-center justify-between gap-4 text-left font-semibold disabled:opacity-50" role="switch" aria-checked={section.form.powerMode ?? false}
          aria-describedby="agent-power-mode-hint" disabled={section.isEnvLocked('PowerMode')}
          onClick={() => section.set({ ...section.form, powerMode: !section.form.powerMode })}>
        {t('powerMode')}
        <span aria-hidden="true" className={`relative h-6 w-11 shrink-0 rounded-full transition-colors ${section.form.powerMode ? 'bg-primary' : 'bg-outline-variant'}`}>
          <span className={`absolute top-1 h-4 w-4 rounded-full bg-surface-lowest shadow-sm transition-all ${section.form.powerMode ? 'left-6' : 'left-1'}`} />
        </span>
      </button>
      <p id="agent-power-mode-hint" className="text-xs text-on-surface-variant">{t('limitHelp.powerMode')}</p>
    </div>
    {limitGroups.map(group => <fieldset key={group.title} className="border-t border-outline-variant pt-4">
      <legend className="pr-3 text-sm font-semibold">{t(`limitGroups.${group.title}`)}</legend>
      <div className="grid gap-x-6 gap-y-4 md:grid-cols-2">{group.keys.map(key => {
        const value = section.form[key] ?? defaultLimits[key];
        const unlimited = section.form.powerMode && powerModeLimits.has(key);
        const disabled = unlimited || section.isEnvLocked(key[0].toUpperCase() + key.slice(1));
        const id = `agent-limit-${key}`;
        return <div key={key} className="min-w-0 space-y-1">
          <label htmlFor={id} className="block text-sm">{t(key)}</label>
          {typeof value === 'boolean' ? <input id={id} type="checkbox" checked={value} disabled={disabled} aria-describedby={`${id}-hint`}
            onChange={e => section.set({ ...section.form, [key]: e.target.checked })} />
            : <input id={id} className="input-field" type="number" min={1} value={value} disabled={disabled} aria-describedby={`${id}-hint`}
              onChange={e => section.set({ ...section.form, [key]: Number(e.target.value) })} />}
          <p id={`${id}-hint`} className="text-xs text-on-surface-variant">{t(`limitHelp.${key}`)}
            {unlimited && <span className="block text-primary">{t('unlimitedInPowerMode')}</span>}
          </p>
        </div>;
      })}</div>
    </fieldset>)}
    <McpReadApprovals grants={section.form.readOnlyMcpTools ?? []} locked={section.isEnvLocked('ReadOnlyMcpTools')}
      onChange={readOnlyMcpTools => section.set({ ...section.form, readOnlyMcpTools })} />
    <ErrorsAndSave errors={section.errors} onSave={() => section.save(Object.fromEntries(Object.entries(section.form).map(([key, value]) => [key[0].toUpperCase() + key.slice(1), value])))} />
    {section.dialog}
  </section>;
}

function McpReadApprovals({ grants, locked, onChange }: { grants: AgentMcpReadGrant[]; locked: boolean; onChange: (value: AgentMcpReadGrant[]) => void }) {
  const { t } = useTranslation('agents');
  const [serverId, setServerId] = useState('');
  const { data: servers = [] } = useQuery({ queryKey: ['agent-mcp-servers'], queryFn: () => api.get<AgentMcpServer[]>('/agents/mcp-servers') });
  const server = servers.find(s => s.id === serverId);
  const { data: tools = [], error, isFetching } = useQuery({ queryKey: ['agent-read-tools', serverId, server?.updatedAt],
    queryFn: () => api.get<AgentMcpTool[]>(`/agents/mcp-servers/${serverId}/tools`), enabled: !!server?.enabled });
  return <div className="space-y-3 border-t border-outline-variant pt-4">
    <h4 className="font-semibold">{t('readOnlyMcpTools')}</h4><p className="text-xs text-on-surface-variant">{t('mcpReadApprovalHint')}</p>
    <select aria-label={t('mcpServer')} className="input-field" value={serverId} onChange={e => setServerId(e.target.value)}>
      <option value="">{t('mcpServer')}</option>{servers.filter(s => s.enabled).map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
    </select>
    {isFetching && <p>{t('loading')}</p>}{error && <p role="alert" className="text-error">{error.message}</p>}
    {server && tools.length > 0 && <div role="group" aria-label={t('mcpServer')} tabIndex={0} className="max-h-80 overflow-y-auto space-y-3 break-words">{tools.map(tool => <label key={tool.name} className="block text-sm"><input type="checkbox" disabled={locked || !tool.readOnly}
      checked={grants.some(g => g.serverId === server.id && g.toolName === tool.name && g.serverUpdatedAt === server.updatedAt && g.contractSha256 === tool.contractSha256)}
      onChange={e => { const remaining = grants.filter(g => g.serverId !== server.id || g.toolName !== tool.name);
        onChange(e.target.checked ? [...remaining, { serverId: server.id, toolName: tool.name, serverUpdatedAt: server.updatedAt, contractSha256: tool.contractSha256 }] : remaining); }} />
      {' '}{tool.name}{!tool.readOnly && ` — ${t('mcpNotReadOnly')}`}<span className="block text-xs text-on-surface-variant">{tool.description}</span>
    </label>)}</div>}
    {grants.length > 0 && <div role="group" aria-label={t('readOnlyMcpTools')} tabIndex={0} className="max-h-80 overflow-y-auto space-y-3">{grants.map(g => <div className="flex justify-between gap-3 text-xs" key={`${g.serverId}/${g.toolName}`}>
      <span className="min-w-0 break-words">{servers.find(s => s.id === g.serverId)?.name ?? g.serverId} · {g.toolName}</span>
      <button type="button" disabled={locked} onClick={() => onChange(grants.filter(x => x !== g))}>{t('remove')}</button>
    </div>)}</div>}
  </div>;
}

function McpServersCard({ admin }: { admin: boolean }) {
  const { t } = useTranslation('agents');
  const client = useQueryClient();
  const { data: servers = [], error, isPending } = useQuery({ queryKey: ['agent-mcp-servers'], queryFn: () => api.get<AgentMcpServer[]>('/agents/mcp-servers') });
  const [query, setQuery] = useState('');
  const filtered = servers.filter(server => `${server.name} ${server.id} ${server.transport}`.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()));
  const [editing, setEditing] = useState<AgentMcpServer | null>(null);
  const mutation = useMutation({ mutationFn: (action: { kind: 'delete' | 'toggle'; server: AgentMcpServer }) => {
    const server = action.server;
    if (action.kind === 'delete') return api.delete(`/agents/mcp-servers/${server.id}`);
    return api.put(`/agents/mcp-servers/${server.id}`, {
      name: server.name, enabled: !server.enabled, transport: server.transport,
      command: server.command, arguments: server.arguments, endpoint: server.endpoint, updatedAt: server.updatedAt,
    });
  }, onSettled: () => client.invalidateQueries({ queryKey: ['agent-mcp-servers'] }) });
  return <section className="np-card p-5 space-y-4">
    {(error || mutation.error) && <p role="alert" className="text-error text-sm">{(error || mutation.error)?.message}</p>}
    <RegistryList title={t('mcpServers')} query={query} onQuery={setQuery} total={servers.length} count={filtered.length} loading={isPending} failed={!!error}>
      {filtered.map(server => <li key={server.id} className="px-3 py-3 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-3">
      <div className="min-w-0 break-words"><strong>{server.name}</strong><div className="text-xs text-on-surface-variant">{server.transport} · {t(server.enabled ? 'enabled' : 'disabled')}</div></div>
      {admin && <div className="flex flex-wrap gap-3">
        <label className="flex gap-2"><input type="checkbox" checked={server.enabled} disabled={mutation.isPending || editing?.id === server.id}
          onChange={() => mutation.mutate({ kind: 'toggle', server })} />{t('enabled')}</label>
        <button type="button" className="text-primary" disabled={mutation.isPending} onClick={() => setEditing(server)}>{t('edit')}</button>
        <button type="button" disabled={mutation.isPending} onClick={() => { if (globalThis.confirm(t('deleteConfirm'))) mutation.mutate({ kind: 'delete', server }); }}>{t('delete')}</button></div>}
      </div><RegistryDetails><code className="break-all">ID: {server.id}</code></RegistryDetails>
    </li>)}</RegistryList>
    {admin && <button type="button" className="text-primary text-sm" onClick={() => setEditing({ id: randomUuid(), name: '', enabled: true,
      transport: 'stdio', command: null, arguments: [], endpoint: null, hasSecrets: false, updatedAt: '' })}>{t('newServer')}</button>}
    {editing && <ServerEditor key={editing.id} initial={editing} onClose={() => setEditing(null)} />}
  </section>;
}

function ServerEditor({ initial, onClose }: { initial: AgentMcpServer; onClose: () => void }) {
  const { t } = useTranslation('agents');
  const client = useQueryClient();
  const [form, setForm] = useState(initial);
  const [args, setArgs] = useState(initial.arguments.join('\n'));
  const [secrets, setSecrets] = useState('');
  const save = useMutation({ mutationFn: () => {
    const parsed: unknown = secrets.trim() ? JSON.parse(secrets) : undefined;
    if (parsed !== undefined && (!parsed || Array.isArray(parsed) || typeof parsed !== 'object' || Object.values(parsed).some(value => typeof value !== 'string')))
      throw new Error(t('invalidJson'));
    return api.put(`/agents/mcp-servers/${form.id}`, { name: form.name, enabled: form.enabled, transport: form.transport,
      command: form.transport === 'stdio' ? form.command : null, endpoint: form.transport === 'streamableHttp' ? form.endpoint : null,
      arguments: args.split(/\r?\n/).filter(Boolean), secrets: parsed, updatedAt: form.updatedAt || undefined });
  }, onSuccess: async () => { setSecrets(''); await client.invalidateQueries({ queryKey: ['agent-mcp-servers'] }); onClose(); } });
  return <form className="border-t border-outline-variant pt-4 space-y-3" onSubmit={e => { e.preventDefault(); save.mutate(); }}>
    <label className="block text-sm">{t('name')}<input className="input-field" required maxLength={128} value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} /></label>
    <label className="block text-sm">{t('transport')}<select className="input-field" value={form.transport} onChange={e => setForm({ ...form, transport: e.target.value as AgentMcpServer['transport'] })}>
      <option value="stdio">stdio</option><option value="streamableHttp">Streamable HTTP</option></select></label>
    {form.transport === 'stdio' ? <>
      <label className="block text-sm">{t('command')}<input className="input-field font-mono" required value={form.command ?? ''} onChange={e => setForm({ ...form, command: e.target.value })} /></label>
      <label className="block text-sm">{t('arguments')}<textarea className="input-field font-mono" rows={3} value={args} onChange={e => setArgs(e.target.value)} /></label>
    </> : <label className="block text-sm">{t('endpoint')}<input className="input-field" type="url" required value={form.endpoint ?? ''} onChange={e => setForm({ ...form, endpoint: e.target.value })} /></label>}
    <label className="block text-sm">{t('secrets')}<input type="password" className="input-field font-mono" autoComplete="new-password" spellCheck={false} value={secrets} onChange={e => setSecrets(e.target.value)} /></label>
    <p className="text-xs text-on-surface-variant">{t('secretsHint')}</p>
    <label className="flex gap-2 text-sm"><input type="checkbox" checked={form.enabled} onChange={e => setForm({ ...form, enabled: e.target.checked })} />{t('enabled')}</label>
    {save.error && <p role="alert" className="text-error text-sm">{save.error.message}</p>}
    <div className="flex gap-4"><button type="submit" className="text-primary" disabled={save.isPending}>{t('save')}</button><button type="button" onClick={onClose}>{t('cancel')}</button></div>
  </form>;
}

function SkillsCard({ admin }: { admin: boolean }) {
  const { t } = useTranslation('agents');
  const client = useQueryClient();
  const { data: skills = [], error, isPending } = useQuery({ queryKey: ['agent-skills'], queryFn: () => api.get<AgentSkill[]>('/agents/skills') });
  const [query, setQuery] = useState('');
  const filtered = skills.filter(skill => `${skill.name} ${skill.version} ${skill.description}`.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()));
  const [version, setVersion] = useState('1.0.0');
  const [file, setFile] = useState<File | null>(null);
  const [importing, setImporting] = useState(false);
  const mutate = useMutation({ mutationFn: async (action: { kind: 'import' } | { kind: 'delete' | 'toggle'; skill: AgentSkill }) => {
    if (action.kind === 'delete') return api.delete(`/agents/skills/${action.skill.id}`);
    if (action.kind === 'toggle') return api.put(`/agents/skills/${action.skill.id}/enabled`, { enabled: !action.skill.enabled });
    if (!file || file.size > 10_000_000) throw new Error(t('package'));
    const authGeneration = captureAuthBoundaryGeneration();
    const bytes = new Uint8Array(await file.arrayBuffer());
    assertAuthBoundaryGenerationCurrent(authGeneration);
    let binary = '';
    for (let start = 0; start < bytes.length; start += 32768) binary += String.fromCharCode(...bytes.subarray(start, start + 32768));
    return api.post('/agents/skills', { version, package: btoa(binary) });
  }, onSuccess: async (_, action) => {
    await client.invalidateQueries({ queryKey: ['agent-skills'] });
    if (action.kind === 'import') { setImporting(false); setFile(null); setVersion('1.0.0'); }
  } });
  return <section className="np-card p-5 space-y-4">
    {(error || mutate.error) && <p role="alert" className="text-error text-sm">{(error || mutate.error)?.message}</p>}
    <RegistryList title={t('skills')} query={query} onQuery={setQuery} total={skills.length} count={filtered.length} loading={isPending} failed={!!error}>
      {filtered.map(skill => <li key={skill.id} className="px-3 py-3 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-3"><div className="min-w-0 break-words"><strong>{skill.name}</strong><div className="text-xs text-on-surface-variant">{skill.version} · {t(skill.enabled ? 'enabled' : 'disabled')}</div></div>
        {admin && <div className="flex flex-wrap gap-3"><label className="flex gap-2"><input type="checkbox" checked={skill.enabled} disabled={mutate.isPending} onChange={() => mutate.mutate({ kind: 'toggle', skill })} />{t('enabled')}</label>
          <button type="button" disabled={mutate.isPending} onClick={() => { if (globalThis.confirm(t('deleteConfirm'))) mutate.mutate({ kind: 'delete', skill }); }}>{t('delete')}</button></div>}</div>
      <RegistryDetails><p>{skill.description}</p><code className="break-all">SHA-256: {skill.sha256}</code></RegistryDetails>
    </li>)}</RegistryList>
    {admin && !importing && <button type="button" className="text-primary text-sm" onClick={() => { mutate.reset(); setImporting(true); }}>{t('importSkill')}</button>}
    {admin && importing && <form aria-label={t('importSkill')} className="space-y-3 border-t border-outline-variant pt-4" onSubmit={e => { e.preventDefault(); mutate.mutate({ kind: 'import' }); }}>
      <h4 className="text-sm font-semibold">{t('importSkill')}</h4>
      <label className="block text-sm">{t('package')}<input autoFocus type="file" accept=".zip" required disabled={mutate.isPending} className="input-field" aria-describedby="agent-skill-import-hint" onChange={e => setFile(e.target.files?.[0] ?? null)} /></label>
      <p id="agent-skill-import-hint" className="text-xs text-on-surface-variant">{t(file && file.size > 10_000_000 ? 'registry.packageTooLarge' : 'registry.importHint')}</p>
      <label className="block text-sm">{t('version')}<input className="input-field" required disabled={mutate.isPending} maxLength={64} pattern="[a-zA-Z0-9._-]+" value={version} onChange={e => setVersion(e.target.value)} /></label>
      <div className="flex gap-4 text-sm">
        <button type="submit" className="text-primary disabled:opacity-50 disabled:cursor-not-allowed" disabled={mutate.isPending || !file || file.size > 10_000_000}>{t('registry.import')}</button>
        <button type="button" disabled={mutate.isPending} onClick={() => { setImporting(false); setFile(null); setVersion('1.0.0'); mutate.reset(); }}>{t('cancel')}</button>
      </div>
    </form>}
  </section>;
}
