import type { Node, Edge } from '@xyflow/react';
import { edgeSourcePort, edgeTargetPort, getPortPoint } from './edgePorts';
import { REMOTE_ACTIVITY_TYPES, TRIGGER_ACTIVITY_TYPES } from './activityCatalog.generated';
import { checkRequiredActivityConfig } from './activityConfigFacts';

// Hybrid activities: without a target machine they run locally in the API process (see
// RunScriptActivity / WaitForConditionActivity). They stay listed in REMOTE_ACTIVITY_TYPES
// so the UI still shows the machine/credential fields — lint just doesn't require them.
const HYBRID_LOCAL_ACTIVITY_TYPES = new Set(['runScript', 'waitForCondition']);

// Cmdlets/patterns that internally spawn `Start-Job` or another background-job worker.
// NodePilot's in-process runspace (engine: "auto" / "runspace") has no co-located
// pwsh.exe, so Start-Job blows up with "The pwsh executable cannot be found at
// …\runtimes\win\lib\net10.0\pwsh.exe". We warn the author here instead of letting the
// step fail red only at runtime. Kept deliberately small — only patterns we've actually
// seen in practice (Get-WindowsUpdateLog, direct Start-Job calls, Invoke-Command -AsJob).
// `Wait-Job`/`Receive-Job` without a preceding Start-Job are legitimate operations on job
// objects from other sources, so they're not listed here.
const STARTJOB_HOSTED_INCOMPATIBLE: Array<{ pattern: RegExp; cmdletName: string }> = [
  { pattern: /(^|[\s|;&])Start-Job\b/i, cmdletName: 'Start-Job' },
  { pattern: /\bGet-WindowsUpdateLog\b/i, cmdletName: 'Get-WindowsUpdateLog (nutzt intern Start-Job)' },
  { pattern: /\bInvoke-Command\b[^\r\n]*-AsJob\b/i, cmdletName: 'Invoke-Command -AsJob' },
];

export type LintSeverity = 'error' | 'warning';

export interface LintIssue {
  severity: LintSeverity;
  nodeId?: string;
  edgeId?: string;
  message: string;
  /** Stable code so future UI can group/filter ("unreachable-node", "dup-output", …). */
  code: string;
}

export interface LintResult {
  errors: LintIssue[];
  warnings: LintIssue[];
}

/**
 * Static workflow validation that runs BEFORE save. Purpose: warn the user about problems
 * that would otherwise only show up at runtime (a template referencing a non-existent
 * upstream variable, two nodes sharing the same `outputVariable`, …). Deliberately NOT a
 * replica of the engine's own logic — the backend remains the source of truth for "is this
 * actually allowed to run".
 *
 * Rules:
 *   error:   Isolated node (no incoming or outgoing edge, and not a trigger)
 *   error:   Duplicate outputVariable in the workflow (downstream references would resolve
 *            to whichever of the two happens to win)
 *   warning: Orphan root (a non-trigger node with no incoming edges but some outgoing ones,
 *            in a graph that does have triggers — the engine skips it, so its downstream
 *            branch never runs)
 *   warning: Unreachable node (has incoming edges, but no path exists from any trigger/root)
 *   warning: A template `{{var.field}}` references a variable that no upstream node exposes
 *            as `outputVariable` or via its step id
 *   warning: Edge pointing at a `disabled` node (the path is dead, but not obviously so)
 */
