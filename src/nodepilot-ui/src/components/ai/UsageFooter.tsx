import { useTranslation } from 'react-i18next';
import type { ChatDoneMeta } from '../../api/ai';

/**
 * One-line footer under a finished assistant turn: model, duration, token usage and generation
 * throughput. Shared by the global AI chat page and the workflow designer's assistant panel —
 * both render the exact same line.
 *
 * The tok/s figure divides completion tokens by `generationMs`, never by `durationMs`. The wall
 * clock also covers prompt prefill and tool execution; with a multi-thousand-token system prompt
 * those dominate completely, so dividing by them reported a small fraction of the model's real
 * decode speed (0.2 tok/s where the server was doing ~50). Very short answers still read noisy —
 * the measurement window is only a few milliseconds wide there — but they are in the right
 * order of magnitude, which the old figure never was.
 */
export function UsageFooter({ meta }: Readonly<{ meta: ChatDoneMeta }>) {
  const { t } = useTranslation(['ai']);

  const tokens = meta.promptTokens != null && meta.completionTokens != null
    ? meta.promptTokens + meta.completionTokens
    : null;
  const tpsValue = meta.completionTokens != null && meta.generationMs != null && meta.generationMs > 0
    ? meta.completionTokens / (meta.generationMs / 1000)
    : null;
  const tps = tpsValue != null ? (tpsValue < 10 ? tpsValue.toFixed(1) : Math.round(tpsValue).toString()) : null;

  let label: string;
  if (tokens != null && tps != null) {
    label = t('ai:chat.usageTokensTps', {
      model: meta.model, ms: meta.durationMs, genMs: meta.generationMs, tokens, tps,
    });
  } else if (tokens != null) {
    // No generation window reported (endpoint streamed nothing measurable) — drop the rate
    // rather than fall back to the wall clock, which would be the old, misleading number.
    label = t('ai:chat.usageTokens', { model: meta.model, ms: meta.durationMs, tokens });
  } else {
    label = t('ai:chat.usageNoTokens', { model: meta.model, ms: meta.durationMs });
  }

  return (
    <span className="ml-1 select-none text-[10px] text-on-surface-variant/70" title={t('ai:chat.usageTitle')}>
      {label}
    </span>
  );
}
