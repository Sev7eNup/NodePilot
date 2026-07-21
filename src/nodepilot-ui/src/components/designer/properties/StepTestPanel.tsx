import {
  CheckmarkFilled,
  ChevronDown,
  CircleDash,
  ErrorFilled,
  Play,
  Renew,
  WarningAltFilled,
} from '@carbon/icons-react';
import { useCallback, useState, useMemo, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../../api/client';
import type {
  StepTestResult,
  StepTestContextResponse,
  StepTestContextRunInfo,
} from '../../../types/api';

/**
 * Four discrete test modes the user can pick before clicking Run.
 *
 * - empty: no mock variables, no upstream context. Default.
 * - lastRun: pull `{{step.output}}` / `{{step.param.x}}` values from the most recent
 *   terminal execution of this workflow. Read-only preview, then Run.
 * - pickRun: same as lastRun but the user chooses which execution to source from.
 * - manualMocks: free-form key=value editor. Used for "I want to test what happens
 *   when freeGb=2 specifically" branches that no past run produced.
 */
type Mode = 'empty' | 'lastRun' | 'pickRun' | 'manualMocks';

interface Props {
  workflowId: string;
  stepId: string;
  /** Live (possibly unsaved) config from the editor — sent as ConfigOverride. */
  liveConfig: Record<string, unknown>;
  /** When false, the Run button is hidden — Viewers can still inspect the panel. */
  canRun: boolean;
  expertMode?: boolean;
}

/**
 * Step-test panel with context-aware modes. Replaces the bare "Test Step" button.
 * Always sends the live editor config as `ConfigOverride` so the user tests what they
 * see, not the last-saved DB state.
 */
export function StepTestPanel({ workflowId, stepId, liveConfig, canRun, expertMode = true }: Readonly<Props>) {
  const { t } = useTranslation(['properties', 'common']);
  const [mode, setMode] = useState<Mode>(expertMode ? 'empty' : 'lastRun');
  const [pickedRunId, setPickedRunId] = useState<string | null>(null);
  const [manualMocksText, setManualMocksText] = useState('');
  const [testResult, setTestResult] = useState<StepTestResult | null>(null);
  const [testing, setTesting] = useState(false);

  const wantsContext = mode === 'lastRun' || mode === 'pickRun';

  useEffect(() => {
    if (!expertMode) setMode('lastRun');
  }, [expertMode]);

  const { data: runs } = useQuery({
    queryKey: ['step-test-runs', workflowId, stepId],
    enabled: mode === 'pickRun',
    staleTime: 30_000,
    queryFn: () => api.get<StepTestContextRunInfo[]>(
      `/workflows/${workflowId}/steps/${stepId}/test-context/runs?limit=10`),
  });

  const { data: context, isFetching: contextFetching, refetch: refetchContext } = useQuery({
    queryKey: ['step-test-context', workflowId, stepId, pickedRunId],
    enabled: wantsContext,
    staleTime: 15_000,
    queryFn: () => {
      const qs = pickedRunId ? `?executionId=${pickedRunId}` : '';
      return api.get<StepTestContextResponse>(
        `/workflows/${workflowId}/steps/${stepId}/test-context${qs}`);
    },
  });

  // First time pickRun mode opens, default to the most recent run that actually exercised
  // the step. Saves the user a click — they can still pick a different run from the dropdown.
  useEffect(() => {
    if (mode !== 'pickRun' || pickedRunId || !runs?.length) return;
    const mostRecentRan = runs.find((r) => r.stepRan);
    if (mostRecentRan) setPickedRunId(mostRecentRan.executionId);
  }, [mode, runs, pickedRunId]);

  // When mode changes away from pickRun, reset the chosen run so re-entering picks fresh.
  useEffect(() => {
    if (mode !== 'pickRun') setPickedRunId(null);
  }, [mode]);

  const buildMocks = useCallback((): Record<string, string> | undefined => {
    if (mode === 'empty') return undefined;
    if (mode === 'manualMocks') {
      const out: Record<string, string> = {};
      for (const line of manualMocksText.split('\n')) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith('#')) continue;
        const eq = trimmed.indexOf('=');
        if (eq < 0) continue;
        const key = trimmed.slice(0, eq).trim();
        const value = trimmed.slice(eq + 1).trim();
        if (key) out[key] = value;
      }
      return Object.keys(out).length ? out : undefined;
    }
    // lastRun / pickRun: collect every variable that has a non-null value. Globals
    // must NOT be passed — the engine pulls them from the IGlobalVariableStore directly,
    // and forwarding their values from a stale run would override the current store.
    if (!context) return undefined;
    const out: Record<string, string> = {};
    for (const v of context.variables) {
      if (v.source === 'global') continue;
      if (v.value === null) continue;
      out[v.key] = v.value;
    }
    return Object.keys(out).length ? out : undefined;
  }, [mode, manualMocksText, context]);

  const runTest = useCallback(async () => {
    if (testing) return;
    setTesting(true);
    setTestResult(null);
    try {
      const mocks = buildMocks();
      const result = await api.post<StepTestResult>(
        `/workflows/${workflowId}/steps/${stepId}/test`,
        {
          mockVariables: mocks,
          configOverride: liveConfig,
        });
      setTestResult(result);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setTestResult({
        success: false, output: null, errorOutput: msg,
        outputParameters: {}, durationMs: 0, errorMessage: msg,
      });
    } finally {
      setTesting(false);
    }
  }, [testing, workflowId, stepId, liveConfig, buildMocks]);

  return (
    <div className="space-y-2.5">
      {expertMode && <ModeSelector mode={mode} onChange={setMode} />}
      {expertMode && mode === 'pickRun' && (
        <RunPicker
          runs={runs ?? null}
          pickedRunId={pickedRunId}
          onPick={setPickedRunId}
        />
      )}
      {expertMode && wantsContext && (
        <ContextPreview
          context={context ?? null}
          isFetching={contextFetching}
          onRefresh={() => refetchContext()}
        />
      )}
      {expertMode && mode === 'manualMocks' && (
        <ManualMocksEditor value={manualMocksText} onChange={setManualMocksText} />
      )}
      {canRun && (
        <button
          onClick={runTest}
          disabled={testing || (mode === 'pickRun' && !pickedRunId)}
          className="flex items-center gap-2 w-full px-3 py-2 rounded-md text-sm font-label font-medium bg-surface-highest text-on-surface hover:bg-surface-high transition-colors disabled:opacity-50"
        >
          {testing
            ? <CircleDash size={14} className="animate-spin" />
            : <Play size={14} />}
          {testing
            ? t('properties:test.testing')
            : expertMode
              ? t('properties:test.runWithMode', { mode: t(`properties:test.modes.${mode}`) })
              : t('properties:test.runStandard')}
        </button>
      )}
      {testResult && <TestResultBlock result={testResult} />}
    </div>
  );
}

