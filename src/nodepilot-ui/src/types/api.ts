export interface LastExecutionInfo {
  id: string;
  status: string;
  startedAt: string;
  completedAt: string | null;
  durationMs: number | null;
}

/**
 * Per-workflow capability flags resolved by the server from the caller's RBAC grants on
 * the workflow's folder, ANDed with the global UserRole cap. Drives button visibility in
 * the UI (the API independently enforces the same checks via [Authorize] + folder ACLs).
 */
export interface ResourceCapabilities {
  canRead: boolean;
  canRun: boolean;
  canEdit: boolean;
  /** Workflow DELETE is Admin-only, enforced at the controller. canDelete is therefore
   *  independent of canEdit — a FolderEditor Operator has canEdit=true but canDelete=false. */
  canDelete: boolean;
  canAdmin: boolean;
}

export interface Workflow {
  id: string;
  name: string;
  description: string | null;
  definitionJson: string;
  version: number;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
  createdBy: string | null;
  updatedBy: string | null;
  activityCount?: number;
  triggerTypes?: string[];
  lastExecution?: LastExecutionInfo | null;
  successCount?: number;
  totalCount?: number;
  avgDurationMs?: number | null;
  /** Edit-Lock — non-null when somebody has the workflow checked out for editing. */
  checkedOutByUserId?: string | null;
  /** Resolved username of the lock owner — server-side join saves a per-row /users round-trip. */
  checkedOutByUserName?: string | null;
  checkedOutAt?: string | null;
  /** RBAC home folder. Always populated; legacy responses default to the Root sentinel. */
  folderId?: string;
  /** "/Finance/Reports" — convenience for breadcrumb / list rendering. */
  folderPath?: string;
  /** Caller's effective capabilities on this workflow. Undefined means the server
   *  did not surface them (older endpoints) and the UI must fall back to global role. */
  capabilities?: ResourceCapabilities;
}

export interface ManagedMachine {
  id: string;
  name: string;
  hostname: string;
  winRmPort: number;
  useSsl: boolean;
  defaultCredentialId: string | null;
  tags: string | null;
  lastConnectivityCheck: string | null;
  isReachable: boolean;
  /** Distinct workflows whose definition references this machine via any node's
   *  `data.targetMachineId`. Drives the "where is this machine used?" indicator
   *  on the machines list — operators check this before deleting/disabling. */
  usedByWorkflowCount: number;
  /** Step executions in the last 7 days targeting this machine (all statuses). */
  recentStepCount: number;
  /** Subset of recentStepCount with status = Failed. Pair drives the success-rate
   *  bar in the activity cell, analogous to WorkflowsPage.successCount/totalCount. */
  recentFailedStepCount: number;
  /** Step executions currently in Running state targeting this machine. 0 = idle. */
  activeRunCount: number;
}

export interface Credential {
  id: string;
  name: string;
  username: string;
  domain: string | null;
  /** Optional expiry (ISO datetime, UTC). Drives the expired/expiring-soon badges on the list. */
  expiresAt: string | null;
}

export interface WorkflowExecution {
  id: string;
  workflowId: string;
  status: string;
  startedAt: string;
  completedAt: string | null;
  triggeredBy: string | null;
  errorMessage: string | null;
  traceId: string | null;
  spanId: string | null;
  returnData: string | null;
  inputParametersJson: string | null;
  // Triage fields from GET /api/executions (list view). On single-resource endpoints
  // (Execute/Retry) these are typically empty/0 — the server only populates them for
  // the history-grid use case. Exception: GetById resolves the parent link fields so
  // detail views (Live-Ops drilldown) can render a navigable parent chip.
  startedByUsername?: string | null;
  parentExecutionId?: string | null;
  parentWorkflowName?: string | null;
  stepsTotal?: number;
  stepsCompleted?: number;
  // ALL failed steps of the run (chronological by StartedAt). Parallel branches can
  // fail at the same time; the grid joins this list into a comma-separated string.
  failedSteps?: { stepId: string; stepName: string | null }[] | null;
}

export interface ObservabilityConfig {
  enabled: boolean;
  traceUiUrlTemplate: string | null;
  traceBackendName: string | null;
  prometheusAvailable: boolean;
  browserOtlpEndpoint: string | null;
  serviceName: string | null;
  environment: string | null;
  grafanaBaseUrl: string | null;
}

export interface TelemetryPanel {
  key: string;
  title: string;
  unit: string;
  value: number | null;
  error: string | null;
}

