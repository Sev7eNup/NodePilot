/**
 * Plausible step results per activity type.
 *
 * Keyed by activity type, never by node id: a visitor who adds, renames or deletes nodes
 * still gets sensible output, and a workflow they build from scratch behaves like the seed
 * ones. This is the only place in the demo that holds invented run data.
 */

export interface StepOutcome {
  output: string;
  outputParameters: Record<string, string>;
  /** Rough step duration in milliseconds, used for both history and the live run. */
  durationMs: number;
}

type OutcomeSpec = Omit<StepOutcome, 'durationMs'> & { durationMs?: number };

const OUTCOMES: Record<string, OutcomeSpec> = {
  manualTrigger: { output: 'Triggered manually by demo.', outputParameters: {}, durationMs: 40 },
  scheduleTrigger: { output: 'Schedule fired (0 30 2 * * ?).', outputParameters: {}, durationMs: 40 },
  webhookTrigger: { output: 'Webhook received.', outputParameters: {}, durationMs: 40 },
  fileWatcherTrigger: { output: 'File created: \\\\file01\\drop\\report.csv', outputParameters: { path: '\\\\file01\\drop\\report.csv' }, durationMs: 40 },
  databaseTrigger: { output: 'Sentinel changed: 4821 -> 4822.', outputParameters: { sentinel: '4822' }, durationMs: 60 },
  eventLogTrigger: { output: 'Event 7031 written to System log.', outputParameters: { eventId: '7031' }, durationMs: 40 },

  runScript: {
    output: 'PowerShell completed.\nUptime: 14 days, 6 hours\nFree space on C: 82.4 GB',
    outputParameters: { exitCode: '0', uptimeDays: '14', freeSpaceGb: '82.4' },
    durationMs: 1800,
  },
  log: { output: 'Log entry written.', outputParameters: {}, durationMs: 30 },
  delay: { output: 'Waited 5 seconds.', outputParameters: {}, durationMs: 5000 },
  restApi: {
    output: '{"status":"ok","itemCount":42}',
    outputParameters: { statusCode: '200', itemCount: '42' },
    durationMs: 420,
  },
  sql: { output: '3 row(s) returned.', outputParameters: { rowCount: '3' }, durationMs: 260 },
  emailNotification: { output: 'Mail queued for ops@contoso.example.', outputParameters: {}, durationMs: 380 },
  jsonQuery: { output: 'healthy', outputParameters: { value: 'healthy' }, durationMs: 25 },
  xmlQuery: { output: '1.4.2', outputParameters: { value: '1.4.2' }, durationMs: 25 },
  generateText: { output: 'Summary generated (182 words).', outputParameters: {}, durationMs: 2400 },
  llmQuery: { output: 'All three probes report nominal values; no action required.', outputParameters: {}, durationMs: 2600 },

  serviceManagement: { output: 'wuauserv is Running (StartType: Manual).', outputParameters: { status: 'Running', startType: 'Manual' }, durationMs: 640 },
  registryOperation: { output: 'LastSuccessTime = 2026-09-17 02:31:14', outputParameters: { value: '2026-09-17 02:31:14', type: 'String', exists: 'true' }, durationMs: 210 },
  wmiQuery: { output: '4 instance(s) returned.', outputParameters: { count: '4' }, durationMs: 730 },
  fileOperation: { output: 'Copied 1 file (2.1 MB).', outputParameters: { bytesCopied: '2202009' }, durationMs: 540 },
  folderOperation: { output: 'Folder created.', outputParameters: { created: 'true' }, durationMs: 180 },
  textFileEdit: { output: '2 line(s) replaced.', outputParameters: { replacements: '2' }, durationMs: 150 },
  fileHash: { output: 'SHA256: 9f2c4b1e8a7d6350c1fb4a2e9d0c8b7a6f5e4d3c2b1a09f8e7d6c5b4a3928170', outputParameters: { hash: '9f2c4b1e8a7d6350c1fb4a2e9d0c8b7a6f5e4d3c2b1a09f8e7d6c5b4a3928170' }, durationMs: 310 },
  zipOperation: { output: 'Archive created: reports-2026-09.zip (18.7 MB).', outputParameters: { entryCount: '124' }, durationMs: 2100 },
  startProgram: { output: 'Process exited with code 0.', outputParameters: { exitCode: '0' }, durationMs: 900 },
  scheduledTask: { output: 'Task "NightlyReport" is Ready.', outputParameters: { state: 'Ready' }, durationMs: 340 },
  powerManagement: { output: 'Restart scheduled in 60 seconds.', outputParameters: {}, durationMs: 260 },
  waitForCondition: { output: 'Condition met after 12 s.', outputParameters: { waitedSeconds: '12' }, durationMs: 12000 },

  junction: { output: 'All 3 inbound branches completed.', outputParameters: {}, durationMs: 20 },
  decision: { output: 'Branch: healthy', outputParameters: { branch: 'healthy' }, durationMs: 20 },
  forEach: { output: 'Processed 6 item(s).', outputParameters: { itemCount: '6' }, durationMs: 3400 },
  startWorkflow: { output: 'Child workflow Succeeded.', outputParameters: { __status: 'Succeeded' }, durationMs: 4200 },
  returnData: { output: 'Returned 4 key(s).', outputParameters: {}, durationMs: 20 },
};

const FALLBACK: OutcomeSpec = { output: 'Completed.', outputParameters: {}, durationMs: 400 };

/** Result for one step. Custom activities answer with their key so the output reads sensibly. */
export function outcomeFor(activityType: string | undefined): StepOutcome {
  if (activityType?.startsWith('custom:')) {
    const key = activityType.slice('custom:'.length);
    return { output: `Custom activity "${key}" completed.`, outputParameters: { exitCode: '0' }, durationMs: 1500 };
  }
  const spec = (activityType && OUTCOMES[activityType]) || FALLBACK;
  return { output: spec.output, outputParameters: spec.outputParameters, durationMs: spec.durationMs ?? 400 };
}

/** Error text for a step the demo marks as failed. */
export function failureFor(activityType: string | undefined): string {
  if (activityType === 'restApi') return 'The remote server returned 503 (Service Unavailable).';
  if (activityType === 'sql') return 'Timeout expired before the operation completed.';
  if (activityType === 'serviceManagement') return "Service 'wuauserv' did not reach state 'Running' within 30 s.";
  return 'Connecting to remote server lab01.contoso.example failed: WinRM cannot complete the operation.';
}
