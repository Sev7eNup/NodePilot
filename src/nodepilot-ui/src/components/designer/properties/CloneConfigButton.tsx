import { Checkmark, ChevronDown, Copy } from '@carbon/icons-react';
import { useState, useRef, useEffect, useMemo } from 'react';
import type { Node } from '@xyflow/react';
import {
  buildClonedDataPatch,
  applyClonedPatch,
  isRemoteActivityType,
  type CloneScope,
} from '../../../lib/configClone';

/**
 * "Config übernehmen von …" affordance — sits at the top of the PropertiesPanel and gives the
 * user a one-click way to copy machine + credential + retry/timeout policy from another step
 * onto the current one. Action-payload fields (script body, query, path, …) are deliberately
 * NOT copied — the whole point of "another step in the same workflow doing similar work" is
 * that the connection params match while the action differs.
 *
 * Two scopes:
 *   - "all"        → identical activity type. Copies the type-specific cloneable config keys
 *                    plus targetMachineId/credentialId (when both ends are remote-capable).
 *   - "remoteOnly" → only targetMachineId + credentialId. Lets you say "all my remote work
 *                    runs on machine-A" without dragging timeout/retry across activity types.
 *
 * Only renders when at least one valid candidate exists in the workflow.
 */
interface Props {
  currentNode: Node;
  allNodes: Node[];
  onClone: (patchedData: Record<string, unknown>) => void;
}

