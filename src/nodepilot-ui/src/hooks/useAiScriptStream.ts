import { useCallback, useMemo } from 'react';
import { aiApi, MAX_UPSTREAM_VARIABLES, type AiUpstreamVariable } from '../api/ai';
import type { UpstreamVariable } from '../lib/upstreamVariables';
import { useAiCapabilities } from './useAiCapabilities';
import { useRole } from '../lib/rbac';

export interface AiScriptStreamBinding {
  generate: (
    prompt: string,
    currentScript: string | null,
    onToken: (text: string) => void,
    signal: AbortSignal,
  ) => Promise<void>;
  targetHost: string | null;
}

/**
 * Builds the streaming binding for both runScript editors. The current editor content is left
 * out by default; the dialog supplies it only after an explicit, one-shot consent. The request
 * carries the matching server-enforced flag, so sending a value alone cannot opt in.
 */
export function useAiScriptStream(opts: {
  workflowId?: string;
  stepId?: string;
  upstreamVars: UpstreamVariable[];
}): AiScriptStreamBinding | undefined {
  const { workflowId, stepId, upstreamVars } = opts;
  const capabilities = useAiCapabilities().data;
  const llmUsable = capabilities?.llm === true;
  const { isViewer } = useRole();
  const callback = useCallback(
    async (prompt: string, currentScript: string | null, onToken: (text: string) => void, signal: AbortSignal) => {
      const capped: AiUpstreamVariable[] = upstreamVars
        .slice(0, MAX_UPSTREAM_VARIABLES)
        .map((v) => ({
          stepId: v.stepId,
          label: v.label,
          variable: v.variable,
          expression: v.expression,
          type: v.type,
        }));
      await aiApi.generateScriptStream(
        {
          prompt,
          workflowId: workflowId ?? null,
          stepId: stepId ?? null,
          upstreamVariables: capped,
          currentScript: currentScript || null,
          includeCurrentScript: !!currentScript,
        },
        { onDelta: onToken, signal },
      );
    },
    [workflowId, stepId, upstreamVars],
  );

  return useMemo(() => llmUsable && !isViewer
    ? { generate: callback, targetHost: capabilities?.scriptContextTargetHost ?? null }
    : undefined,
  [llmUsable, isViewer, callback, capabilities?.scriptContextTargetHost]);
}
