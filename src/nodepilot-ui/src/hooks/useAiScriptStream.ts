import { useCallback } from 'react';
import { aiApi, MAX_UPSTREAM_VARIABLES, type AiUpstreamVariable } from '../api/ai';
import type { UpstreamVariable } from '../lib/upstreamVariables';

/**
 * Builds the streaming `onAiGenerate` callback for the ScriptEditorDialog. Shared between
 * the two call sites (RunScriptConfig in the properties panel + the runScript double-click
 * editor in EditorOverlays) so the upstream-variable logic and the SSE call aren't duplicated.
 * The callback streams the response: `onToken` is called per token, `signal` aborts the stream.
 */
export function useAiScriptStream(opts: {
  workflowId?: string;
  stepId?: string;
  upstreamVars: UpstreamVariable[];
}): (prompt: string, currentScript: string, onToken: (text: string) => void, signal: AbortSignal) => Promise<void> {
  const { workflowId, stepId, upstreamVars } = opts;
  return useCallback(
    async (prompt, currentScript, onToken, signal) => {
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
          currentScript: currentScript || null, // empty editor → don't include a script block in the prompt
        },
        { onDelta: onToken, signal },
      );
    },
    [workflowId, stepId, upstreamVars],
  );
}
