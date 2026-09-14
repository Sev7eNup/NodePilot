import { useMemo, useState } from 'react'
import {
  Checkmark, CheckmarkFilled, CircleDash, FitToScreen, FlowConnection,
  Information, PlayFilledAlt, Renew, Search, Settings, Terminal,
  WarningFilled, ZoomIn, ZoomOut,
} from '@carbon/icons-react'
import {
  Handle, MarkerType, Position, ReactFlow, useNodesState,
  type Edge, type Node, type NodeProps, type ReactFlowInstance,
} from '@xyflow/react'
import { useTranslation } from 'react-i18next'

export type DemoStatus = 'idle' | 'running' | 'success' | 'failed'
type NodeStatus = DemoStatus | 'skipped'
type Activity = 'start' | 'check' | 'result' | 'error'
type PreviewNode = Node<{ activity: Activity; label: string; status: NodeStatus }, 'preview'>

const activities: Activity[] = ['start', 'check', 'result', 'error']
const activityIcons = { start: PlayFilledAlt, check: Settings, result: Checkmark, error: Terminal }
const initialPositions = {
  start: { x: 0, y: 130 },
  check: { x: 215, y: 130 },
  result: { x: 460, y: 60 },
  error: { x: 460, y: 260 },
}
const initialNodes: PreviewNode[] = activities.map(activity => ({
  id: activity,
  type: 'preview',
  position: initialPositions[activity],
  selected: activity === 'check',
  data: { activity, label: '', status: 'idle' },
}))

function activityStatus(activity: Activity, status: DemoStatus): NodeStatus {
  if (status === 'idle') return 'idle'
  if (activity === 'start') return 'success'
  if (activity === 'check') return status
  if (status === 'running') return 'idle'
  if (activity === 'error') return status === 'failed' ? 'success' : 'skipped'
  return status === 'success' ? 'success' : 'skipped'
}

function StatusIcon({ status }: { status: NodeStatus }) {
  const Icon = status === 'success' ? CheckmarkFilled : status === 'failed' ? WarningFilled : status === 'running' ? PlayFilledAlt : CircleDash
  return <Icon size={14} aria-hidden="true" />
}

function ActivityNode({ data, selected }: NodeProps<PreviewNode>) {
  const { t } = useTranslation('designerPreview')
  const Icon = activityIcons[data.activity]
  const bookendPoints = data.activity === 'start'
    ? '55.25,0.75 14,0.75 0.75,28 14,55.25 55.25,55.25'
    : data.activity === 'result' ? '0.75,0.75 42,0.75 55.25,28 42,55.25 0.75,55.25' : undefined
  return (
    <div className={`sp-flow-node sp-flow-node-${data.activity}${selected ? ' sp-flow-node-selected' : ''}`}>
      {data.activity !== 'start' && <Handle type="target" position={Position.Left} isConnectable={false} />}
      <div className={`sp-flow-node-icon${bookendPoints ? ' sp-flow-node-bookend' : ''}`}>
        {bookendPoints && <svg className="sp-flow-bookend-outline" viewBox="0 0 56 56" aria-hidden="true">
          {selected && <polygon className="sp-flow-bookend-selection" points={bookendPoints} transform="translate(-5 -5) scale(1.18)" />}
          <polygon className="sp-flow-bookend-body" points={bookendPoints} />
        </svg>}
        <Icon className="sp-flow-node-glyph" size={28} aria-hidden="true" />
      </div>
      {data.activity !== 'result' && data.activity !== 'error' && <Handle type="source" position={Position.Right} isConnectable={false} />}
      <div className="sp-flow-node-label">{data.label}</div>
      <div className={`sp-flow-node-status sp-designer-status-${data.status}`}>
        <StatusIcon status={data.status} />{t(`statuses.${data.status}`)}
      </div>
    </div>
  )
}

const nodeTypes = { preview: ActivityNode }

