import type { Node, Edge } from '@xyflow/react';
import type { LintIssue, LintResult } from './workflowLint';
import { TRIGGER_ACTIVITY_TYPES } from './activityCatalog.generated';

/**
 * Workflow fields the pre-publish modal needs. Kept loose so callers can pass
 * `Workflow | undefined` straight from React Query without an extra cast.
 */
export interface PrePublishWorkflowShape {
  name?: string | null;
  description?: string | null;
}

/**
 * Checks that run only right before a publish, kept out of `lintWorkflow`:
 *
 *   - "no description" is a UX hint, worth a single nag at publish time but noisy in
 *     the always-on lint pill.
 *   - "trigger without outgoing edge" is usually just a half-wired workflow at design
 *     time, while at publish time it is almost certainly a bug.
 *
 * Returns `LintIssue` values so the modal renders these items like regular lint.
 */
export function getPrePublishIssues(
  nodes: Node[],
  edges: Edge[],
  workflow: PrePublishWorkflowShape | undefined,
): LintIssue[] {
  const issues: LintIssue[] = [];

  const liveNodes = nodes.filter((n) => {
    const at = (n.data as Record<string, unknown>)?.activityType as string | undefined;
    return at !== 'note' && at !== 'group' && n.type !== 'group';
  });

  // ---- Trigger gate -------------------------------------------------------
  const triggerNodes = liveNodes.filter((n) => {
    const at = (n.data as Record<string, unknown>)?.activityType as string | undefined;
    return TRIGGER_ACTIVITY_TYPES.has(at ?? '');
  });

  // The "no trigger at all" case is covered by the always-on `no-trigger` lint error, folded
  // in through baseLint in getPrePublishLint, which also blocks publish. Only triggers that
  // exist but have no outgoing connection are flagged here, as a warning.
  if (triggerNodes.length > 0) {
    const sourceIds = new Set<string>();
    for (const e of edges) {
      const disabled = (e.data as Record<string, unknown> | undefined)?.disabled;
      if (disabled) continue;
      sourceIds.add(e.source);
    }
    for (const t of triggerNodes) {
      if (sourceIds.has(t.id)) continue;
      const label = ((t.data as Record<string, unknown>)?.label as string) || t.id;
      issues.push({
        severity: 'warning',
        nodeId: t.id,
        code: 'trigger-without-outgoing',
        message: `Trigger "${label}" hat keine ausgehende Verbindung — er feuert, aber nichts läuft danach.`,
      });
    }
  }

  // ---- Description (soft UX hint) ----------------------------------------
  const description = (workflow?.description ?? '').trim();
  if (!description) {
    issues.push({
      severity: 'warning',
      code: 'no-description',
      message:
        'Workflow hat keine Beschreibung. Eine kurze Zeile erleichtert es Kollegen, in der Workflow-Liste zu erkennen, was hier läuft.',
    });
  }

  return issues;
}

/**
 * Folds the standard lint result and the pre-publish-only checks into one `LintResult`.
 * Keeps errors and warnings apart so the modal can disable the confirm button while
 * errors exist.
 */
export function getPrePublishLint(
  baseLint: LintResult,
  nodes: Node[],
  edges: Edge[],
  workflow: PrePublishWorkflowShape | undefined,
): LintResult {
  const extra = getPrePublishIssues(nodes, edges, workflow);
  const errors = [...baseLint.errors, ...extra.filter((i) => i.severity === 'error')];
  const warnings = [...baseLint.warnings, ...extra.filter((i) => i.severity === 'warning')];
  return { errors, warnings };
}
