// Fictional, read-only data for the real NodePilot frontend. No backend is called.
export const USER = { id: '00000000-0000-0000-0000-000000000099', username: 'demo-admin', role: 'Admin' };
export const ROOT = '00000000-0000-0000-0000-000000000002';
export const WF = '20202020-2020-2020-2020-202020202020';
export const EXEC = 'de120001-2020-2020-2020-202020202020';
const now = Date.now();
const iso = (offset = 0) => new Date(now + offset).toISOString();
const MIN = 60000;
const node = (id, label, activityType, x, y, config = {}, extra = {}) => ({
  id, type: 'activity', position: { x, y }, data: { label, activityType, config, ...extra },
});
export const definition = {
  nodes: [
    node('schedule', 'Every morning', 'scheduleTrigger', 0, 180, { cronExpression: '0 7 * * 1-5', timeZoneId: 'UTC' }),
    node('services', 'Check WinRM service', 'serviceManagement', 240, 0, { action: 'status', serviceName: 'WinRM' }, { targetMachineId: 'machine-web', outputVariable: 'services' }),
    node('disk', 'Check disk space', 'runScript', 240, 180, { script: "# Check free space on local fixed disks\n$disks = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3'\n\n$diskReport = $disks | Select-Object DeviceID,\n    @{Name='FreeGB'; Expression={[math]::Round($_.FreeSpace / 1GB, 1)}},\n    @{Name='FreePercent'; Expression={\n        [math]::Round(100 * $_.FreeSpace / $_.Size, 1)\n    }}\n\n$diskHealthy = @($diskReport | Where-Object FreePercent -lt 15).Count -eq 0\n$diskReport | Format-Table -AutoSize\nWrite-Output \"Disk capacity healthy: $diskHealthy\"" }, { targetMachineId: 'machine-web', outputVariable: 'disk' }),
    node('archive', 'File Copy · archive log', 'fileOperation', 240, 360, { operation: 'copy', path: 'C:\\NodePilot\\logs\\daily-health.log', destination: 'D:\\OpsArchive\\daily-health.log' }, { targetMachineId: 'machine-web', outputVariable: 'archive' }),
    node('join', 'Wait for all checks', 'junction', 500, 180, { mode: 'waitAll' }),
    node('summary', 'LLM · health summary', 'llmQuery', 730, 180, { prompt: 'Write a concise operations brief from these results. Service: {{services.output}}. Disk: {{disk.output}}. Log archive: {{archive.output}}.', systemPrompt: 'Summarize the supplied results. Highlight any issue that needs attention.', temperature: 0.2, maxTokens: 512 }, { outputVariable: 'report' }),
    node('return', 'Return report', 'returnData', 960, 180, { data: { report: '{{report.output}}' } }),
  ],
  edges: [['schedule','services'],['schedule','disk'],['schedule','archive'],['services','join'],['disk','join'],['archive','join'],['join','summary'],['summary','return']].map(([source,target],i) => ({
    id: `edge-${i}`, source, target, type: 'labeled', sourceHandle: 'right', targetHandle: 'left', data: { condition: `${source}.success`, label: 'On Success' },
  })),
};
const names = ['Morning Fleet Health Check', 'Nightly Backup', 'Service Recovery', 'Certificate Watch', 'Temp File Cleanup', 'Inventory Sync'];
export const workflows = names.map((name,i) => ({
  id: i === 0 ? WF : `workflow-${i}`, name,
  description: i === 0 ? 'Check a service and disk capacity, archive the daily log, then summarize the results with an LLM.' : 'Scheduled Windows operations with execution history.',
  definitionJson: JSON.stringify(definition), version: 4 + i, isEnabled: true,
  createdAt: iso(-30 * 86400000), updatedAt: iso(-(i + 1) * 60 * MIN), createdBy: USER.username, updatedBy: USER.username,
  activityCount: i === 0 ? 7 : 4 + i, triggerTypes: [i === 2 ? 'webhookTrigger' : 'scheduleTrigger'],
  successCount: [96,48,23,144,72,288][i], totalCount: [96,48,24,144,72,288][i], avgDurationMs: [12600,48000,8400,3100,2400,18000][i],
  folderId: ROOT, folderPath: '/', checkedOutByUserId: i === 0 ? USER.id : null,
  checkedOutByUserName: i === 0 ? USER.username : null, checkedOutAt: i === 0 ? iso(-5 * MIN) : null,
  capabilities: { canRead: true, canRun: true, canEdit: true, canDelete: true, canAdmin: true },
  lastExecution: { id: i === 0 ? EXEC : `run-${i}`, status: 'Succeeded', startedAt: iso(-(i+1)*3*MIN), completedAt: iso(-(i+1)*3*MIN+12600), durationMs: 12600 },
}));
export const executions = workflows.map((w,i) => ({
  id: i === 0 ? EXEC : `run-${i}`, workflowId: w.id, status: 'Succeeded',
  startedAt: iso(-(i+1)*3*MIN), completedAt: iso(-(i+1)*3*MIN+w.avgDurationMs),
  triggeredBy: 'schedule', startedByUsername: 'system', errorMessage: null, traceId: null, spanId: null,
  returnData: null, inputParametersJson: null, stepsTotal: w.activityCount, stepsCompleted: w.activityCount, failedSteps: [], parentExecutionId: null,
}));
export const steps = definition.nodes.map((n,i) => {
  const timings = [[0,30],[30,7400],[30,10600],[30,9800],[10600,10640],[10640,12400],[12400,12600]][i];
  return { id: `step-${n.id}`, stepId: n.id, stepName: n.data.label, stepType: n.data.activityType,
    targetMachine: n.data.targetMachineId ? 'WEB-DEMO-01' : null, status: 'Succeeded',
    startedAt: iso(-3*MIN+timings[0]), completedAt: iso(-3*MIN+timings[1]),
    output: ['Schedule accepted.','WinRM: Running','C: 142.6 GB free (57.1%)','Copied daily-health.log to D:\\OpsArchive\\daily-health.log','All three branches completed.','WinRM is running, disk capacity is healthy, and the daily log was archived. No action required.','Report returned.'][i],
    errorOutput: null, traceOutput: null, outputParametersJson: null,
  };
});
export const machines = ['WEB-DEMO-01','APP-DEMO-01','DB-DEMO-01','OPS-DEMO-01'].map((name,i) => ({
  id: i === 0 ? 'machine-web' : `machine-${i}`, name, hostname: `${name.toLowerCase()}.example.test`,
  winRmPort: 5986, useSsl: true, defaultCredentialId: null, tags: 'demo,windows', isReachable: true,
  lastConnectivityCheck: iso(-MIN), usedByWorkflowCount: 3+i, recentStepCount: 180+i*32, recentFailedStepCount: 0, activeRunCount: i < 2 ? 1 : 0,
}));
const buckets = Array.from({length:24},(_,i) => ({hourStart:iso(-(23-i)*60*MIN),succeeded:12+(i*7)%19,failed:i===7?1:0,cancelled:0}));
const succeeded = buckets.reduce((a,b)=>a+b.succeeded,0);
export const stats = {
  workflowsTotal: workflows.length, workflowsEnabled: workflows.length, machinesTotal: 4, machinesReachable: 4, executionsTotal: 8342,
  last24h: { total: succeeded+3, succeeded, failed:1, running:2, cancelled:0 }, last24hBuckets:buckets,
  topWorkflows: workflows.map(w=>({id:w.id,name:w.name,runCount:w.totalCount,successCount:w.successCount,failCount:w.totalCount-w.successCount,avgDurationMs:w.avgDurationMs,p95DurationMs:w.avgDurationMs*1.3})),
  running: workflows.slice(1,3).map((w,i)=>({id:`active-${i}`,workflowId:w.id,workflowName:w.name,status:'Running',startedAt:iso(-(3+i)*MIN),triggeredBy:'schedule'})),
  recent: executions.map((e,i)=>({...e,workflowName:workflows[i].name,durationMs:workflows[i].avgDurationMs})),
  armedTriggers: workflows.filter((_,i)=>i!==2).map((w,i)=>({workflowId:w.id,workflowName:w.name,triggerTypes:['scheduleTrigger'],nextFireUtc:iso((i+1)*7*MIN),nextFireKind:'cron',pollIntervalSeconds:null,blockedByWindowName:null})),
  pendingCount:0,runningCount:2,longRunningCount:0,failingWorkflows:[{id:'workflow-2',name:'Service Recovery',failCount:1,runCount:24,lastFailureAt:iso(-9*60*MIN)}],editLocks:[],
  healthHeartbeats:['Scheduler','TriggerOrchestrator','NotificationDispatcher'].map(serviceName=>({serviceName,lastHeartbeatAt:iso(-1000),expectedIntervalSeconds:60,status:'ok',isStale:false})),
  databaseProvider:'PostgreSQL',clusterRole:null,recentAudit:[],llmEnabled:true,
};
export const operations = {
  nodes: workflows.map((w,i)=>({workflowId:w.id,name:w.name,folderId:ROOT,folderPath:'/',isEnabled:true,runningCount:i===1||i===2?1:0,lastStatus:'Succeeded',callFrequency:12+i*3,canRun:true,canEdit:true})),
  edges: [], density: [],
  running: stats.running.map((r,i)=>({executionId:r.id,workflowId:r.workflowId,status:r.status,startedAt:r.startedAt,parentExecutionId:null,stepsFinished:i===0?4:2,lastCompletedStepName:i===0?'Verify archive':'Check service state',lastProgressAt:iso(-7000),activeStepCount:1})),
  recent: workflows.flatMap((w,i)=>[8,16,24].map((m,j)=>({executionId:`recent-${i}-${j}`,workflowId:w.id,status:'Succeeded',startedAt:iso(-(m+i*.2)*MIN),completedAt:iso(-(m-1.6+i*.2)*MIN),parentExecutionId:null}))),
  meta:{overdueSeconds:900,windowMinutes:30,recentSinceUtc:iso(-30*MIN),oldestReturnedCompletedAt:iso(-24*MIN),recentTruncated:false,densityBucketSeconds:0,densityCapped:false},
};
export function responseFor(url) {
  const p = url.pathname;
  if(p==='/api/auth/me') return USER;
  if(p==='/api/ai/knowledge/capabilities') return {enabled:true,llm:true,docs:true,operational:true,sourceCode:false,db:true,scriptContextTargetHost:'localhost'};
  if(p==='/api/system/host-info') return {machineName:'NODEPILOT-DEMO',fqdn:'nodepilot.example.test',domain:'example.test',appVersion:'1.2.27'};
  if(p==='/api/stats/dashboard') return stats;
  if(p==='/api/workflows') return workflows;
  if(p===`/api/workflows/${WF}`) return workflows[0];
  if(p===`/api/workflows/${WF}/step-health`) return Object.fromEntries(definition.nodes.map(n=>[n.id,Array.from({length:6},()=>({status:'Succeeded'}))]));
  if(p===`/api/workflows/${WF}/step-stats`) return {};
  if(p==='/api/machines') return machines;
  if(p==='/api/operations/graph') return operations;
  if(p==='/api/executions') return url.searchParams.has('workflowId') ? [executions[0]] : executions;
  if(p===`/api/executions/${EXEC}/steps`) return steps;
  if(p===`/api/executions/${EXEC}`) return executions[0];
  if(p.startsWith('/api/executions/active-')) {
    const i = Number(p.slice(-1)); const r = stats.running[i];
    return {...executions[i+1],id:r.id,status:'Running',startedAt:r.startedAt,completedAt:null,stepsCompleted:i===0?4:2};
  }
  if(p==='/api/observability/config') return {enabled:false,prometheusAvailable:false};
  return undefined;
}
