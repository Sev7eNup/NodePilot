// Display fixtures only. Addresses are reserved examples; secrets are never real.
const now = Date.now();
const iso = ms => new Date(now + ms).toISOString();
const names = ['Morning Fleet Health Check', 'Nightly Backup', 'Service Recovery', 'Certificate Watch', 'Temp File Cleanup', 'Inventory Sync'];
export const supportEvents = Array.from({ length: 34 }, (_, i) => {
  const isWarn = i === 5;
  const isFailure = i === 12;
  const eventType = isFailure ? 'STEP_FAILED' : i % 4 === 0 ? 'EXECUTION_SUCCEEDED' : i % 4 === 1 ? 'USER_LOG' : i % 4 === 2 ? 'EXECUTION_STARTED' : 'USER_LOG';
  const message = isWarn ? 'Certificate expires in 14 days; renewal scheduled.' : isFailure ? 'Service check timed out; retry queued.' : [
    'Workflow completed successfully. Health report published.',
    'All checks passed: services, disk capacity and event log.',
    'Scheduled execution accepted by the workflow engine.',
    'WinRM connection established; running PowerShell activity.',
  ][i % 4];
  return {
    id: `demo-event-${i}`, timestamp: iso(-i * 4200), level: isWarn ? 3 : isFailure ? 4 : 2,
    eventType, message, workflowId: `workflow-${i%6}`, workflowName: names[i%6],
    executionId: `demo-execution-${i%6}`, executionShort: `demo-${i%6}`,
    stepId: i%4===1 ? 'disk' : null, stepLabel: i%4===1 ? 'Check disk space' : null,
    activityType: i%4===1 ? 'runScript' : null, userName: 'demo-admin', userId: null,
    traceId: 'aabbccddeeff00112233445566778899', spanId: '0011223344556677',
    propertiesJson: JSON.stringify({ environment: 'demo', targetMachine: 'WEB-DEMO-01', durationMs: 12600 }),
  };
});

const sections = {
  Smtp: { host: 'mail.example.test', port: 587, username: 'nodepilot-demo', password: null, from: 'nodepilot@example.test', enableSsl: true },
  Llm: {
    enabled: true, activeProfileId: 'local',
    profiles: [
      { id: 'local', name: 'Local Ollama', baseUrl: 'http://localhost:11434/v1', apiKey: null, model: 'qwen3:8b', maxTokens: 8192, timeoutSeconds: 120, enableToolCalling: true, toolCallMaxDepth: 6, managedBy: null },
      { id: 'cloud', name: 'Cloud API', baseUrl: 'https://llm.example.test/v1', apiKey: '***', model: 'demo-model', maxTokens: 8192, timeoutSeconds: 90, enableToolCalling: true, toolCallMaxDepth: 6, managedBy: null },
    ],
    proxy: { mode: 'off', address: '', bypassList: [], username: null, password: null, useDefaultCredentials: false },
  },
  Authentication: {
    ldap: { enabled: true, server: 'dc01.example.test', endpoints: ['dc01.example.test', 'dc02.example.test'], port: 636, useSsl: true,
      baseDn: 'DC=example,DC=test', upnSuffix: 'example.test', bindTimeoutSeconds: 5,
      serviceBindDn: 'CN=svc-nodepilot,OU=Services,DC=example,DC=test', servicePassword: '***',
      allowedGroupSids: ['S-1-5-21-100000001-100000002-100000003-1100'], directorySyncIntervalMinutes: 5, directorySyncMaxConcurrency: 16,
      globalRoleMappings: [{groupSid:'S-1-5-21-100000001-100000002-100000003-1100',role:'Operator'}], jitUserDefaultRootRole: 'FolderViewer' },
    windows: { enabled: true, allowNtlmFallback: false, ntlmDisabledByPolicy: true },
    oidc: { enabled: true, authority: 'https://login.example.test', clientId: 'nodepilot-demo', clientSecret: '***', displayName: 'Company SSO',
      nameClaimType: 'preferred_username', groupsClaimType: 'groups', scopes: ['openid','profile','email'],
      allowedGroupIds: ['nodepilot-operators'], globalRoleMappings: [{groupId:'nodepilot-operators',role:'Operator'}] },
    scim: { enabled: false, bearerToken: null, previousBearerToken: null, authority: null },
    localLoginMode: 'BreakGlassOnly', sessionAbsoluteLifetimeHours: 8, maxAuthorizationStalenessMinutes: 15,
  },
};
export function settingsAndLogResponse(url) {
  if (url.pathname === '/api/diagnostics/support-events') {
    const type = url.searchParams.get('eventType');
    return { items: supportEvents.filter(e => !type || e.eventType === type), nextCursor: null, hasMore: false };
  }
  if (url.pathname === '/api/diagnostics/support-log') {
    const lines = [...supportEvents].reverse().map(e => `${e.timestamp.replace('T',' ').slice(0,23)} [${e.level===4?'ERR ':e.level===3?'WARN':'INFO'}] [${e.workflowName}] ${e.message}`);
    return { file: `C:/NodePilot/logs/nodepilot-support-${iso(0).slice(0,10)}.log`, lines, lineCount: lines.length };
  }
  if (url.pathname === '/api/admin/settings/status') return { overridesPath:'C:/NodePilot/demo/overrides.json',restartRequired:false,restartRequiredSince:null,restartRequiredFor:[],lastSavedAt:null,lastSavedBy:null };
  const sectionPath = url.pathname.replace('/api/admin/settings/','');
  if (url.pathname.startsWith('/api/admin/settings/') && Object.hasOwn(sections,sectionPath)) {
    return { sectionPath, payload: sections[sectionPath], etag:'"demo-v1"',isHotReloadable:sectionPath!=='Authentication',effectiveSource:{} };
  }
  return undefined;
}
