export interface AgentToolSelection {
  name: string;
  allowedPaths?: string[];
  allowedHosts?: string[];
  workflowIds?: string[];
  mcpServerId?: string;
  mcpToolName?: string;
}

export interface AgentDefinition {
  id: string;
  role: string;
  instructions: string;
  model?: string;
  isSupervisor?: boolean;
  isReviewer?: boolean;
  targetMachineId?: string | null;
  credentialId?: string | null;
  useServiceIdentity?: boolean;
  workingDirectory?: string;
  bashPath?: string;
  tools: AgentToolSelection[];
  skillIds: string[];
}

export interface AgentRun {
  id: string;
  workflowExecutionId: string;
  stepId: string;
  status: string;
  startedAt: string;
  completedAt: string | null;
  result: string | null;
  error: string | null;
  modelCalls: number;
  toolCalls: number;
  delegations: number;
  inputTokens: number | null;
  outputTokens: number | null;
  outcome?: 'completed' | 'partial' | 'blocked' | 'unassessed';
  outcomeReason?: string | null;
}

export interface AgentRunEvent {
  agentRunId: string;
  sequence: number;
  timestamp: string;
  memberId: string | null;
  kind: string;
  toolName: string | null;
  content: string;
}

export interface AgentEventNotification extends AgentRunEvent { executionId: string; stepId: string }

export interface AgentMcpServer {
  id: string;
  name: string;
  enabled: boolean;
  transport: 'stdio' | 'streamableHttp';
  command: string | null;
  arguments: string[];
  endpoint: string | null;
  hasSecrets: boolean;
  updatedAt: string;
}
export interface AgentMcpTool { name: string; description: string | null; schema: unknown; readOnly: boolean; contractSha256: string }
export interface AgentMcpReadGrant { serverId: string; toolName: string; serverUpdatedAt: string; contractSha256: string }
export interface AgentSkill {
  id: string; name: string; version: string; description: string;
  sha256: string; enabled: boolean; createdAt: string;
}
