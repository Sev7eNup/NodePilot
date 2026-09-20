import { demoId } from '../state/ids';
import type { GraphNode } from '../state/world';
import type { StepOutcome } from './outcomes';

export const FILE_WORKFLOW_ID = demoId('workflow:guided-file');
export const FILE_DEFAULTS = { fileName: 'settings.json', pollIntervalSeconds: '30', environment: 'Test' };
export const FILE_FAILURE_INPUTS = { ...FILE_DEFAULTS, environment: 'Protected' };
export interface FileOutcome extends StepOutcome { error?: string }

export function fileInputError(parameters: Record<string, string>): string | null {
  const inputs = { ...FILE_DEFAULTS, ...parameters };
  if (!/^[a-z][a-z0-9_-]{0,39}\.json$/i.test(inputs.fileName) || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])\./i.test(inputs.fileName)) return 'Use a file name such as settings.json (letters, digits, _ or -; no reserved Windows names).';
  const interval = Number(inputs.pollIntervalSeconds);
  if (!Number.isInteger(interval) || interval < 5 || interval > 300) return 'pollIntervalSeconds must be a whole number between 5 and 300.';
  if (!['Test', 'Production', 'Protected'].includes(inputs.environment)) return 'environment must be Test, Production or Protected.';
  return null;
}

/** Outcomes for the guided fixture; values are derived from this run's submitted parameters. */
export function fileOutcome(node: GraphNode, parameters: Record<string, string>, bus: Record<string, string>): FileOutcome | null {
  const inputs = { ...FILE_DEFAULTS, ...parameters };
  const resolve = (value: unknown) => String(value ?? '').replace(/\{\{([^{}]+)\}\}/g, (match, key: string) => bus[key] ?? match);
  const config = node.data?.config ?? {};
  const result = (output: string, outputParameters: Record<string, string>, error?: string): FileOutcome => ({ output, outputParameters, durationMs: 1800, error });
  switch (node.data?.activityType) {
    case 'manualTrigger': return result('Manual parameters accepted.', inputs);
    case 'registryOperation': {
      const directory = inputs.environment === 'Protected' ? 'C:\\Windows\\System32\\NodePilotDemo' : `C:\\Apps\\FileWorker\\${inputs.environment}\\config`;
      return result(`ConfigDirectory = ${directory}`, { value: directory, type: 'String' });
    }
    case 'serviceManagement': return result('NodePilot.FileWorker is Running.', { name: 'NodePilot.FileWorker', status: 'Running', startType: 'Automatic' });
    case 'runScript': {
      const sourcePath = `C:\\Staging\\${inputs.fileName}`;
      const content = JSON.stringify({ environment: inputs.environment, pollIntervalSeconds: Number(inputs.pollIntervalSeconds), service: 'NodePilot.FileWorker' }, null, 2);
      return result(content, { sourcePath, destinationPath: `${bus['registry.param.value']}\\${inputs.fileName}`, content, exitCode: '0' });
    }
    case 'fileOperation': {
      const path = resolve(config.path), destination = resolve(config.destination);
      if (destination.startsWith('C:\\Windows\\System32\\')) return result('', {}, `Access denied: ${destination}. The demo execution account has no write permission on the destination directory.`);
      return result(`Copied ${path} -> ${destination}`, { operation: 'copy', path, destination });
    }
    case 'log': { const message = resolve(config.message); return result(message, { level: resolve(config.level), message }); }
    case 'returnData': {
      const data = Object.fromEntries(Object.entries(config.data as Record<string, unknown> ?? {}).map(([key, value]) => [key, resolve(value)]));
      return result(JSON.stringify(data, null, 2), data);
    }
    default: return null;
  }
}

export function publishFileOutcome(bus: Record<string, string>, node: GraphNode, outcome: StepOutcome): void {
  bus[`${node.id}.output`] = outcome.output;
  for (const [key, value] of Object.entries(outcome.outputParameters)) {
    bus[`${node.id}.param.${key}`] = value;
    if (node.data?.activityType === 'manualTrigger') bus[`manual.${key}`] = value;
  }
}
