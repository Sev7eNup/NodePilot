import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Add, Apps, ArrowRight, Checkmark, CheckmarkFilled, ChevronRight, Close,
  ErrorFilled, Flow, Information, Play, Renew, Search, Settings, Time, UserAvatar,
} from '@carbon/icons-react';
import { PreviewDesigner, type DemoStatus } from './PreviewDesigner';

type Variant = 'light' | 'dark';
type View = 'overview' | 'designer';
type SampleWorkflow = { id: string; nameKey?: string; name?: string; folder: string; trigger: string; time: string; status: DemoStatus };
const initialWorkflows: SampleWorkflow[] = [
  { id: 'service', nameKey: 'service', folder: 'folderService', trigger: 'manual', time: 'ago2', status: 'success' },
  { id: 'inventory', nameKey: 'inventory', folder: 'folderInventory', trigger: 'scheduled', time: 'ago8', status: 'success' },
  { id: 'backup', nameKey: 'backup', folder: 'folderBackup', trigger: 'scheduled', time: 'ago15', status: 'failed' },
  { id: 'cleanup', nameKey: 'cleanup', folder: 'folderCleanup', trigger: 'scheduled', time: 'ago42', status: 'success' },
  { id: 'certificate', nameKey: 'certificate', folder: 'folderCertificate', trigger: 'event', time: 'ago60', status: 'idle' },
];

function updateQuery(key: string, value: string) {
  const url = new URL(location.href);
  url.searchParams.set(key, value);
  history.replaceState(null, '', url);
}

function StatusBadge({ status }: { status: DemoStatus }) {
  const { t } = useTranslation('preview');
  const Icon = status === 'success' ? CheckmarkFilled : status === 'failed' ? ErrorFilled : status === 'running' ? Play : Time;
  return <span className={`sp-badge sp-status-${status}`}><Icon size={12} />{t(status)}</span>;
}

