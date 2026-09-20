/**
 * Custom activities, stored as their FULL definitions.
 *
 * The world keeps the whole thing — script included — because the app reads two different views
 * of it: the palette catalog from the list endpoint, and the complete definition from the single
 * read that the edit dialog loads. Storing only the catalog is what made "Edit" throw.
 */
import { demoId } from '../state/ids';

export interface DemoCustomActivityInput {
  name: string;
  label: string;
  type: 'string' | 'number' | 'boolean' | 'select' | 'multiline';
  required?: boolean;
  default?: string | null;
  options?: string[] | null;
  description?: string | null;
}

export interface DemoCustomActivityOutput {
  name: string;
  type: 'string' | 'number' | 'boolean' | 'object' | 'array';
}

/** Mirrors `FullDef` in CustomActivitiesPage: the catalog entry plus the authoring fields. */
export interface DemoCustomActivityDefinition {
  id: string;
  key: string;
  name: string;
  description: string | null;
  icon: string;
  color: string | null;
  runsRemote: boolean;
  inputs: DemoCustomActivityInput[];
  outputs: DemoCustomActivityOutput[];
  isEnabled: boolean;
  version: number;
  scriptTemplate: string;
  engine: string;
  isolated: boolean;
  memoryLimitMb: number | null;
  maxProcesses: number | null;
  defaultTimeoutSeconds: number | null;
  successExitCodes: string | null;
  concurrencyToken: string;
  updatedAt: string;
  updatedBy: string | null;
}

/** The wire shape of a single read: the definition plus the derived `type`. */
export function fullDefinitionOf(definition: DemoCustomActivityDefinition) {
  return { ...definition, type: `custom:${definition.key}`, timeout: 'always' };
}

const DRAIN_SCRIPT = `param([string]$NodeName, [int]$GraceSeconds = 300)

Write-Output "Cordoning $NodeName"
$sessions = Get-RDUserSession -CollectionName 'Default' -ErrorAction SilentlyContinue |
  Where-Object { $_.HostServer -eq $NodeName }

foreach ($session in $sessions) {
  Send-RDUserMessage -HostServer $NodeName -UnifiedSessionID $session.UnifiedSessionID \`
    -MessageTitle 'Maintenance' -MessageBody "This host goes offline in $GraceSeconds seconds."
}

Start-Sleep -Seconds $GraceSeconds
$drained = $true
$sessionsClosed = @($sessions).Count
`;

const CERT_SCRIPT = `param([string]$StoreName = 'My')

$certs = Get-ChildItem -Path "Cert:\\LocalMachine\\$StoreName" |
  Sort-Object NotAfter

$soonest = $certs | Select-Object -First 1
$thumbprint = $soonest.Thumbprint
$daysRemaining = [int](($soonest.NotAfter - (Get-Date)).TotalDays)

Write-Output "Soonest expiry: $($soonest.Subject) in $daysRemaining day(s)"
`;

export function buildCustomActivities(now: number): DemoCustomActivityDefinition[] {
  const updatedAt = new Date(now - 9 * 24 * 60 * 60_000).toISOString();
  return [
    {
      id: demoId('custom:drain-node'),
      key: 'drain-node',
      name: 'Drain Cluster Node',
      description: 'Cordons a node and waits for its sessions to end before maintenance.',
      icon: 'server',
      color: null,
      runsRemote: true,
      inputs: [
        { name: 'nodeName', label: 'Node name', type: 'string', required: true, default: null },
        { name: 'graceSeconds', label: 'Grace period (s)', type: 'number', required: false, default: '300' },
      ],
      outputs: [
        { name: 'drained', type: 'boolean' },
        { name: 'sessionsClosed', type: 'number' },
      ],
      isEnabled: true,
      version: 3,
      scriptTemplate: DRAIN_SCRIPT,
      engine: 'powershell',
      isolated: false,
      memoryLimitMb: null,
      maxProcesses: null,
      defaultTimeoutSeconds: 900,
      successExitCodes: null,
      concurrencyToken: demoId('custom-token:drain-node'),
      updatedAt,
      updatedBy: 'demo',
    },
    {
      id: demoId('custom:certificate-expiry'),
      key: 'certificate-expiry',
      name: 'Certificate Expiry Probe',
      description: 'Reads the local certificate store and reports the soonest expiry.',
      icon: 'shield',
      color: null,
      runsRemote: true,
      inputs: [
        { name: 'storeName', label: 'Store', type: 'select', required: true, default: 'My', options: ['My', 'WebHosting', 'Root'] },
      ],
      outputs: [
        { name: 'thumbprint', type: 'string' },
        { name: 'daysRemaining', type: 'number' },
      ],
      isEnabled: true,
      version: 1,
      scriptTemplate: CERT_SCRIPT,
      engine: 'powershell',
      isolated: false,
      memoryLimitMb: null,
      maxProcesses: null,
      defaultTimeoutSeconds: 120,
      successExitCodes: null,
      concurrencyToken: demoId('custom-token:certificate-expiry'),
      updatedAt,
      updatedBy: 'demo',
    },
  ];
}