export function CloneConfigButton({ currentNode, allNodes, onClone }: Readonly<Props>) {
  const [open, setOpen] = useState(false);
  const [scope, setScope] = useState<CloneScope>('all');
  const [justCloned, setJustCloned] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  const currentData = currentNode.data as Record<string, unknown>;
  const currentActivityType = (currentData.activityType as string) || '';

  // Candidates depend on scope: same-type clones need an exact activity-type match;
  // remote-only clones accept any other Remote-Activity. Note-/group-/trigger-types and the
  // current node itself are always excluded.
  const candidates = useMemo(() => {
    return allNodes.filter((n) => {
      if (n.id === currentNode.id) return false;
      if (n.type === 'group' || n.type === 'stickyNote') return false;
      const at = (n.data as Record<string, unknown>)?.activityType as string | undefined;
      if (!at) return false;
      if (scope === 'all') return at === currentActivityType;
      // remoteOnly: both ends remote-capable, AND the source actually has a machineId set
      // (otherwise the clone copies "null" which is a no-op users wouldn't expect).
      if (!isRemoteActivityType(at) || !isRemoteActivityType(currentActivityType)) return false;
      const machine = (n.data as Record<string, unknown>)?.targetMachineId;
      return machine !== null && machine !== undefined && machine !== '';
    });
  }, [allNodes, currentNode.id, currentActivityType, scope]);

  // Close when clicking outside.
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Element)) {
        setOpen(false);
      }
    };
    globalThis.addEventListener('mousedown', handler);
    return () => globalThis.removeEventListener('mousedown', handler);
  }, [open]);

  // Hide entirely when neither scope can produce candidates — keeps the panel uncluttered
  // for trivial workflows. We probe both scopes here (not just current) so toggling between
  // them after click doesn't suddenly show an empty list.
  const sameTypeCandidates = useMemo(
    () => allNodes.some((n) => n.id !== currentNode.id
      && (n.data as Record<string, unknown>)?.activityType === currentActivityType
      && n.type !== 'group' && n.type !== 'stickyNote'),
    [allNodes, currentNode.id, currentActivityType],
  );
  const remoteCandidates = useMemo(
    () => isRemoteActivityType(currentActivityType) && allNodes.some((n) => {
      if (n.id === currentNode.id) return false;
      if (n.type === 'group' || n.type === 'stickyNote') return false;
      const at = (n.data as Record<string, unknown>)?.activityType as string | undefined;
      if (!at || !isRemoteActivityType(at)) return false;
      return !!(n.data as Record<string, unknown>)?.targetMachineId;
    }),
    [allNodes, currentNode.id, currentActivityType],
  );
  if (!sameTypeCandidates && !remoteCandidates) return null;

  const remoteHere = isRemoteActivityType(currentActivityType);

  const handleClone = (sourceNode: Node) => {
    const sourceData = sourceNode.data as Record<string, unknown>;
    const patch = buildClonedDataPatch(sourceData, currentActivityType, scope);
    if (Object.keys(patch).length === 0) return;
    const next = applyClonedPatch(currentData, patch);
    onClone(next);
    setOpen(false);
    setJustCloned(true);
    globalThis.setTimeout(() => setJustCloned(false), 1500);
  };

  return (
    <div ref={containerRef} className="relative">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-md bg-surface-high hover:bg-surface-highest text-on-surface-variant text-[11px] font-label font-semibold transition-colors w-full justify-between"
        title="Übernimmt die komplette Config (inkl. Script/Query/Pfad) plus Maschine + Credential von einem anderen Step. Label und Output-Variable bleiben."
        data-testid="clone-config-button"
      >
        <span className="flex items-center gap-1.5">
          {justCloned ? <Checkmark size={12} className="text-green-600" /> : <Copy size={12} />}
          {justCloned ? 'Übernommen' : 'Config übernehmen von…'}
        </span>
        <ChevronDown size={12} className={`transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>
      {open && (
        <div
          className="absolute z-30 mt-1 w-full bg-surface-lowest rounded-md shadow-xl border border-outline-variant/30 overflow-hidden"
          data-testid="clone-config-popover"
        >
          {/* Scope toggle: same-type only or any-remote. Hidden when one of them has no
              candidates — no point showing a useless tab. */}
          {sameTypeCandidates && remoteCandidates && (
            <div className="grid grid-cols-2 border-b border-outline-variant/20 text-[10px] font-label font-semibold">
              <button
                type="button"
                onClick={() => setScope('all')}
                className={`py-1.5 transition-colors ${scope === 'all' ? 'bg-primary-fixed text-primary' : 'text-on-surface-variant hover:bg-surface-high'}`}
              >
                Vollständig (gleicher Typ)
              </button>
              <button
                type="button"
                onClick={() => setScope('remoteOnly')}
                className={`py-1.5 transition-colors ${scope === 'remoteOnly' ? 'bg-primary-fixed text-primary' : 'text-on-surface-variant hover:bg-surface-high'}`}
              >
                Nur Maschine + Credential
              </button>
            </div>
          )}

          <div className="px-3 py-2 border-b border-outline-variant/10 bg-surface-low text-[10px] font-label text-on-surface-variant leading-snug">
            {scope === 'all' ? (
              <>
                Übernimmt die <strong>komplette Config</strong> von einem anderen <strong>{currentActivityType}</strong>
                {remoteHere && <> &mdash; inkl. Maschine + Credential</>}.
                <div className="mt-1 italic">Label + Output-Variable bleiben unverändert.</div>
              </>
            ) : (
              <>Übernimmt nur <strong>Maschine</strong> + <strong>Credential</strong> von einem beliebigen Remote-Step. Config bleibt unverändert.</>
            )}
          </div>

          {candidates.length === 0 ? (
            <div className="px-3 py-3 text-[11px] font-label text-outline italic">
              {scope === 'all'
                ? `Kein anderer ${currentActivityType}-Step im Workflow.`
                : 'Kein Remote-Step mit gesetzter Maschine im Workflow.'}
            </div>
          ) : (
            <ul className="max-h-64 overflow-y-auto" role="listbox">
              {candidates.map((n) => {
                const d = n.data as Record<string, unknown>;
                const lbl = (d.label as string) || n.id;
                const at = (d.activityType as string) || '';
                const machine = (d.targetMachineId as string) || '';
                return (
                  <li key={n.id}>
                    <button
                      type="button"
                      onClick={() => handleClone(n)}
                      className="w-full text-left px-3 py-2 hover:bg-surface-high transition-colors border-b border-outline-variant/10 last:border-b-0"
                      data-testid={`clone-source-${n.id}`}
                    >
                      <div className="font-label text-xs font-semibold text-on-surface truncate">{lbl}</div>
                      <div className="font-mono text-[10px] text-on-surface-variant truncate">
                        {at}
                        {machine && ` · machine ${machine.slice(0, 8)}…`}
                      </div>
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}