export function SkinPreview({ initialVariant }: { initialVariant: Variant }) {
  const { t, i18n } = useTranslation('preview');
  const [variant, setVariant] = useState(initialVariant);
  const [view, setView] = useState<View>(() => new URLSearchParams(location.search).get('view') === 'designer' ? 'designer' : 'overview');
  const [designerVisited, setDesignerVisited] = useState(view === 'designer');
  const [status, setStatus] = useState<DemoStatus>('idle');
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState('all');
  const [workflows, setWorkflows] = useState(initialWorkflows);
  const [editedName, setEditedName] = useState<string | null>(null);
  const [description, setDescription] = useState<string | null>(null);
  const [machine, setMachine] = useState('WIN-SRV-01');
  const [enabled, setEnabled] = useState(true);
  const [newName, setNewName] = useState('');
  const [notice, setNotice] = useState('');
  const [resetKey, setResetKey] = useState(0);
  const dialogRef = useRef<HTMLDialogElement>(null);
  const openerRef = useRef<HTMLButtonElement | null>(null);
  const newNameRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    document.documentElement.dataset.previewVariant = variant;
    document.documentElement.classList.toggle('dark', variant === 'dark');
    document.documentElement.style.colorScheme = variant;
    updateQuery('variant', variant);
  }, [variant]);

  useEffect(() => { document.documentElement.lang = i18n.language; }, [i18n.language]);
  useEffect(() => {
    if (!notice) return;
    const timer = window.setTimeout(() => setNotice(''), 4500);
    return () => window.clearTimeout(timer);
  }, [notice]);

  function switchView(next: View) {
    if (next === 'designer') setDesignerVisited(true);
    setView(next);
    updateQuery('view', next);
  }

  function openDialog(button: HTMLButtonElement) {
    openerRef.current = button;
    setNewName('');
    dialogRef.current?.showModal();
    newNameRef.current?.focus();
  }

  function resetDemo() {
    setStatus('idle'); setQuery(''); setFilter('all'); setWorkflows(initialWorkflows);
    setEditedName(null); setDescription(null); setMachine('WIN-SRV-01'); setEnabled(true);
    setResetKey((key) => key + 1); setNotice('resetNotice');
  }

  const displayedWorkflows = workflows.map((workflow) => ({
    ...workflow,
    label: workflow.name ?? t(workflow.nameKey!),
    status: workflow.id === 'service' && status !== 'idle' ? status : workflow.status,
  })).filter((workflow) => workflow.label.toLowerCase().includes(query.toLowerCase()) && (filter === 'all' || workflow.status === filter));

  return (
    <div className="skin-preview">
      <header className="sp-preview-header">
        <div className="sp-preview-identity"><span className="sp-prototype-tag">{t('prototype')}</span><span className="sp-muted sp-demo-description">{t('demo')}</span></div>
        <nav className="sp-view-tabs" aria-label={t('navigatePreview')}>
          <button type="button" className={view === 'overview' ? 'is-active' : ''} aria-pressed={view === 'overview'} onClick={() => switchView('overview')}><Apps size={16} />{t('overview')}</button>
          <button type="button" className={view === 'designer' ? 'is-active' : ''} aria-pressed={view === 'designer'} onClick={() => switchView('designer')}><Flow size={16} />{t('designer')}</button>
        </nav>
        <select className="sp-language" aria-label={t('language')} value={i18n.language} onChange={(event) => void i18n.changeLanguage(event.target.value)}><option value="de">DE</option><option value="en">EN</option></select>
      </header>

      {designerVisited && <div className="sp-designer-host" hidden={view !== 'designer'}><PreviewDesigner key={resetKey} status={status} variant={variant} /></div>}
      {view === 'overview' && (
        <div className="sp-app-layout">
          <aside className="sp-sidebar">
            <div className="sp-brand"><Flow size={27} /><div><strong>NodePilot</strong><span>WORKFLOW ORCHESTRATOR</span></div></div>
            <div className="sp-sidebar-section">{t('workspace')}</div>
            <nav aria-label={t('screenReader')}>
              <button type="button" className="sp-nav-item is-active" aria-current="page" onClick={() => { setQuery(''); setFilter('all'); }}><Apps size={18} /><span>{t('workflows')}</span><span className="sp-nav-count">{workflows.length}</span></button>
              <button type="button" className="sp-nav-item" onClick={() => switchView('designer')}><Flow size={18} /><span>{t('designer')}</span><ChevronRight size={14} /></button>
              <button type="button" className="sp-nav-item" onClick={() => document.getElementById('sp-workflow-settings')?.focus()}><Settings size={18} /><span>{t('settings')}</span></button>
            </nav>
            <div className="sp-sidebar-bottom"><div className="sp-workspace-avatar"><UserAvatar size={20} /><span>{t('account')}</span></div><span className="sp-muted"><span className="sp-local-dot" />{t('local')}</span></div>
          </aside>

          <div className="sp-main-shell">
            <header className="sp-app-header"><div>{t('workspace')}<ChevronRight size={14} /><strong>{t('workflows')}</strong></div><span className="sp-muted">{t('note')}</span></header>
            <main className="sp-overview">
              <div className="sp-page-heading"><div><h1>{t('workflows')}</h1><p className="sp-muted">{t('subtitle')}</p></div><button type="button" className="sp-button sp-button-primary" onClick={(event) => openDialog(event.currentTarget)}><Add size={17} />{t('newWorkflow')}</button></div>

              <div className="sp-stats">
                {[
                  { title: 'total', value: String(workflows.length), hint: 'totalHint', tone: '' },
                  { title: 'enabled', value: String(workflows.length - 1 + Number(enabled)), hint: 'enabledHint', tone: '' },
                  { title: 'successRate', value: i18n.language === 'de' ? '96,8 %' : '96.8%', hint: 'rateHint', tone: 'success' },
                  { title: 'attention', value: '1', hint: 'attentionHint', tone: 'danger' },
                ].map((stat) => <section className="sp-panel sp-stat" key={stat.title}><span className="sp-stat-title">{t(stat.title)}</span><strong className={stat.tone ? `sp-text-${stat.tone}` : ''}>{stat.value}</strong><span className="sp-muted">{t(stat.hint)}</span></section>)}
              </div>

              <div className="sp-content-grid">
                <section className="sp-panel sp-workflow-panel">
                  <div className="sp-panel-heading"><h2>{t('allWorkflows')}</h2><span className="sp-muted">{t('count', { count: workflows.length })}</span></div>
                  <div className="sp-table-filters"><div className="sp-search"><Search size={16} /><input className="sp-field" aria-label={t('search')} placeholder={t('search')} value={query} onChange={(event) => setQuery(event.target.value)} /></div><select className="sp-field" aria-label={t('filter')} value={filter} onChange={(event) => setFilter(event.target.value)}><option value="all">{t('allStates')}</option>{(['idle', 'running', 'success', 'failed'] as const).map((state) => <option key={state} value={state}>{t(state)}</option>)}</select></div>
                  <div className="sp-table-scroll"><table className="sp-table"><thead><tr><th>{t('name')}</th><th>{t('trigger')}</th><th>{t('lastExecution')}</th><th>{t('result')}</th></tr></thead><tbody>{displayedWorkflows.map((workflow) => <tr key={workflow.id} className={workflow.id === 'service' ? 'sp-featured-row' : ''}><td>{workflow.id === 'service' ? <button type="button" className="sp-workflow-link" onClick={() => switchView('designer')}><Flow size={17} /><span><strong>{workflow.label}</strong><small>{t(workflow.folder)}</small></span><ArrowRight size={14} /></button> : <div className="sp-workflow-name"><Flow size={17} /><span><strong>{workflow.label}</strong><small>{t(workflow.folder)}</small></span></div>}</td><td><span className="sp-trigger">{workflow.trigger === 'manual' ? <Play size={12} /> : <Time size={12} />}{t(workflow.trigger)}</span></td><td className="sp-muted sp-no-wrap">{t(workflow.time)}</td><td><StatusBadge status={workflow.status} /></td></tr>)}</tbody></table>{displayedWorkflows.length === 0 && <div className="sp-empty"><p>{t('empty')}</p><button className="sp-button" onClick={() => { setQuery(''); setFilter('all'); }}>{t('clearSearch')}</button></div>}</div>
                  <div className="sp-table-footer"><span className="sp-muted">{t('service')}</span><button type="button" className="sp-text-button" onClick={() => switchView('designer')}>{t('openDesigner')}<ArrowRight size={14} /></button></div>
                </section>

                <section className="sp-panel sp-settings" id="sp-workflow-settings" tabIndex={-1} aria-labelledby="sp-settings-title">
                  <div className="sp-panel-heading"><h2 id="sp-settings-title">{t('selected')}</h2><Settings size={16} className="sp-muted" /></div>
                  <form onSubmit={(event) => {
                    event.preventDefault();
                    const name = (editedName ?? t('service')).trim();
                    if (!name) return;
                    setWorkflows((items) => items.map((workflow) => workflow.id === 'service' ? { ...workflow, name } : workflow));
                    setNotice('saved');
                  }}>
                    <label className="sp-label">{t('name')}<input className="sp-field" required value={editedName ?? t('service')} onChange={(event) => setEditedName(event.target.value)} /></label>
                    <label className="sp-label">{t('machine')}<select className="sp-field sp-mono" value={machine} onChange={(event) => setMachine(event.target.value)}><option>WIN-SRV-01</option><option>WIN-SRV-02</option><option>SQL-SRV-02</option></select></label>
                    <label className="sp-label">{t('description')}<textarea className="sp-field" rows={3} value={description ?? t('descriptionValue')} onChange={(event) => setDescription(event.target.value)} /></label>
                    <label className="sp-checkbox"><input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} /><span>{t('activeLabel')}<small className="sp-muted">{t('activeHint')}</small></span></label>
                    <button className="sp-button sp-settings-save" type="submit" disabled={!(editedName ?? t('service')).trim()}><Checkmark size={16} />{t('save')}</button>
                  </form>
                </section>

                <section className="sp-panel sp-activity"><div className="sp-panel-heading"><h2>{t('recent')}</h2><Time size={16} className="sp-muted" /></div>{[
                  { title: 'completedEvent', detail: 'completedDetail', time: 'ago8', status: 'success' as const },
                  { title: 'runningEvent', detail: 'runningDetail', time: 'ago2', status: 'running' as const },
                  { title: 'failedEvent', detail: 'failedDetail', time: 'ago15', status: 'failed' as const },
                ].map((item) => <div className="sp-activity-row" key={item.title}><span className={`sp-activity-icon sp-status-${item.status}`}>{item.status === 'success' ? <Checkmark size={16} /> : item.status === 'failed' ? <Close size={16} /> : <Play size={16} />}</span><div><strong>{t(item.title)}</strong><span className="sp-muted">{t(item.detail)}</span></div><time className="sp-muted">{t(item.time)}</time></div>)}</section>

                <section className="sp-panel sp-controls-sample"><div className="sp-panel-heading"><h2>{t('controls')}</h2><Information size={16} className="sp-muted" /></div><p className="sp-muted">{t('controlsHint')}</p><div className="sp-button-samples"><button type="button" className="sp-button sp-button-primary" onClick={() => setNotice('sampleAction')}><Play size={14} />{t('primary')}</button><button type="button" className="sp-button" onClick={(event) => openDialog(event.currentTarget)}>{t('showDialog')}</button><button type="button" className="sp-button" disabled>{t('disabled')}</button></div><div className="sp-badge-samples"><StatusBadge status="success" /><StatusBadge status="running" /><StatusBadge status="failed" /></div></section>
              </div>
            </main>
          </div>
        </div>
      )}

      <footer className="sp-preview-bar">
        <div className="sp-preview-caption"><strong>{t('previewTitle')}</strong><span className="sp-muted">{t('previewHint')}</span></div>
        <div className="sp-variant-switch" role="group" aria-label={t('variant')}>
          {(['light', 'dark'] as const).map((theme) => <button key={theme} type="button" className={variant === theme ? 'is-active' : ''} aria-pressed={variant === theme} onClick={() => setVariant(theme)}><span className={`sp-swatch sp-swatch-${theme}`} />{t(theme)}{variant === theme && <Checkmark size={13} />}</button>)}
        </div>
        <label className="sp-status-control"><span>{t('status')}</span><select className="sp-field" value={status} onChange={(event) => setStatus(event.target.value as DemoStatus)}>{(['idle', 'running', 'success', 'failed'] as const).map((state) => <option key={state} value={state}>{t(state)}</option>)}</select></label>
        <button className="sp-icon-button" type="button" title={t('reset')} aria-label={t('reset')} onClick={resetDemo}><Renew size={18} /></button>
      </footer>

      <div className={`sp-notice ${notice ? 'is-visible' : ''}`} role="status">{notice && <><CheckmarkFilled size={16} />{t(notice)}</>}</div>
      <dialog className="sp-dialog" ref={dialogRef} aria-labelledby="sp-dialog-title" aria-describedby="sp-dialog-hint" onClose={() => openerRef.current?.focus()} onClick={(event) => { if (event.target === event.currentTarget) { const bounds = event.currentTarget.getBoundingClientRect(); if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) dialogRef.current?.close(); } }}>
        <div className="sp-dialog-heading"><h2 id="sp-dialog-title">{t('dialogTitle')}</h2><button type="button" className="sp-icon-button" aria-label={t('close')} onClick={() => dialogRef.current?.close()}><Close size={18} /></button></div>
        <p id="sp-dialog-hint" className="sp-muted">{t('dialogHint')}</p>
        <form onSubmit={(event) => {
          event.preventDefault();
          const name = newName.trim();
          if (!name) return;
          setWorkflows((items) => [...items, { id: crypto.randomUUID(), name, folder: 'newFolder', trigger: 'manual', time: 'never', status: 'idle' }]);
          setQuery(''); setFilter('all'); setNotice('created'); dialogRef.current?.close();
        }}>
          <label className="sp-label">{t('workflowName')}<input ref={newNameRef} className="sp-field" placeholder={t('namePlaceholder')} value={newName} required maxLength={100} onChange={(event) => setNewName(event.target.value)} /></label>
          <div className="sp-dialog-actions"><button type="button" className="sp-button" onClick={() => dialogRef.current?.close()}>{t('cancel')}</button><button className="sp-button sp-button-primary" type="submit" disabled={!newName.trim()}><Add size={16} />{t('create')}</button></div>
        </form>
      </dialog>
    </div>
  );
}
