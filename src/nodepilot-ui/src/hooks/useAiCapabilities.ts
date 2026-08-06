import { useQuery, type QueryClient } from '@tanstack/react-query';
import { getKnowledgeCapabilities } from '../api/ai';

export const AI_CAPABILITIES_QUERY_KEY = ['ai-knowledge-capabilities'] as const;

/**
 * Effective AI capabilities for the current user — the single source for gating AI entry points:
 * `data?.llm` hides/shows the designer assistant, the script-editor generate button and the AI
 * workflow generation; `data?.enabled` gates the AI-Chat nav entry and page.
 *
 * While the query is loading (or failed), `data` is undefined, so `data?.llm`-style gates resolve
 * to false and the buttons stay hidden — same semantics the sidebar nav always had.
 *
 * The query stays permanently active through the Sidebar, and the app disables
 * `refetchOnWindowFocus` globally — without an interval it would never refetch after the first
 * load. The moderate `refetchInterval` makes out-of-SPA config changes (direct file edits, a
 * second admin session) converge within ≤60 s; in-SPA settings saves refresh immediately via
 * {@link refreshAiCapabilities}. One request per minute per visible tab is well inside the
 * `ai-generate` rate limit (20/min/IP).
 */
export function useAiCapabilities() {
  return useQuery({
    queryKey: AI_CAPABILITIES_QUERY_KEY,
    queryFn: getKnowledgeCapabilities,
    staleTime: 60_000,
    refetchInterval: 60_000,
  });
}

/**
 * Call after saving a settings section that changes AI availability (`Llm`, `AiKnowledge`).
 * Invalidates twice: once immediately, and once after the backend's `appsettings.runtime.json`
 * file watcher has certainly fired — the PUT returns after *writing* the file, but IOptionsMonitor
 * reloads asynchronously, so an immediate refetch can still read the old values.
 */
export function refreshAiCapabilities(queryClient: QueryClient): void {
  queryClient.invalidateQueries({ queryKey: AI_CAPABILITIES_QUERY_KEY });
  setTimeout(() => {
    queryClient.invalidateQueries({ queryKey: AI_CAPABILITIES_QUERY_KEY });
  }, 2000);
}
