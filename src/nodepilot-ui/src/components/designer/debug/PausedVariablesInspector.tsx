import { Checkbox, Debug, Edit, Play, Reset, SkipForward } from '@carbon/icons-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

/**
 * Debug panel for a step that's paused at a breakpoint. Reads the variable snapshot from
 * the backend (already secret-redacted) and lets the user edit values before clicking
 * Resume — classic "what-if" testing: set `{{globals.ENV}}` to `prod`, click Continue, and
 * the downstream steps see the new value.
 *
 * Variables are grouped by prefix (`globals.*`, `manual.*`, step outputs per upstream step)
 * so 30+ entries don't turn into a wall of text.
 */
export interface PausedVariablesInspectorProps {
  stepName: string;
  stepId: string;
  pausedAt: string;
  reason: string;
  variables: Record<string, string>;
  onResume: (mode: 'continue' | 'stepOver' | 'stop', overrides: Record<string, string>) => Promise<void>;
}

export function PausedVariablesInspector(props: Readonly<PausedVariablesInspectorProps>) {
  const { t } = useTranslation('designer');
  const { stepName, stepId, pausedAt, reason, variables, onResume } = props;
  const [edits, setEdits] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState<null | 'continue' | 'stepOver' | 'stop'>(null);

  // Grouped for readability. The prefix scheme mirrors VariableResolver.BuildStepVariables:
  // `globals.*`, `manual.*`, `<stepVar>.output`, `<stepVar>.error`, `<stepVar>.param.*`.
  // There is no `trigger.` or `webhook.` namespace in the engine — webhook payloads land as
  // `manual.webhookBody`/`manual.webhookHeader_X` (see WebhooksController).
  // We group on a stable, language-independent key and only translate at render time —
  // otherwise the sorting/lookup logic would be tied to the UI language.
  const groups = useMemo(() => {
    const g: Record<string, Array<[string, string]>> = {};
    for (const [k, v] of Object.entries(variables)) {
      let group: string;
      if (k.startsWith('globals.')) group = 'globals';
      else if (k.startsWith('manual.')) group = 'manual';
      else if (k.includes('.param.')) group = `step:${k.split('.')[0]}`;
      else if (k.endsWith('.output') || k.endsWith('.error') || k.endsWith('.success')) {
        group = `step:${k.split('.')[0]}`;
      }
      else group = 'other';
      g[group] ??= [];
      g[group].push([k, v]);
    }
    // Sort so Globals/Manual come first, step groups are alphabetical, and Other sits last.
    const order = (name: string) =>
      name === 'globals' ? 0
      : name === 'manual' ? 1
      : name === 'other' ? 99
      : 10;
    return Object.entries(g).sort((a, b) => order(a[0]) - order(b[0]) || a[0].localeCompare(b[0]));
  }, [variables]);

  // Display label for a stable group key.
  const groupLabel = (key: string): string => {
    if (key === 'globals') return t('debug.groupGlobals');
    if (key === 'manual') return t('debug.groupManual');
    if (key === 'other') return t('debug.groupOther');
    return t('debug.groupStep', { name: key.slice('step:'.length) });
  };

  const dirtyCount = Object.keys(edits).length;
  const effectiveValue = (k: string) => edits[k] ?? variables[k];

  const submit = async (mode: 'continue' | 'stepOver' | 'stop') => {
    if (submitting) return;
    setSubmitting(mode);
    try {
      await onResume(mode, edits);
      setEdits({});
    } finally {
      setSubmitting(null);
    }
  };

  return (
    <div className="h-full overflow-y-auto flex flex-col">
      {/* Debug action bar — paused-token accent (dark-fähig) so it's unmistakable that
          we're in paused mode. */}
      <div className="px-4 py-3 bg-paused-container/50 border-b border-paused/30 border-t-2 border-t-paused flex items-center gap-3 shrink-0">
        <Debug size={16} className="text-paused shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="font-label text-sm font-semibold text-on-paused-container truncate">
            {t('debug.pausedAt', { step: stepName })}
          </div>
          <div className="font-label text-[10px] text-on-paused-container/80 flex items-center gap-2">
            <span className="font-mono">{stepId}</span>
            <span>·</span>
            <span>{reason === 'stepOver' ? t('debug.stepOverTrigger') : t('debug.breakpoint')}</span>
            <span>·</span>
            <span className="font-mono tabular-nums">{new Date(pausedAt).toLocaleTimeString(undefined, { hour12: false })}</span>
            {dirtyCount > 0 && (
              <>
                <span>·</span>
                <span className="text-on-warning-container font-semibold">{t('debug.variablesModified', { count: dirtyCount })}</span>
              </>
            )}
          </div>
        </div>
        <button
          onClick={() => submit('continue')}
          disabled={!!submitting}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-success text-on-success hover:brightness-110 text-xs font-label font-semibold transition-colors disabled:opacity-50"
          title={t('debug.continueTooltip')}
        >
          <Play size={12} /> {t('debug.continue')}
        </button>
        <button
          onClick={() => submit('stepOver')}
          disabled={!!submitting}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-info text-on-info hover:brightness-110 text-xs font-label font-semibold transition-colors disabled:opacity-50"
          title={t('debug.stepOverTooltip')}
        >
          <SkipForward size={12} /> {t('debug.stepOver')}
        </button>
        <button
          onClick={() => submit('stop')}
          disabled={!!submitting}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-error text-on-error hover:brightness-110 text-xs font-label font-semibold transition-colors disabled:opacity-50"
          title={t('debug.stopTooltip')}
        >
          <Checkbox size={12} /> {t('debug.stop')}
        </button>
      </div>
      {/* Variable list */}
      <div className="flex-1 overflow-y-auto p-3 space-y-4">
        {groups.map(([groupName, entries]) => (
          <div key={groupName}>
            <h3 className="font-label text-[10px] font-bold uppercase tracking-widest text-on-surface-variant mb-1.5">
              {groupLabel(groupName)} <span className="text-outline font-normal">({entries.length})</span>
            </h3>
            <div className="space-y-0.5">
              {entries.map(([key, originalValue]) => {
                const isDirty = key in edits;
                const current = effectiveValue(key);
                return (
                  <div
                    key={key}
                    className={`grid grid-cols-[minmax(180px,300px)_1fr_auto] gap-2 items-start px-2 py-1 rounded hover:bg-surface-low transition-colors border-l-2 ${
                      isDirty ? 'border-warning bg-warning-container/40' : 'border-transparent'
                    }`}
                  >
                    <code className="font-mono text-[11px] text-on-surface-variant truncate" title={key}>
                      {key}
                    </code>
                    <input
                      type="text"
                      value={current}
                      onChange={(e) => {
                        const next = e.target.value;
                        setEdits((prev) => {
                          if (next === originalValue) {
                            const cp = { ...prev };
                            delete cp[key];
                            return cp;
                          }
                          return { ...prev, [key]: next };
                        });
                      }}
                      className={`font-mono text-[11px] bg-transparent border-b px-1 py-0.5 outline-none focus:border-primary ${
                        isDirty ? 'border-warning text-on-warning-container' : 'border-outline-variant/30 text-on-surface'
                      }`}
                    />
                    {isDirty ? (
                      <button
                        onClick={() => setEdits((prev) => { const cp = { ...prev }; delete cp[key]; return cp; })}
                        className="text-outline hover:text-on-surface"
                        title={t('debug.resetToOriginal')}
                      >
                        <Reset size={11} />
                      </button>
                    ) : (
                      <Edit size={11} className="text-outline-variant/60" />
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        ))}
        {groups.length === 0 && (
          <div className="text-center py-12 font-label text-sm text-on-surface-variant">
            {t('debug.noVariables')}
          </div>
        )}
      </div>
    </div>
  );
}
