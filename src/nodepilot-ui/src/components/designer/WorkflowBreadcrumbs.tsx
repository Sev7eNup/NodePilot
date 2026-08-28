import { ChevronRight, FlowModeler, WarningAltFilled } from '@carbon/icons-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import type { Node } from '@xyflow/react';
import { api } from '../../api/client';
import type { Workflow } from '../../types/api';

export interface WorkflowCallRef {
  sourceLabel: string;
  refName: string;
  target: Workflow | null;
}

/**
 * Derives the outgoing static workflow references of the current workflow:
 * `startWorkflow.config.workflowNameOrId` and `forEach.config.childWorkflowNameOrId`.
 * Dynamic refs containing `{{` resolve at runtime and are skipped. Results are deduped by target.
 */
export function useWorkflowCallRefs(nodes: Node[]): WorkflowCallRef[] {
  const { data: workflows = [] } = useQuery({
    queryKey: ['workflows'],
    queryFn: () => api.get<Workflow[]>('/workflows'),
    staleTime: 30_000,
  });

  return useMemo(() => {
    const out: WorkflowCallRef[] = [];
    for (const n of nodes) {
      const data = n.data as Record<string, unknown> | undefined;
      if (!data) continue;
      const activityType = data.activityType as string | undefined;
      const config = (data.config as Record<string, unknown>) ?? {};
      const label = (data.label as string) ?? activityType ?? n.id;
      let ref: string | undefined;
      if (activityType === 'startWorkflow') ref = config.workflowNameOrId as string | undefined;
      else if (activityType === 'forEach') ref = config.childWorkflowNameOrId as string | undefined;
      if (!ref || ref.includes('{{')) continue;  // skip dynamic and unresolved refs
      // Resolve like the backend WorkflowNameResolver: an id or exact-case name wins,
      // otherwise fall back to a case- and whitespace-insensitive name match. The fallback
      // keeps refs that differ only by casing from rendering as broken pills even though
      // the engine would resolve them.
      const norm = (s: string) => s.trim().toLowerCase();
      const target =
        workflows.find((w) => w.id === ref || w.name === ref) ??
        workflows.find((w) => norm(w.name) === norm(ref)) ??
        null;
      out.push({ sourceLabel: label, refName: ref, target });
    }
    // Dedupe by target id so a workflow referenced several times shows once.
    const seen = new Set<string>();
    return out.filter((r) => {
      const key = r.target?.id ?? r.refName;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  }, [nodes, workflows]);
}

/**
 * Inline group of outgoing calls: an icon, a label and one pill per reference. It renders no
 * strip or background of its own so it can be composed into the shared editor status strip
 * next to the other hints instead of taking its own row. Each resolvable pill links to the
 * child editor; unresolved refs show a broken-ref pill. Returns null when there are no
 * static references.
 */
export function WorkflowCallsInline({ refs }: Readonly<{ refs: WorkflowCallRef[] }>) {
  const { t } = useTranslation('designer');
  if (refs.length === 0) return null;

  return (
    <div className="flex min-w-0 items-center gap-2 overflow-x-auto text-[11px] font-label">
      <FlowModeler size={11} className="text-primary shrink-0" />
      <span className="shrink-0 whitespace-nowrap text-[10px] font-semibold uppercase tracking-wide text-primary">
        {t('breadcrumbs.calls')}
      </span>
      <div className="flex items-center gap-1">
        {refs.map((r, i) => (
          <span key={i} className="flex items-center gap-1">
            {i > 0 && <ChevronRight size={11} className="text-primary/40" />}
            {r.target ? (
              <Link
                to={`/workflows/${r.target.id}`}
                className="np-call-pill np-call-pill--link inline-flex items-center gap-1 rounded-full px-2 text-primary bg-primary-fixed/40 transition-all hover:bg-primary-fixed hover:-translate-y-px whitespace-nowrap"
                title={t('breadcrumbs.openChild', { name: r.target.name })}
              >
                <span className="inline-flex h-3.5 w-3.5 items-center justify-center rounded-full bg-primary/15">
                  <FlowModeler size={9} />
                </span>
                {r.target.name}
              </Link>
            ) : (
              <span
                className="np-call-pill np-call-pill--broken inline-flex items-center gap-1 rounded-full px-2 bg-warning-container text-on-warning-container transition-all hover:-translate-y-px whitespace-nowrap"
                title={t('breadcrumbs.notFound', { name: r.refName })}
              >
                <span className="inline-flex h-3.5 w-3.5 items-center justify-center rounded-full bg-warning/20">
                  <WarningAltFilled size={9} className="shrink-0" />
                </span>
                {r.refName}
              </span>
            )}
          </span>
        ))}
      </div>
    </div>
  );
}

interface Props {
  /** Nodes of the current workflow, scanned for startWorkflow and forEach references. */
  nodes: Node[];
}

/**
 * Standalone outgoing-references row. A thin wrapper over the hook and the inline group for
 * direct use and unit tests; the editor composes {@link useWorkflowCallRefs} and
 * {@link WorkflowCallsInline} into the shared status strip instead of rendering this row.
 */
export function WorkflowBreadcrumbs({ nodes }: Readonly<Props>) {
  const refs = useWorkflowCallRefs(nodes);
  return <WorkflowCallsInline refs={refs} />;
}
