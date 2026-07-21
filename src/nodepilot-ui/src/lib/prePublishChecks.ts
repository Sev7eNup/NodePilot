import type { Node, Edge } from '@xyflow/react';
import type { LintIssue, LintResult } from './workflowLint';
import { TRIGGER_ACTIVITY_TYPES } from './activityCatalog.generated';

/**
 * Workflow shape — only the fields we need for the pre-publish modal. Kept loose so
 * the caller can pass `Workflow | undefined` from React Query without an extra cast.
 */
export interface PrePublishWorkflowShape {
  name?: string | null;
  description?: string | null;
}

/**
 * Extra checks that only run right before a publish. Reasoning behind keeping them out
 * of `lintWorkflow`:
 *
 *   - "no description" is a UX hint that would be noisy in the always-on lint pill but
 *     deserves a one-time nag at the publish moment.
 *   - "trigger without outgoing edge" overlaps with the engine's own behaviour (the
 *     trigger fires but does nothing) — at design time this is often a half-finished
 *     workflow that the user just hasn't wired up yet, so we don't want it to flicker
 *     into the lint count on every keystroke. At publish time it almost certainly is
 *     a bug worth flagging.
 *
 * Returns an array of `LintIssue` so the modal can render the pre-publish-only items
 * with the same UI as the regular lint.
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

  // The "no trigger at all" case is already covered by the always-on `no-trigger` lint error
  // (folded in via baseLint in getPrePublishLint), which also blocks publish through
  // errors.length — no need to duplicate it here. Only when triggers DO exist: flag any of
  // them that has no outgoing connection as a soft warning.
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
 * Folds the standard lint result and the pre-publish-only checks into a single
 * `LintResult`. Keeps `error`-vs-`warning` separation so the modal can grey-out the
 * confirm button while errors exist.
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