export function PreviewDesigner({ status, variant }: { status: DemoStatus; variant: 'light' | 'dark' }) {
  const { t } = useTranslation('designerPreview')
  const [nodes, setNodes, onNodesChange] = useNodesState<PreviewNode>(initialNodes)
  const [flow, setFlow] = useState<ReactFlowInstance<PreviewNode, Edge> | null>(null)
  const [selected, setSelected] = useState<Activity>('check')
  const [search, setSearch] = useState('')
  const [names, setNames] = useState<Partial<Record<Activity, string>>>({})
  const [machine, setMachine] = useState('WIN-SRV-01')
  const [service, setService] = useState('Spooler')
  const [timeout, setTimeoutValue] = useState('30')
  const [trigger, setTrigger] = useState('manual')
  const [severity, setSeverity] = useState('error')
  const [output, setOutput] = useState<string | undefined>()
  const [message, setMessage] = useState<string | undefined>()
  const SelectedIcon = activityIcons[selected]

  const displayNodes = nodes.map(node => ({
    ...node,
    ariaLabel: `${names[node.data.activity] ?? t(`nodes.${node.data.activity}`)}, ${t(`statuses.${activityStatus(node.data.activity, status)}`)}`,
    data: { ...node.data, label: names[node.data.activity] ?? t(`nodes.${node.data.activity}`), status: activityStatus(node.data.activity, status) },
  }))
  const edges: Edge[] = useMemo(() => {
    const routes = [
      { id: 'start-check', source: 'start', target: 'check', active: status !== 'idle' },
      { id: 'check-result', source: 'check', target: 'result', active: status === 'success', label: t('successPath') },
      { id: 'check-error', source: 'check', target: 'error', active: status === 'failed', label: t('failedPath') },
    ]
    return routes.map(route => {
      const color = route.active ? route.target === 'error' ? 'var(--sp-danger)' : 'var(--sp-accent)' : 'var(--sp-control-border)'
      return {
        ...route,
        type: 'smoothstep',
        markerEnd: { type: MarkerType.ArrowClosed, width: 18, height: 18, color },
        style: { stroke: color, strokeWidth: route.active ? 2 : 1.5 },
        labelStyle: { fill: 'var(--sp-muted)', fontSize: 12 },
        labelBgStyle: { fill: 'var(--sp-bg)' },
        labelBgPadding: [6, 4] as [number, number],
        labelBgBorderRadius: 3,
      }
    })
  }, [status, t])

  function selectActivity(activity: Activity) {
    setSelected(activity)
    setNodes(current => current.map(node => ({ ...node, selected: node.id === activity })))
  }

  function resetPositions() {
    setNodes(current => current.map(node => ({ ...node, position: initialPositions[node.data.activity] })))
    requestAnimationFrame(() => { void flow?.fitView({ padding: 0.18, maxZoom: 1 }) })
  }

  return (
    <section className="sp-designer" aria-label={t('title')}>
      <header className="sp-designer-toolbar">
        <div className="sp-designer-heading">
          <FlowConnection size={20} aria-hidden="true" />
          <div><h2>{t('title')}</h2><p className="sp-muted">{t('subtitle')}</p></div>
        </div>
        <div className="sp-designer-tools" aria-label={t('controls')}>
          <span className="sp-muted sp-designer-count">{t('nodesCount')}</span>
          <button type="button" className="sp-icon-button" title={t('zoomOut')} aria-label={t('zoomOut')} onClick={() => { void flow?.zoomOut() }}><ZoomOut size={18} /></button>
          <button type="button" className="sp-icon-button" title={t('zoomIn')} aria-label={t('zoomIn')} onClick={() => { void flow?.zoomIn() }}><ZoomIn size={18} /></button>
          <button type="button" className="sp-icon-button" title={t('fitView')} aria-label={t('fitView')} onClick={() => { void flow?.fitView({ padding: 0.18, maxZoom: 1 }) }}><FitToScreen size={18} /></button>
          <button type="button" className="sp-icon-button" title={t('reset')} aria-label={t('reset')} onClick={resetPositions}><Renew size={18} /></button>
        </div>
      </header>

      <div className="sp-designer-workspace">
        <aside className="sp-designer-library" aria-label={t('library')}>
          <div className="sp-designer-section-heading"><h3>{t('library')}</h3><span className="sp-muted">4</span></div>
          <label className="sp-designer-search"><Search size={16} aria-hidden="true" /><input className="sp-field" type="search" aria-label={t('search')} placeholder={t('search')} value={search} onChange={event => setSearch(event.target.value)} /></label>
          <p className="sp-designer-library-hint sp-muted">{t('libraryHint')}</p>
          <div className="sp-designer-library-list">
            {activities.filter(activity => `${t(`nodes.${activity}`)} ${t(`kinds.${activity}`)}`.toLowerCase().includes(search.toLowerCase())).map(activity => {
              const Icon = activityIcons[activity]
              return <button key={activity} type="button" className={`sp-designer-library-item${selected === activity ? ' is-selected' : ''}`} aria-pressed={selected === activity} onClick={() => selectActivity(activity)}><span className={`sp-designer-library-icon sp-flow-tone-${activity}`}><Icon size={20} aria-hidden="true" /></span><span><strong>{t(`nodes.${activity}`)}</strong><small>{t(`kinds.${activity}`)}</small></span></button>
            })}
            {!activities.some(activity => `${t(`nodes.${activity}`)} ${t(`kinds.${activity}`)}`.toLowerCase().includes(search.toLowerCase())) && <p className="sp-muted">{t('noResults')}</p>}
          </div>
        </aside>

        <div className="sp-designer-canvas" aria-label={t('canvas')}>
          <ReactFlow<PreviewNode, Edge>
            aria-label={t('canvas')}
            nodes={displayNodes}
            edges={edges}
            nodeTypes={nodeTypes}
            onNodesChange={onNodesChange}
            onNodeClick={(_, node) => selectActivity(node.data.activity)}
            onSelectionChange={({ nodes: selectedNodes }) => { if (selectedNodes.length === 1) setSelected(selectedNodes[0].data.activity) }}
            onInit={setFlow}
            colorMode={variant}
            fitView
            fitViewOptions={{ padding: 0.18, maxZoom: 1 }}
            minZoom={0.3}
            maxZoom={1.75}
            nodesConnectable={false}
            edgesFocusable={false}
            deleteKeyCode={null}
            multiSelectionKeyCode={null}
            ariaLabelConfig={{
              'node.a11yDescription.default': t('canvasKeyboard'),
              'node.a11yDescription.keyboardDisabled': t('canvasKeyboard'),
              'node.a11yDescription.ariaLiveMessage': ({ x, y }) => t('nodeMoved', { x, y }),
              'edge.a11yDescription.default': t('edgeDescription'),
              'handle.ariaLabel': t('handle'),
            }}
          />
          <span className="sp-designer-canvas-hint">{t('canvasHint')}</span>
        </div>

        <aside className="sp-designer-properties" aria-label={t('properties')}>
          <div className="sp-designer-section-heading"><h3>{t('properties')}</h3><span className="sp-designer-configured"><Checkmark size={12} aria-hidden="true" />{t('configured')}</span></div>
          <div className="sp-designer-property-title"><span className={`sp-designer-library-icon sp-flow-tone-${selected}`}><SelectedIcon size={20} aria-hidden="true" /></span><div><strong>{names[selected] ?? t(`nodes.${selected}`)}</strong><small className="sp-muted">{t(`kinds.${selected}`)}</small></div></div>
          <p className="sp-designer-description sp-muted">{t(`descriptions.${selected}`)}</p>
          <div className="sp-designer-fields">
            <label className="sp-label">{t('name')}<input className="sp-field" value={names[selected] ?? t(`nodes.${selected}`)} onChange={event => setNames(current => ({ ...current, [selected]: event.target.value }))} /></label>
            {selected === 'check' && <>
              <label className="sp-label">{t('machine')}<select className="sp-field" value={machine} onChange={event => setMachine(event.target.value)}><option>WIN-SRV-01</option><option>WIN-SRV-02</option><option>SQL-SRV-02</option></select></label>
              <label className="sp-label">{t('service')}<input className="sp-field sp-designer-mono" value={service} onChange={event => setService(event.target.value)} /></label>
              <label className="sp-label">{t('timeout')}<input className="sp-field sp-designer-timeout" type="number" min="1" max="300" value={timeout} onChange={event => setTimeoutValue(event.target.value)} /></label>
            </>}
            {selected === 'start' && <><label className="sp-label">{t('trigger')}<select className="sp-field" value={trigger} onChange={event => setTrigger(event.target.value)}><option value="manual">{t('manual')}</option><option value="scheduled">{t('scheduled')}</option></select></label>{trigger === 'scheduled' && <p className="sp-muted">{t('interval')}</p>}</>}
            {selected === 'result' && <label className="sp-label">{t('output')}<textarea className="sp-field" rows={4} value={output ?? t('outputValue')} onChange={event => setOutput(event.target.value)} /></label>}
            {selected === 'error' && <><label className="sp-label">{t('severity')}<select className="sp-field" value={severity} onChange={event => setSeverity(event.target.value)}><option value="error">{t('error')}</option><option value="warning">{t('warning')}</option></select></label><label className="sp-label">{t('logMessage')}<textarea className="sp-field" rows={4} value={message ?? t('logValue')} onChange={event => setMessage(event.target.value)} /></label></>}
          </div>
          <p className="sp-designer-property-hint sp-muted"><Information size={14} aria-hidden="true" />{t('propertiesHint')}</p>
        </aside>
      </div>

      <section className="sp-designer-execution" aria-label={t('execution')}>
        <div className="sp-designer-execution-heading"><h3><Terminal size={16} aria-hidden="true" />{t('execution')}</h3><span className="sp-muted">{t('executionHint')}</span><span className={`sp-designer-run-status sp-designer-status-${status}`}><StatusIcon status={status} />{t(`statuses.${status}`)}</span></div>
        <p className={`sp-designer-execution-summary sp-designer-status-${status}`} role="status">{t(`${status}Summary`, { machine, service })}</p>
        <div className="sp-designer-execution-steps">{activities.map(activity => {
          const stepStatus = activityStatus(activity, status)
          return <button className="sp-designer-execution-step" type="button" key={activity} onClick={() => selectActivity(activity)} aria-pressed={selected === activity}><span className={`sp-designer-step-symbol sp-designer-status-${stepStatus}`}><StatusIcon status={stepStatus} /></span><span><strong>{names[activity] ?? t(`nodes.${activity}`)}</strong><small className="sp-muted">{t(`statuses.${stepStatus}`)}</small></span><span className="sp-designer-step-duration sp-muted">{stepStatus === 'success' ? activity === 'check' ? '248 ms' : '12 ms' : '—'}</span></button>
        })}</div>
      </section>
    </section>
  )
}
