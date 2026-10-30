import { useCallback, useMemo } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../api/client';
import { aiApi, MAX_UPSTREAM_VARIABLES, type AiUpstreamVariable } from '../api/ai';
import type { UpstreamVariable } from '../lib/upstreamVariables';
import { useAiCapabilities, AI_CAPABILITIES_QUERY_KEY } from './useAiCapabilities';
import { useRole } from '../lib/rbac';

export interface AiScriptStreamBinding {
  generate: (
    prompt: string,
    currentScript: string | null,
    onToken: (text: string) => void,
    signal: AbortSignal,
    expectedRecipientKey: string | null,
  ) => Promise<void>;
  targetHost: string | null;
  recipientKey: string | null;
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
  const queryClient = useQueryClient();
  const llmUsable = capabilities?.llm === true;
  const { isViewer } = useRole();
  const callback = useCallback(
    async (prompt: string, currentScript: string | null, onToken: (text: string) => void, signal: AbortSignal, expectedRecipientKey: string | null) => {
      const capped: AiUpstreamVariable[] = upstreamVars
        .slice(0, MAX_UPSTREAM_VARIABLES)
        .map((v) => ({
          stepId: v.stepId,
          label: v.label,
          variable: v.variable,
          expression: v.expression,
          type: v.type,
        }));
      try { await aiApi.generateScriptStream(
        {
          prompt,
          workflowId: workflowId ?? null,
          stepId: stepId ?? null,
          upstreamVariables: capped,
          currentScript: currentScript || null,
          includeCurrentScript: !!currentScript,
          expectedRecipientKey: currentScript ? expectedRecipientKey : null,
        },
        { onDelta: onToken, signal },
      ); } catch (error) {
        if (error instanceof ApiError && error.code === 'LLM_RECIPIENT_CHANGED') {
          await queryClient.invalidateQueries({ queryKey: AI_CAPABILITIES_QUERY_KEY });
        }
        throw error;
      }
    },
    [workflowId, stepId, upstreamVars, queryClient],
  );

  return useMemo(() => llmUsable && !isViewer
    ? { generate: callback, targetHost: capabilities?.scriptContextTargetHost ?? null, recipientKey: capabilities?.scriptContextRecipientKey ?? null }
    : undefined,
  [llmUsable, isViewer, callback, capabilities?.scriptContextTargetHost, capabilities?.scriptContextRecipientKey]);
}
