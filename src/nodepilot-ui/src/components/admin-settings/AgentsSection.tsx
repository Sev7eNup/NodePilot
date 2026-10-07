import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import { useAuthStore } from '../../stores/authStore';
import { useSectionForm, ErrorsAndSave } from './SectionFormHelpers';
import type { AgentMcpServer, AgentMcpTool, AgentMcpReadGrant, AgentSkill } from '../../types/agents';
import { randomUuid } from '../../lib/uuid';

const defaultLimits = {
  enabled: true, allowServiceIdentity: false, maxConcurrentRuns: 2,
  singleModelCalls: 20, singleToolCalls: 40, singleTimeoutSeconds: 1200,
  teamModelCalls: 100, teamToolCalls: 500, teamDelegations: 20, teamMaxParallelMembers: 3, teamTimeoutSeconds: 1800,
  modelCallTimeoutSeconds: 180, modelMaxOutputTokens: 250000,
  maxContextCharacters: 250000, maxToolOutputCharacters: 16000, maxResultCharacters: 64000,
  readOnlyMcpTools: [] as AgentMcpReadGrant[],
};

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
    <div className="grid gap-3 md:grid-cols-2">{Object.entries(section.form).filter((entry): entry is [string, number | boolean] => typeof entry[1] === 'number' || typeof entry[1] === 'boolean').map(([key, value]) => <label key={key} className="text-sm space-y-1">
      <span className="block">{t(key)}</span>
      {typeof value === 'boolean' ? <input type="checkbox" checked={value} disabled={section.isEnvLocked(key[0].toUpperCase() + key.slice(1))}
        onChange={e => section.set({ ...section.form, [key]: e.target.checked })} />
        : <input className="input-field" type="number" min={1} value={value} disabled={section.isEnvLocked(key[0].toUpperCase() + key.slice(1))}
          onChange={e => section.set({ ...section.form, [key]: Number(e.target.value) })} />}
    </label>)}</div>
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
    {server && tools.map(tool => <label key={tool.name} className="block text-sm"><input type="checkbox" disabled={locked || !tool.readOnly}
      checked={grants.some(g => g.serverId === server.id && g.toolName === tool.name && g.serverUpdatedAt === server.updatedAt && g.contractSha256 === tool.contractSha256)}
      onChange={e => { const remaining = grants.filter(g => g.serverId !== server.id || g.toolName !== tool.name);
        onChange(e.target.checked ? [...remaining, { serverId: server.id, toolName: tool.name, serverUpdatedAt: server.updatedAt, contractSha256: tool.contractSha256 }] : remaining); }} />
      {' '}{tool.name}{!tool.readOnly && ` — ${t('mcpNotReadOnly')}`}<span className="block text-xs text-on-surface-variant">{tool.description}</span>
    </label>)}
    {grants.map(g => <div className="flex justify-between gap-3 text-xs" key={`${g.serverId}/${g.toolName}`}>
      <span>{servers.find(s => s.id === g.serverId)?.name ?? g.serverId} · {g.toolName}</span>
      <button type="button" disabled={locked} onClick={() => onChange(grants.filter(x => x !== g))}>{t('remove')}</button>
    </div>)}
  </div>;
}

function McpServersCard({ admin }: { admin: boolean }) {
  const { t } = useTranslation('agents');
  const client = useQueryClient();
  const { data: servers = [], error } = useQuery({ queryKey: ['agent-mcp-servers'], queryFn: () => api.get<AgentMcpServer[]>('/agents/mcp-servers') });
  const [editing, setEditing] = useState<AgentMcpServer | null>(null);
  const deletion = useMutation({ mutationFn: (id: string) => api.delete(`/agents/mcp-servers/${id}`),
    onSuccess: () => client.invalidateQueries({ queryKey: ['agent-mcp-servers'] }) });
  return <section className="np-card p-5 space-y-4"><h3 className="font-semibold">{t('mcpServers')}</h3>
    {(error || deletion.error) && <p role="alert" className="text-error text-sm">{(error || deletion.error)?.message}</p>}
    <ul className="divide-y divide-outline-variant">{servers.map(server => <li key={server.id} className="flex items-center justify-between gap-3 py-2 text-sm">
      <div><strong>{server.name}</strong> · {server.transport}{!server.enabled && ` · ${t('disabled')}`}<div className="text-xs font-mono text-on-surface-variant">{server.id}</div></div>
      {admin && <div className="flex gap-3"><button type="button" className="text-primary" onClick={() => setEditing(server)}>{t('edit')}</button>
        <button type="button" disabled={deletion.isPending} onClick={() => { if (globalThis.confirm(t('deleteConfirm'))) deletion.mutate(server.id); }}>{t('delete')}</button></div>}
    </li>)}</ul>
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
  const { data: skills = [], error } = useQuery({ queryKey: ['agent-skills'], queryFn: () => api.get<AgentSkill[]>('/agents/skills') });
  const [version, setVersion] = useState('1.0.0');
  const [file, setFile] = useState<File | null>(null);
  const mutate = useMutation({ mutationFn: async (action: { kind: 'import' } | { kind: 'delete' | 'toggle'; skill: AgentSkill }) => {
    if (action.kind === 'delete') return api.delete(`/agents/skills/${action.skill.id}`);
    if (action.kind === 'toggle') return api.put(`/agents/skills/${action.skill.id}/enabled`, { enabled: !action.skill.enabled });
    if (!file || file.size > 10_000_000) throw new Error(t('package'));
    const bytes = new Uint8Array(await file.arrayBuffer());
    let binary = '';
    for (let start = 0; start < bytes.length; start += 32768) binary += String.fromCharCode(...bytes.subarray(start, start + 32768));
    return api.post('/agents/skills', { version, package: btoa(binary) });
  }, onSuccess: () => client.invalidateQueries({ queryKey: ['agent-skills'] }) });
  return <section className="np-card p-5 space-y-4"><h3 className="font-semibold">{t('skills')}</h3>
    {(error || mutate.error) && <p role="alert" className="text-error text-sm">{(error || mutate.error)?.message}</p>}
    <ul className="divide-y divide-outline-variant">{skills.map(skill => <li key={skill.id} className="py-3 space-y-1 text-sm">
      <div className="flex justify-between gap-4"><strong>{skill.name} · {skill.version}</strong>
        {admin && <div className="flex gap-3"><label className="flex gap-2"><input type="checkbox" checked={skill.enabled} disabled={mutate.isPending} onChange={() => mutate.mutate({ kind: 'toggle', skill })} />{t('enabled')}</label>
          <button type="button" disabled={mutate.isPending} onClick={() => { if (globalThis.confirm(t('deleteConfirm'))) mutate.mutate({ kind: 'delete', skill }); }}>{t('delete')}</button></div>}</div>
      <p className="text-on-surface-variant">{skill.description}</p><code className="text-xs break-all">SHA-256: {skill.sha256}</code>
    </li>)}</ul>
    {admin && <form className="space-y-3 border-t border-outline-variant pt-4" onSubmit={e => { e.preventDefault(); mutate.mutate({ kind: 'import' }); }}>
      <label className="block text-sm">{t('version')}<input className="input-field" required maxLength={64} pattern="[a-zA-Z0-9._-]+" value={version} onChange={e => setVersion(e.target.value)} /></label>
      <label className="block text-sm">{t('package')}<input type="file" accept=".zip" required className="input-field" onChange={e => setFile(e.target.files?.[0] ?? null)} /></label>
      <button type="submit" className="text-primary text-sm" disabled={mutate.isPending || !file}>{t('importSkill')}</button>
    </form>}
  </section>;
}