export function lintWorkflow(
  nodes: Node[],
  edges: Edge[],
  knownWorkflows?: Array<{ id: string; name: string }>,
): LintResult {
  const errors: LintIssue[] = [];
  const warnings: LintIssue[] = [];

  // Note and group nodes are pure annotations — not linted, since they don't affect the graph.
  const liveNodes = nodes.filter((n) => {
    const at = (n.data as Record<string, unknown>)?.activityType as string | undefined;
    return at !== 'note' && at !== 'group' && n.type !== 'group';
  });

  // ---- Adjazenz + Roots ----------------------------------------------------
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  for (const n of liveNodes) {
    outgoing.set(n.id, []);
    incoming.set(n.id, []);
  }
  const liveNodeIds = new Set(liveNodes.map((n) => n.id));
  const activeEdges = edges.filter((e) => {
    const d = (e.data as Record<string, unknown>) ?? {};
    return !d.disabled && liveNodeIds.has(e.source) && liveNodeIds.has(e.target);
  });

  // ---- Duplicate source->target edges -------------------------------------
  const seenEdgePairs = new Map<string, string>();
  for (const e of edges) {
    if (!liveNodeIds.has(e.source) || !liveNodeIds.has(e.target)) continue;
    const key = `${e.source}\u0000${e.target}`;
    const firstEdgeId = seenEdgePairs.get(key);
    if (firstEdgeId) {
      errors.push({
        severity: 'error',
        edgeId: e.id,
        code: 'duplicate-edge',
        message: `Verbindung ${e.source} -> ${e.target} existiert bereits (${firstEdgeId}). Bearbeite die bestehende Edge statt eine zweite anzulegen.`,
      });
    } else {
      seenEdgePairs.set(key, e.id);
    }
  }

  for (const e of activeEdges) {
    outgoing.get(e.source)?.push(e.target);
    incoming.get(e.target)?.push(e.source);
  }

  // Raw degree count (all edges, including disabled ones). Used only for isolation detection:
  // a node that's connected exclusively via a disabled edge is intentionally cut off — not a
  // bug, just a documented "dead branch". Reachability/BFS logic stays strictly on activeEdges,
  // otherwise disabled edges would feed downstream paths that shouldn't actually run.
  const rawIn = new Map<string, number>();
  const rawOut = new Map<string, number>();
  for (const n of liveNodes) { rawIn.set(n.id, 0); rawOut.set(n.id, 0); }
  for (const e of edges) {
    if (!liveNodeIds.has(e.source) || !liveNodeIds.has(e.target)) continue;
    rawOut.set(e.source, (rawOut.get(e.source) ?? 0) + 1);
    rawIn.set(e.target, (rawIn.get(e.target) ?? 0) + 1);
  }

  // Roots are EXCLUSIVELY (non-disabled) trigger nodes — exactly matching the engine. There is
  // no inDegree-0 fallback anymore: a graph without a trigger has no entry point, so the engine
  // runs nothing. A disabled trigger doesn't count (it's not a root).
  const isLiveTrigger = (n: Node) => {
    const d = (n.data as Record<string, unknown>) ?? {};
    return d.disabled !== true && TRIGGER_ACTIVITY_TYPES.has((d.activityType as string) ?? '');
  };
  const hasTriggerNode = liveNodes.some(isLiveTrigger);
  const rootIds = new Set<string>();
  for (const n of liveNodes) {
    if (isLiveTrigger(n)) rootIds.add(n.id);
  }

  // No (active) trigger, but at least one regular (non-disabled) activity → the workflow has
  // no entry point and can never run. Report this as a single clear error (blocks publish via
  // errors.length); the per-node "unreachable" flood below is suppressed in this case.
  const hasLiveActivity = liveNodes.some((n) => {
    const d = (n.data as Record<string, unknown>) ?? {};
    return d.disabled !== true && !TRIGGER_ACTIVITY_TYPES.has((d.activityType as string) ?? '');
  });
  if (!hasTriggerNode && hasLiveActivity) {
    errors.push({
      severity: 'error',
      code: 'no-trigger',
      message: 'Workflow hat keinen Trigger. Ohne Trigger-Node (manuell, Zeitplan, Webhook, …) gibt es keinen Einstiegspunkt — die Engine führt nichts aus.',
    });
  }

  // ---- Isolated & Unreachable ---------------------------------------------
  for (const n of liveNodes) {
    const d = (n.data as Record<string, unknown>) ?? {};
    const isDisabled = d.disabled === true;
    const inCount = incoming.get(n.id)?.length ?? 0;
    const outCount = outgoing.get(n.id)?.length ?? 0;
    const type = d.activityType as string | undefined;
    const isTrigger = TRIGGER_ACTIVITY_TYPES.has(type ?? '');

    // Disabled nodes: a disabled step is a deliberate "temporarily off" marker from the user,
    // not a broken workflow. We don't raise isolation or orphan-root errors for it and leave
    // the publish gate open — the block below only warns if it still has a downstream branch.
    if (isDisabled) continue;

    const rawInCount = rawIn.get(n.id) ?? 0;
    const rawOutCount = rawOut.get(n.id) ?? 0;

    if (rawInCount === 0 && rawOutCount === 0 && !isTrigger) {
      // Truly isolated: not a single edge points at or away from this node.
      errors.push({
        severity: 'error',
        nodeId: n.id,
        code: 'isolated-node',
        message: `"${getLabel(n)}" ist nicht mit dem Graph verbunden — weder eingehende noch ausgehende Kanten.`,
      });
    } else if (hasTriggerNode && !isTrigger && inCount === 0 && outCount > 0) {
      // Orphan root: not a trigger, no incoming edge, but has successors. Typically happens
      // when the user deletes an edge and the downstream branch is left dangling. The engine
      // will NOT start this node as an entry point (only triggers are roots), so the whole
      // path gets skipped.
      warnings.push({
        severity: 'warning',
        nodeId: n.id,
        code: 'orphan-root',
        message: `"${getLabel(n)}" hat keine eingehende Verbindung und ist kein Trigger — wird beim Ausführen übersprungen.`,
      });
    }
  }

  // BFS from all roots, marking reachable nodes. Only meaningful when roots actually exist —
  // without an (active) trigger EVERY connected node would read as "unreachable", drowning
  // the single `no-trigger` error in a flood of warnings. In that case the one no-trigger
  // error is sufficient on its own.
  if (rootIds.size > 0) {
    const reachable = new Set<string>();
    const queue = [...rootIds];
    while (queue.length > 0) {
      const cur = queue.shift()!;
      if (reachable.has(cur)) continue;
      reachable.add(cur);
      for (const next of outgoing.get(cur) ?? []) queue.push(next);
    }
    for (const n of liveNodes) {
      if (reachable.has(n.id)) continue;
      const inCount = incoming.get(n.id)?.length ?? 0;
      if (inCount === 0) continue; // isolated — already reported as an error above
      warnings.push({
        severity: 'warning',
        nodeId: n.id,
        code: 'unreachable-node',
        message: `"${getLabel(n)}" ist von keinem Trigger erreichbar — der Pfad dorthin läuft über deaktivierte Kanten oder einen Zyklus.`,
      });
    }
  }

  // ---- Duplicate outputVariable -------------------------------------------
  const seenOutputVar = new Map<string, string>(); // outputVar → first-seen nodeId
  for (const n of liveNodes) {
    const d = (n.data as Record<string, unknown>) ?? {};
    if (d.disabled === true) continue; // disabled steps emit no output, so no conflict is possible
    const ov = (d.outputVariable as string) || '';
    if (!ov) continue;
    if (seenOutputVar.has(ov)) {
      errors.push({
        severity: 'error',
        nodeId: n.id,
        code: 'dup-output-variable',
        message: `outputVariable "${ov}" ist schon von einem anderen Step gesetzt — Downstream-Referenzen {{${ov}.output}} werden zufällig einen der beiden treffen.`,
      });
    } else {
      seenOutputVar.set(ov, n.id);
    }
  }

  // ---- Template reference to an unknown variable --------------------------
  // We collect every variable name that could be exposed: each outputVariable + each node id
  // (the fallback when no outputVariable is set) + `globals.*` (we can't enumerate those here,
  // so we simply allow them) + `manual.*`. There's no `trigger.*` / `webhook.*` runtime
  // namespace — trigger data lands under `manual.*`, or as `param.*` on the trigger node.
  const knownVars = new Set<string>();
  for (const n of liveNodes) {
    knownVars.add(n.id);
    const ov = (n.data as Record<string, unknown>)?.outputVariable as string | undefined;
    if (ov) knownVars.add(ov);
  }

  const templateRegex = /\{\{\s*([a-zA-Z0-9_-]+)\.[a-zA-Z0-9_.-]+\s*\}\}/g;
  // Prefixes injected at runtime; lint must never flag these as unknown.
  const runtimePrefixes = new Set(['globals', 'manual']);

  for (const n of liveNodes) {
    const d = (n.data as Record<string, unknown>) ?? {};
    const haystack = JSON.stringify(d.config ?? {}) + ' ' + ((d.targetMachineId as string) || '') + ' ' + ((d.credentialId as string) || '');
    templateRegex.lastIndex = 0;
    let match: RegExpExecArray | null;
    while ((match = templateRegex.exec(haystack))) {
      const head = match[1];
      if (runtimePrefixes.has(head) || knownVars.has(head)) continue;
      warnings.push({
        severity: 'warning',
        nodeId: n.id,
        code: 'unknown-template-ref',
        message: `"${getLabel(n)}" referenziert {{${head}.…}} — dieser Step existiert nicht (oder heißt anders). Typo oder outputVariable umbenannt?`,
      });
      // Only report the first bad reference per node, otherwise one broken node spams the list.
      break;
    }
  }

  // ---- Activity config required fields -------------------------------------
  for (const n of liveNodes) {
    const d = (n.data as Record<string, unknown>) ?? {};
    if ((d.disabled as boolean) === true) continue;
    const at = (d.activityType as string) || '';
    const cfg = (d.config as Record<string, unknown>) ?? {};

    const msg = checkRequiredActivityConfig(at, cfg);
    if (msg) {
      errors.push({ severity: 'error', nodeId: n.id, code: 'missing-required-config', message: `"${getLabel(n)}": ${msg}` });
    }

    // runScript and waitForCondition are hybrid: without a target machine they run locally
    // in the API process (see the RunScriptActivity / WaitForConditionActivity localhost bypass).
    if (REMOTE_ACTIVITY_TYPES.has(at) && !HYBRID_LOCAL_ACTIVITY_TYPES.has(at)) {
      const machine = (d.targetMachineId as string) || '';
      if (!machine) {
        errors.push({ severity: 'error', nodeId: n.id, code: 'missing-target-machine', message: `"${getLabel(n)}": Ziel-Maschine (targetMachineId) ist für Remote-Activities erforderlich.` });
      }
    }
  }

  // ---- runScript: Start-Job / background jobs in the in-process engine -------
  // The in-process runspace (engine: "auto" → RunspaceExecutionEngine, or explicitly
  // "runspace") runs in the same process as the NodePilot API and hosts no co-located
  // pwsh.exe. Any cmdlet that internally calls `Start-Job` fails with "The pwsh executable
  // cannot be found at …\runtimes\win\lib\net10.0\pwsh.exe" — Microsoft documents this as
  // "by design" for hosted PowerShell. `-EA SilentlyContinue` only hides the error while
  // transcript wrapping is off; once auto-logging is on the step fails red, because
  // Start-Transcript bypasses the error-stream interception. Fix for the script author:
  // set engine: "pwsh" (an external PowerShell 7 process, where Start-Job can spawn its
  // child pwsh normally) or replace the cmdlet with a job-free alternative.
  for (const n of liveNodes) {
    const d = (n.data as Record<string, unknown>) ?? {};
    if ((d.disabled as boolean) === true) continue;
    if ((d.activityType as string) !== 'runScript') continue;
    const cfg = (d.config as Record<string, unknown>) ?? {};
    const engine = ((cfg.engine as string) || 'auto').toLowerCase();
    if (engine !== 'auto' && engine !== 'runspace') continue;
    const script = (cfg.script as string) || '';
    if (!script) continue;
    const hit = STARTJOB_HOSTED_INCOMPATIBLE.find((p) => p.pattern.test(script));
    if (!hit) continue;
    warnings.push({
      severity: 'warning',
      nodeId: n.id,
      code: 'startjob-in-runspace',
      message: `"${getLabel(n)}": Script ruft ${hit.cmdletName} auf — das spawnt intern einen Background-Job (Start-Job), für den eine externe pwsh.exe nötig ist. Der in-process Runspace (engine: "${engine}") unterstützt das nicht. Setze engine: "pwsh" in der Activity-Config.`,
    });
  }

  // ---- Cross-workflow reference to an unknown workflow ------------------
  if (knownWorkflows && knownWorkflows.length > 0) {
    const knownIds = new Set(knownWorkflows.map((w) => w.id.toLowerCase()));
    const knownNames = new Set(knownWorkflows.map((w) => w.name.toLowerCase()));
    for (const n of liveNodes) {
      const d = (n.data as Record<string, unknown>) ?? {};
      if ((d.activityType as string) !== 'startWorkflow') continue;
      const cfg = (d.config as Record<string, unknown>) ?? {};
      const ref = (cfg.workflowNameOrId as string) || '';
      if (!ref || ref.includes('{{')) continue;
      const refLower = ref.toLowerCase();
      if (!knownIds.has(refLower) && !knownNames.has(refLower)) {
        warnings.push({
          severity: 'warning',
          nodeId: n.id,
          code: 'unknown-workflow-ref',
          message: `"${getLabel(n)}": Workflow "${ref}" nicht gefunden — Typo oder wurde gelöscht?`,
        });
      }
    }
  }

  // ---- Edges pointing at disabled nodes ------------------------------------------
  const disabledNodeIds = new Set(
    liveNodes
      .filter((n) => (n.data as Record<string, unknown>)?.disabled === true)
      .map((n) => n.id),
  );
  // Per disabled node: report a single hint if it still has downstream steps attached.
  // The engine cascades the skip to those steps too (see WorkflowEngine.ExecuteAsync) —
  // without this warning, the author would be puzzled why "just disabling one step"
  // silently took out an entire downstream branch.
  const disabledWithDownstreamReported = new Set<string>();
  for (const e of edges) {
    if ((e.data as Record<string, unknown>)?.disabled) continue;
    if (disabledNodeIds.has(e.target)) {
      warnings.push({
        severity: 'warning',
        edgeId: e.id,
        code: 'edge-to-disabled',
        message: `Kante zielt auf einen deaktivierten Step — der wird nie ausgeführt.`,
      });
    }
    if (disabledNodeIds.has(e.source) && !disabledWithDownstreamReported.has(e.source)) {
      disabledWithDownstreamReported.add(e.source);
      const sourceNode = liveNodes.find((n) => n.id === e.source);
      const label = sourceNode ? getLabel(sourceNode) : e.source;
      warnings.push({
        severity: 'warning',
        nodeId: e.source,
        code: 'disabled-with-downstream',
        message: `"${label}" ist deaktiviert, hat aber noch ausgehende Verbindungen — die nachfolgenden Steps werden mit-skipped. Edges entfernen oder Step wieder aktivieren.`,
      });
    }
  }

  // ---- Edge occlusion: a connection line runs through an unrelated node ---------------------
  // React Flow renders each node's background color ON TOP of the edge/SVG layer. When an
  // edge's geometric path happens to cross another node's bounding box, the user sees what
  // looks like a "cut off" connection — the workflow still runs fine (the engine reads the
  // JSON directly), but the UI visually suggests "not connected". Typical trigger: 4+ fan-out
  // edges from one source node to vertically stacked targets, where the outermost edges cut
  // diagonally through the targets in between. Fix: Auto-Layout (Tidy) repositions nodes to
  // avoid the overlap. We flag this statically so the user notices before they start
  // debugging a step that looks "phantom-disconnected".
  const nodeBounds = new Map<string, { x: number; y: number; w: number; h: number }>();
  for (const n of liveNodes) {
    const m = (n as { measured?: { width?: number; height?: number } }).measured;
    const w = m?.width ?? (n as { width?: number }).width ?? 140;
    const h = m?.height ?? (n as { height?: number }).height ?? 132;
    nodeBounds.set(n.id, { x: n.position.x, y: n.position.y, w, h });
  }
  const labelById = new Map<string, string>();
  for (const n of liveNodes) labelById.set(n.id, getLabel(n));

  for (const e of activeEdges) {
    const sb = nodeBounds.get(e.source);
    const tb = nodeBounds.get(e.target);
    if (!sb || !tb) continue;
    // Handle-aware: edges without explicit ports fall back to the classic right -> left path.
    const sourcePort = edgeSourcePort(e);
    const sourcePoint = getPortPoint(sb, sourcePort);
    const targetPoint = getPortPoint(tb, edgeTargetPort(e));
    const samples = sampleBezier(
      sourcePoint.x,
      sourcePoint.y,
      targetPoint.x,
      targetPoint.y,
      sourcePort === 'top' || sourcePort === 'bottom',
    );

    const occludedBy: string[] = [];
    const crowdedBy: string[] = [];
    const STRICT_SHRINK = 10;
    const NEAR_EXPAND = 25;
    for (const [nid, nb] of nodeBounds) {
      if (nid === e.source || nid === e.target) continue;
      const strictRect: [number, number, number, number] = [nb.x + STRICT_SHRINK, nb.y + STRICT_SHRINK, nb.w - 2 * STRICT_SHRINK, nb.h - 2 * STRICT_SHRINK];
      const crowdRect: [number, number, number, number] = [nb.x - NEAR_EXPAND, nb.y - NEAR_EXPAND, nb.w + 2 * NEAR_EXPAND, nb.h + 2 * NEAR_EXPAND];
      if (pointsHitRect(samples, ...strictRect)) {
        occludedBy.push(labelById.get(nid) ?? nid);
      } else if (pointsHitRect(samples, ...crowdRect)) {
        crowdedBy.push(labelById.get(nid) ?? nid);
      }
    }
    if (occludedBy.length > 0) {
      warnings.push({
        severity: 'warning',
        edgeId: e.id,
        code: 'edge-occluded',
        message: `Kante läuft geometrisch durch ${describeNodes(occludedBy)} — optisch wirkt sie "unverbunden", Engine führt sie aber aus. Auto-Layout (Tidy) anwenden, um das Layout zu bereinigen.`,
      });
    } else if (crowdedBy.length >= 2) {
      // Being near a single node is fine (neighbouring nodes are normal). The rule only
      // fires once 2+ neighbours sit along the same edge path — that's the "fan-out running
      // through a whole row" case that originally prompted this check.
      warnings.push({
        severity: 'warning',
        edgeId: e.id,
        code: 'edge-crowded',
        message: `Kante läuft dicht an ${describeNodes(crowdedBy)} vorbei — optisch schwer zuordenbar, obwohl die Verbindung existiert. Auto-Layout (Tidy) sorgt für klarere Abstände.`,
      });
    }
  }

  return { errors, warnings };
}

