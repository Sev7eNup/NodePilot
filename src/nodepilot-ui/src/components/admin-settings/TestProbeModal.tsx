import { Checkmark, CircleDash, Close } from '@carbon/icons-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { SettingsTestProbeResult } from '../../api/adminSettings';

type Props = {
  title: string;
  open: boolean;
  onClose: () => void;
  /** Probe function — receives a click on the run button, resolves with the JSON status. */
  runProbe: () => Promise<SettingsTestProbeResult>;
  /** Optional pre-probe form contents rendered above the run button (e.g. "send to:"). */
  children?: React.ReactNode;
};

/**
 * Generic modal wrapper around the Admin Settings test probes (SMTP, LLM, …). Renders
 * a single Run button, displays the JSON status payload — ok / message / durationMs /
 * errorKind — and surfaces network failures inline rather than as a toast that
 * disappears before the operator can read it.
 *
 * <para>Deliberately not a streaming view: V1 probes return a single shot of data
 * (send-test-mail succeeded, ping-model returned 200). When a future probe needs
 * progressive output (e.g. LDAP-with-stages), we extend this component then; until
 * then a streaming widget would just be unused complexity.</para>
 */
export function TestProbeModal({ title, open, onClose, runProbe, children }: Readonly<Props>) {
  const { t } = useTranslation(['adminSettings']);
  const [pending, setPending] = useState(false);
  const [result, setResult] = useState<SettingsTestProbeResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  if (!open) return null;

  const run = async () => {
    setPending(true);
    setResult(null);
    setError(null);
    try {
      const r = await runProbe();
      setResult(r);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setPending(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/30 backdrop-blur-sm">
      <div className="bg-surface-lowest rounded-lg shadow-xl w-full max-w-lg p-6 space-y-4">
        <div className="flex items-center justify-between">
          <h3 className="text-lg font-semibold text-on-surface">{title}</h3>
          <button
            type="button"
            onClick={onClose}
            className="p-1 text-on-surface-variant hover:bg-surface-low rounded"
            aria-label={t('adminSettings:cancelButton')}
          >
            <Close size={18} />
          </button>
        </div>

        {children}

        <button
          type="button"
          onClick={run}
          disabled={pending}
          className="w-full px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-blue-400 text-sm font-medium flex items-center justify-center gap-2"
        >
          {pending && <CircleDash size={14} className="animate-spin" />}
          {t('adminSettings:testProbeRunButton')}
        </button>

        {result && (
          <div className={`rounded-md p-3 border ${result.ok ? 'bg-green-50 border-green-200 text-green-900' : 'bg-red-50 border-red-200 text-red-900'}`}>
            <div className="flex items-center gap-2 font-semibold">
              {result.ok ? <Checkmark size={16} /> : <Close size={16} />}
              {result.ok ? t('adminSettings:testProbeSuccess') : t('adminSettings:testProbeFailure')}
            </div>
            <p className="text-sm mt-1 whitespace-pre-wrap break-words">{result.message}</p>
            <p className="text-xs mt-2 text-on-surface-variant">
              {t('adminSettings:testProbeDurationLabel')}: {t('adminSettings:testProbeMs', { ms: Math.round(result.durationMs) })}
              {result.errorKind && ` • ${t('adminSettings:testProbeErrorKind')}: ${result.errorKind}`}
            </p>
          </div>
        )}

        {error && (
          <div className="rounded-md p-3 border bg-red-50 border-red-200 text-red-900">
            <p className="font-semibold text-sm">{t('adminSettings:errorTitle')}</p>
            <p className="text-sm mt-0.5 whitespace-pre-wrap break-words">{error}</p>
          </div>
        )}
      </div>
    </div>
  );
}
