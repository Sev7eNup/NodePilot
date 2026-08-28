import { useQuery, type QueryClient } from '@tanstack/react-query';
import { getKnowledgeCapabilities } from '../api/ai';

export const AI_CAPABILITIES_QUERY_KEY = ['ai-knowledge-capabilities'] as const;

/**
 * Effective AI capabilities for the current user; the single source for gating AI entry points.
 * `data?.llm` gates the designer assistant, the script-editor generate button and AI workflow
 * generation, `data?.enabled` gates the AI-Chat nav entry and page. While the query loads or
 * after it fails, `data` is undefined, so every gate resolves to false and the entry points stay
 * hidden.
 *
 * The Sidebar keeps this query mounted and the app disables `refetchOnWindowFocus` globally, so
 * without the interval it would never refetch. The interval lets config changes made outside the
 * SPA converge; in-SPA settings saves refresh right away via {@link refreshAiCapabilities}.
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
 * Invalidates twice: immediately, and again once the backend's `appsettings.runtime.json` file
 * watcher has fired. The PUT returns after the file is written, but IOptionsMonitor reloads
 * asynchronously, so an immediate refetch can still read the old values.
 */
export function refreshAiCapabilities(queryClient: QueryClient): void {
  queryClient.invalidateQueries({ queryKey: AI_CAPABILITIES_QUERY_KEY });
  setTimeout(() => {
    queryClient.invalidateQueries({ queryKey: AI_CAPABILITIES_QUERY_KEY });
  }, 2000);
}