export interface TelemetrySummary {
  available: boolean;
  panels: TelemetryPanel[];
}

export interface MetricsPoint { timestamp: number; value: number | null; }
export interface MetricsSeriesLine { label: string; points: MetricsPoint[]; }
export interface MetricsSeries { key: string; title: string; unit: string; lines: MetricsSeriesLine[]; }
export interface MetricsTableRow { label: string; value: number; }
export interface MetricsTable { key: string; title: string; unit: string; rows: MetricsTableRow[]; }
export interface MetricsDataSeries { label: string; labels: Record<string, string>; points: MetricsPoint[]; }
export interface MetricsWidget {
  id: number;
  title: string;
  description: string | null;
  type: 'stat' | 'timeseries' | 'bargauge' | 'piechart' | 'table' | 'heatmap';
  unit: string;
  grid: { x: number; y: number; width: number; height: number };
  data: MetricsDataSeries[];
  error: string | null;
}
export interface MetricsDashboard {
  available: boolean;
  key: string;
  title: string;
  panels: TelemetryPanel[];
  series: MetricsSeries[];
  tables: MetricsTable[];
  widgets: MetricsWidget[];
}

export interface StepExecution {
  id: string;
  stepId: string;
  stepName: string | null;
  stepType: string;
  targetMachine: string | null;
  status: string;
  startedAt: string | null;
  completedAt: string | null;
  output: string | null;
  errorOutput: string | null;
  attemptCount?: number;
  pausedAt?: string | null;
  variablesSnapshot?: string | null;
  /** Verbose execution log (PowerShell Start-Transcript capture for runScript with `config.transcript:true`). Null when tracing was disabled. */
  traceOutput: string | null;
  /** JSON-serialized OutputParameters captured at terminal time. Null when the step produced no params. Already redacted. */
  outputParametersJson?: string | null;
  /** Producing node's `data.outputVariable` alias, when set. */
  outputVariable?: string | null;
  /** Custom-activity reproducibility snapshot (which definition key/version/hash ran). Non-null only for custom:&lt;key&gt; steps. */
  customActivityKey?: string | null;
  customActivityVersion?: number | null;
  customActivityHash?: string | null;
}

/**
 * Per-step coverage stats over the last <windowDays>. Drives the canvas heatmap toggle.
 * `executedCount` covers Succeeded + Failed; `skippedCount` covers Skipped + Cancelled
 * (cancelled = junction-race, didn't produce a result).
 */
export interface NodeCoverageStats {
  stepId: string;
  executedCount: number;
  failedCount: number;
  skippedCount: number;
  lastExecutedAt: string | null;
  lastSucceededAt: string | null;
  lastFailedAt: string | null;
}

export interface WorkflowCoverageResponse {
  workflowId: string;
  windowDays: number;
  totalExecutions: number;
  oldestExecutionInWindow: string | null;
  nodes: NodeCoverageStats[];
}

/**
 * One declared input parameter of a child workflow's manualTrigger. Drives the typed
 * mapping table in StartWorkflowConfig.
 *
 * `hasConflict` = true when multiple manualTrigger nodes in the same workflow declared
 * this name with different `type` or `default`. UI renders a warning chip.
 */
export interface WorkflowContractInput {
  name: string;
  type: string;
  required: boolean;
  default: string | null;
  description: string | null;
  hasConflict: boolean;
}

/**
 * One downstream-available key after the parent's startWorkflow step.
 *
 * - `system`: always-present engine metadata (__executionId, __status, __workflowId, __workflowName)
 * - `single`: from the workflow's only returnData node — reliable
 * - `multiple`: from one of several returnData nodes — only one wins per execution,
 *   so the UI surfaces a warning that this key may not be populated for any given run
 */
export interface WorkflowContractOutput {
  name: string;
  source: 'system' | 'single' | 'multiple';
}

export interface WorkflowContractResponse {
  workflowId: string;
  workflowName: string;
  hasManualTrigger: boolean;
  hasReturnData: boolean;
  hasMultipleReturnDataNodes: boolean;
  inputs: WorkflowContractInput[];
  outputs: WorkflowContractOutput[];
}

export interface StepTestResult {
  success: boolean;
  output: string | null;
  errorOutput: string | null;
  outputParameters: Record<string, string>;
  durationMs: number;
  errorMessage: string | null;
}

/**
 * Single variable suggestion for the step-test mock editor.
 * `key` is the template handle (`stepName.output`, `globals.ENV`, etc.).
 * `value` is the last-known value (already redacted) or null when no run is available.
 */
