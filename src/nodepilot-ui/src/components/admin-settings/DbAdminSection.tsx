import { Close, DataBase, SecurityServices } from '@carbon/icons-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  useSectionForm,
  Card,
  Toggle,
  NumberInput,
  ErrorsAndSave,
  HotReloadHint,
} from './SectionFormHelpers';

type DbAdminDto = {
  allowWriteQueries: boolean;
  queryTimeoutSeconds: number;
  queryMaxRows: number;
};

const CONFIRM_PHRASE = 'ALLOW WRITE';

/**
 * Settings tab for the admin SQL query console (POST /api/dbadmin/query).
 *
 * `AllowWriteQueries` is the sharp edge: flipping it from false to true lets
 * any Admin run UPDATE/DELETE/DROP from the query pane, bypassing every
 * per-table guard the row-editor applies. We gate the enable transition
 * behind a typed-phrase confirmation dialog — same friction the QueryPane
 * itself enforces per write-statement, applied here once at config time.
 *
 * Disabling write queries needs no confirmation (you're removing a power,
 * not granting one), and the other two fields (timeout, row cap) are plain
 * tuning knobs without a security dimension.
 */
export function DbAdminSection() {
  const { t } = useTranslation(['adminSettings']);
  const ui = useSectionForm<DbAdminDto>('DbAdmin', {
    allowWriteQueries: false,
    queryTimeoutSeconds: 30,
    queryMaxRows: 10_000,
  });

  const [pendingEnable, setPendingEnable] = useState(false);
  const [confirmInput, setConfirmInput] = useState('');

  if (ui.loading) {
    return (
      <Card icon={DataBase} title={t('adminSettings:dbAdmin.title')}>
        <p className="text-sm">{t('adminSettings:loading')}</p>
      </Card>
    );
  }
  const { form, set, data, isEnvLocked, save, errors } = ui;

  const writeServerEnabled = data.payload.allowWriteQueries;
  const writeFormEnabled = form.allowWriteQueries;

  // Only intercept enable transitions — disabling is always allowed without friction.
  const handleToggle = (next: boolean) => {
    if (next && !writeServerEnabled) {
      setPendingEnable(true);
      setConfirmInput('');
      return;
    }
    set({ ...form, allowWriteQueries: next });
  };

  const acceptEnable = () => {
    set({ ...form, allowWriteQueries: true });
    setPendingEnable(false);
    setConfirmInput('');
  };

  return (
    <Card icon={DataBase} title={t('adminSettings:dbAdmin.title')}>
      <HotReloadHint isHotReloadable={data.isHotReloadable} />
      <p className="text-xs text-on-surface-variant mb-3">
        {t('adminSettings:dbAdmin.description')}
      </p>
      <Toggle
        label={t('adminSettings:dbAdmin.allowWriteQueries')}
        checked={writeFormEnabled}
        onChange={handleToggle}
        configKey="DbAdmin:AllowWriteQueries"
        effectiveSource={data.effectiveSource}
        isEnvLocked={isEnvLocked}
      />
      {writeFormEnabled && (
        <p className="text-[11px] text-amber-700 flex items-center gap-1 ml-6 mb-2">
          <SecurityServices size={12} />
          {t('adminSettings:dbAdmin.writeWarning')}
        </p>
      )}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3 mt-3">
        <NumberInput
          label={t('adminSettings:dbAdmin.timeout')}
          value={form.queryTimeoutSeconds}
          min={1}
          max={600}
          onChange={(v) => set({ ...form, queryTimeoutSeconds: v })}
          configKey="DbAdmin:QueryTimeoutSeconds"
          effectiveSource={data.effectiveSource}
          isEnvLocked={isEnvLocked}
          hint={t('adminSettings:dbAdmin.timeoutHint')}
        />
        <NumberInput
          label={t('adminSettings:dbAdmin.maxRows')}
          value={form.queryMaxRows}
          min={1}
          max={1_000_000}
          onChange={(v) => set({ ...form, queryMaxRows: v })}
          configKey="DbAdmin:QueryMaxRows"
          effectiveSource={data.effectiveSource}
          isEnvLocked={isEnvLocked}
          hint={t('adminSettings:dbAdmin.maxRowsHint')}
        />
      </div>
      <ErrorsAndSave
        errors={errors}
        onSave={() => save({
          AllowWriteQueries: form.allowWriteQueries,
          QueryTimeoutSeconds: form.queryTimeoutSeconds,
          QueryMaxRows: form.queryMaxRows,
        })}
      />
      {ui.dialog}
      {pendingEnable && (
        <ConfirmEnableWriteDialog
          phrase={CONFIRM_PHRASE}
          input={confirmInput}
          onInput={setConfirmInput}
          onCancel={() => { setPendingEnable(false); setConfirmInput(''); }}
          onConfirm={acceptEnable}
        />
      )}
    </Card>
  );
}

function ConfirmEnableWriteDialog({
  phrase, input, onInput, onCancel, onConfirm,
}: Readonly<{
  phrase: string;
  input: string;
  onInput: (v: string) => void;
  onCancel: () => void;
  onConfirm: () => void;
}>) {
  const { t } = useTranslation(['adminSettings', 'common']);
  const ok = input === phrase;

  return (
    <div
      className="fixed inset-0 bg-black/30 backdrop-blur-sm flex items-center justify-center z-50"
      onClick={onCancel}
      onKeyDown={(e) => e.key === 'Escape' && onCancel()}
      role="presentation"
      tabIndex={-1}
    >
      <div
        className="bg-surface-lowest rounded-lg shadow-xl p-6 w-full max-w-md"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-lg font-semibold text-on-surface flex items-center gap-2">
            <SecurityServices size={18} className="text-amber-600" />
            {t('adminSettings:dbAdmin.confirmEnableTitle')}
          </h3>
          <button onClick={onCancel} className="p-1 text-on-surface-variant hover:bg-surface-container rounded">
            <Close size={16} />
          </button>
        </div>
        <p className="text-sm text-on-surface-variant mb-3">
          {t('adminSettings:dbAdmin.confirmEnableBody')}
        </p>
        <p className="text-xs text-on-surface-variant mb-1">
          {t('adminSettings:dbAdmin.confirmEnablePrompt', { phrase })}
        </p>
        <input
          type="text"
          value={input}
          onChange={(e) => onInput(e.target.value)}
          autoFocus
          className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-amber-500"
        />
        <div className="flex justify-end gap-2 mt-4">
          <button
            onClick={onCancel}
            className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md"
          >
            {t('common:cancel')}
          </button>
          <button
            onClick={onConfirm}
            disabled={!ok}
            className="px-4 py-2 bg-amber-600 text-white text-sm rounded-md hover:bg-amber-700 disabled:opacity-50"
          >
            {t('adminSettings:dbAdmin.confirmEnableButton')}
          </button>
        </div>
      </div>
    </div>
  );
}