function ModeSelector({ mode, onChange }: Readonly<{ mode: Mode; onChange: (m: Mode) => void }>) {
  const { t } = useTranslation('properties');
  const modes: Mode[] = ['empty', 'lastRun', 'pickRun', 'manualMocks'];
  return (
    <div className="flex flex-wrap gap-1">
      {modes.map((m) => (
        <button
          key={m}
          onClick={() => onChange(m)}
          className={`px-2.5 py-1 rounded-md text-[11px] font-label font-medium transition-colors ${
            mode === m
              ? 'bg-primary text-on-primary'
              : 'bg-surface-highest text-on-surface-variant hover:bg-surface-high'
          }`}
        >
          {t(`test.modes.${m}`)}
        </button>
      ))}
    </div>
  );
}

function RunPicker({
  runs, pickedRunId, onPick,
}: Readonly<{
  runs: StepTestContextRunInfo[] | null;
  pickedRunId: string | null;
  onPick: (id: string) => void;
}>) {
  const { t } = useTranslation('properties');
  if (!runs) {
    return (
      <div className="text-xs text-on-surface-variant flex items-center gap-1.5 py-1">
        <CircleDash size={11} className="animate-spin" />
        {t('test.loadingRuns')}
      </div>
    );
  }
  if (runs.length === 0) {
    return (
      <div className="text-xs text-on-surface-variant py-1">
        {t('test.noRuns')}
      </div>
    );
  }
  return (
    <div className="relative">
      <select
        value={pickedRunId ?? ''}
        onChange={(e) => onPick(e.target.value)}
        className="w-full appearance-none px-2.5 py-1.5 pr-8 rounded-md bg-surface-highest text-sm text-on-surface border border-outline-variant focus:outline-none focus:border-primary"
      >
        <option value="">{t('test.selectRun')}</option>
        {runs.map((r) => {
          const date = new Date(r.startedAt).toLocaleString();
          const stepRanBadge = r.stepRan ? '' : ` (${t('test.stepDidNotRun')})`;
          return (
            <option key={r.executionId} value={r.executionId}>
              {date} · {r.status} · {r.triggeredBy ?? '?'}{stepRanBadge}
            </option>
          );
        })}
      </select>
      <ChevronDown size={14} className="absolute right-2 top-1/2 -translate-y-1/2 pointer-events-none text-on-surface-variant" />
    </div>
  );
}