export interface StepTestContextVariable {
  key: string;
  origin: string;
  source: 'output' | 'error' | 'success' | 'param' | 'global';
  value: string | null;
}

export interface StepTestContextResponse {
  executionId: string | null;
  executedAt: string | null;
  status: string | null;
  variables: StepTestContextVariable[];
}

export interface StepTestContextRunInfo {
  executionId: string;
  startedAt: string;
  status: string;
  triggeredBy: string | null;
  /** True iff the step under test actually executed in this run (vs skipped). */
  stepRan: boolean;
}

/**
 * Browser auth response (login / refresh / windows). The JWT is NOT here — it lives only in
 * the httpOnly `np_auth` cookie, so JS (and any XSS) cannot read or exfiltrate it. The server
 * returns the token in the body solely to non-browser Bearer callers (CLI / API), which the
 * SPA never is.
 */
export interface LoginResponse {
  userId: string;
  username: string;
  role: string;
}

/**
 * Anonymous /auth/methods response — the LoginPage uses this to decide which auth
 * affordances to render. The password form is available when either local or LDAP
 * authentication is enabled. Windows and OIDC expose dedicated browser endpoints.
 */
export interface AuthMethodsResponse {
  local: boolean;
  ldap: boolean;
  windows: boolean;
  windowsEndpoint: string | null;
  oidc?: boolean;
  oidcEndpoint?: string | null;
  oidcDisplayName?: string | null;
}

export interface UserRow {
  id: string;
  username: string;
  role: 'Admin' | 'Operator' | 'Viewer';
  isActive: boolean;
  createdAt: string;
  /** Authentication source. Absent on responses from pre-enterprise API versions. */
  provider?: string | null;
  /** Stable identity-provider authority (for example an AD forest or OIDC issuer). */
  authority?: string | null;
  /** Provider subject. For Active Directory this is the canonical object SID. */
  subject?: string | null;
  lastDirectorySyncAt?: string | null;
  directorySyncStatus?: 'Never' | 'Current' | 'Healthy' | 'Stale' | 'Failed' | string | null;
  /** External-identity tombstones cannot be recreated by a later JIT login. */
  isTombstoned?: boolean;
  /** Explicitly allowlisted emergency account for BreakGlassOnly local-login mode. */
  isBreakGlass?: boolean;
}

export interface CreateUserPayload {
  username: string;
  password: string;
  role: 'Admin' | 'Operator' | 'Viewer';
  isBreakGlass?: boolean;
}

export interface UpdateUserPayload {
  role?: 'Admin' | 'Operator' | 'Viewer';
  isActive?: boolean;
  password?: string;
  isBreakGlass?: boolean;
}

// ---- Operations / live-ops Mission Control (GET /api/operations/graph) ------
// Mirrors NodePilot.Api.Dtos.OperationsGraphDto. RBAC-folder-scoped on the server.

export type OpsRefStatus = 'Resolved' | 'Dynamic' | 'Unresolved' | 'Ambiguous';

export interface OpsNode {
  workflowId: string;
  name: string;
  folderId: string;
  folderPath: string;
  isEnabled: boolean;
  runningCount: number;
  lastStatus: string | null;
  callFrequency: number | null;
}

export interface OpsEdge {
  id: string;
  source: string;
  /** Resolved target workflow id; null for dynamic/unresolved/ambiguous refs. */
  target: string | null;
  kind: 'startWorkflow' | 'forEach';
  refStatus: OpsRefStatus;
  rawRef: string;
  callCount: number;
}

export interface OpsRunningExecution {
  executionId: string;
  workflowId: string;
  status: string;
  startedAt: string;
  /** Parent run for sub-workflow executions — drives the timeline call connectors. */
  parentExecutionId: string | null;
}

/** Terminal execution completed within the recent window (30 min, newest 200). */
export interface OpsRecentExecution {
  executionId: string;
  workflowId: string;
  status: string;
  startedAt: string;
  completedAt: string;
  /** Parent run for sub-workflow executions — drives the timeline call connectors. */
  parentExecutionId: string | null;
}

export interface OpsCapabilities {
  canCancel: boolean;
}

export interface OperationsGraph {
  nodes: OpsNode[];
  edges: OpsEdge[];
  running: OpsRunningExecution[];
  recent: OpsRecentExecution[];
  capabilities: OpsCapabilities;
}