function describeNodes(labels: string[]): string {
  if (labels.length === 1) return `"${labels[0]}"`;
  const head = labels.slice(0, 2).map((l) => `"${l}"`).join(', ');
  return `${labels.length} Nodes (${head}${labels.length > 2 ? ', …' : ''})`;
}

/** Sampled points of a cubic Bezier curve, right→left (or bottom→top for vertical flow).
 *  The control-point offset matches React Flow's `getBezierPath` with curvature=0.25 — see
 *  getControlWithCurvature → calculateControlOffset(distance, 0.25): for a positive distance
 *  it's 0.5 * distance, otherwise it's curvature-scaled sqrt(-distance). We only cover the
 *  forward case (dx>0 or dy>0) because backward edges are routed around in a loop by
 *  SmartEdgePath anyway, where this lint check wouldn't be reliable. */
function sampleBezier(x1: number, y1: number, x2: number, y2: number, vertical: boolean): Array<[number, number]> {
  const d = vertical ? y2 - y1 : x2 - x1;
  // Backward: too complex to approximate accurately (it loops around in a U-shape below),
  // so skip precise analysis and fall back to straight-line samples instead — the strict
  // occlusion check still catches it if the direct path happens to cross a node.
  if (d < 0) return sampleLine(x1, y1, x2, y2);
  const offset = Math.max(0.5 * Math.abs(d), 30);
  const cp1 = vertical ? [x1, y1 + offset] : [x1 + offset, y1];
  const cp2 = vertical ? [x2, y2 - offset] : [x2 - offset, y2];
  const points: Array<[number, number]> = [];
  const steps = 24;
  for (let i = 1; i < steps; i++) {
    const t = i / steps;
    const mt = 1 - t;
    const bx = mt * mt * mt * x1 + 3 * mt * mt * t * cp1[0] + 3 * mt * t * t * cp2[0] + t * t * t * x2;
    const by = mt * mt * mt * y1 + 3 * mt * mt * t * cp1[1] + 3 * mt * t * t * cp2[1] + t * t * t * y2;
    points.push([bx, by]);
  }
  return points;
}

function sampleLine(x1: number, y1: number, x2: number, y2: number): Array<[number, number]> {
  const points: Array<[number, number]> = [];
  const steps = 24;
  for (let i = 1; i < steps; i++) {
    const t = i / steps;
    points.push([x1 + (x2 - x1) * t, y1 + (y2 - y1) * t]);
  }
  return points;
}

function pointsHitRect(points: Array<[number, number]>, rx: number, ry: number, rw: number, rh: number): boolean {
  if (rw <= 0 || rh <= 0) return false;
  for (const [px, py] of points) {
    if (px >= rx && px <= rx + rw && py >= ry && py <= ry + rh) return true;
  }
  return false;
}

function getLabel(n: Node): string {
  const d = n.data as Record<string, unknown>;
  return (d?.label as string) || (d?.activityType as string) || n.id;
}