function ContextPreview({
  context, isFetching, onRefresh,
}: Readonly<{
  context: StepTestContextResponse | null;
  isFetching: boolean;
  onRefresh: () => void;
}>) {
  const { t } = useTranslation('properties');
  const groups = useMemo(() => {
    if (!context) return [];
    const byOrigin = new Map<string, typeof context.variables>();
    for (const v of context.variables) {
      const list = byOrigin.get(v.origin) ?? [];
      list.push(v);
      byOrigin.set(v.origin, list);
    }
    return Array.from(byOrigin.entries());
  }, [context]);

  if (!context && !isFetching) return null;

  return (
    <div className="rounded-md border border-outline-variant bg-surface-low">
      <div className="flex items-center justify-between px-2.5 py-1.5 border-b border-outline-variant text-[11px] font-label font-semibold text-on-surface-variant">
        <span className="flex items-center gap-1.5">
          {isFetching && <CircleDash size={10} className="animate-spin" />}
          {context?.executedAt
            ? t('test.contextFromRun', {
                date: new Date(context.executedAt).toLocaleString(),
                status: context.status ?? '?',
              })
            : t('test.contextSchemaOnly')}
        </span>
        <button
          onClick={onRefresh}
          className="p-0.5 rounded hover:bg-surface-high text-on-surface-variant"
          title={t('test.refresh')}
        >
          <Renew size={10} />
        </button>
      </div>
      <div className="max-h-48 overflow-auto p-2 space-y-2">
        {groups.length === 0 && !isFetching && (
          <div className="text-xs text-on-surface-variant py-2 text-center">
            {t('test.contextEmpty')}
          </div>
        )}
        {groups.map(([origin, vars]) => (
          <div key={origin} className="space-y-0.5">
            <div className="text-[10px] uppercase tracking-wider font-bold text-primary/80">
              {origin}
            </div>
            {vars.map((v) => (
              <div key={v.key} className="flex items-baseline gap-1.5 text-[11px] font-mono">
                <span className="text-primary truncate flex-shrink-0">{v.key}</span>
                <span className="text-on-surface-variant">=</span>
                <span className={v.value === null ? 'text-on-surface-variant italic' : 'text-on-surface truncate'}>
                  {v.value === null ? t('test.noValue') : v.value}
                </span>
              </div>
            ))}
          </div>
        ))}
      </div>
    </div>
  );
}

function ManualMocksEditor({ value, onChange }: Readonly<{ value: string; onChange: (v: string) => void }>) {
  const { t } = useTranslation('properties');
  return (
    <div className="space-y-1">
      <textarea
        value={value}
        onChange={(e) => onChange(e.target.value)}
        rows={5}
        spellCheck={false}
        placeholder={t('test.mocksPlaceholder')}
        className="w-full px-2.5 py-1.5 rounded-md bg-surface-highest text-xs font-mono text-on-surface border border-outline-variant focus:outline-none focus:border-primary resize-y"
      />
      <p className="text-[10px] text-on-surface-variant">
        {t('test.mocksHint')}
      </p>
    </div>
  );
}

function TestResultBlock({ result }: Readonly<{ result: StepTestResult }>) {
  const { t } = useTranslation('properties');
  const hasParams = Object.keys(result.outputParameters).length > 0;
  return (
    <div className={`rounded-md border p-2.5 text-xs font-mono ${
      result.success ? 'bg-green-50 border-green-200' : 'bg-red-50 border-red-200'}`}>
      <div className="flex items-center gap-1.5 mb-1.5 font-sans font-semibold">
        {result.success
          ? <CheckmarkFilled size={13} className="text-green-600" />
          : <ErrorFilled size={13} className="text-red-600" />}
        <span className={result.success ? 'text-green-700' : 'text-red-700'}>
          {result.success ? t('test.succeeded') : t('test.failed')}
        </span>
        <span className="ml-auto font-normal text-on-surface-variant font-mono">
          {result.durationMs.toFixed(0)} ms
        </span>
      </div>
      {result.errorMessage && !result.errorOutput && (
        <div className="flex gap-1 text-red-700">
          <WarningAltFilled size={11} className="mt-0.5 flex-shrink-0" />
          <span>{result.errorMessage}</span>
        </div>
      )}
      {result.output && (
        <pre className="whitespace-pre-wrap overflow-auto max-h-40 text-on-surface">{result.output}</pre>
      )}
      {result.errorOutput && (
        <pre className="whitespace-pre-wrap overflow-auto max-h-32 text-red-700 mt-1">{result.errorOutput}</pre>
      )}
      {hasParams && (
        <div className="mt-1.5 font-sans space-y-0.5">
          {Object.entries(result.outputParameters).map(([k, v]) => (
            <div key={k} className="flex gap-1">
              <span className="text-primary font-mono">{k}:</span>
              <span className="text-on-surface truncate">{v}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
