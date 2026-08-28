import { DataBase, SecurityServices } from '@carbon/icons-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { TypedPhraseConfirmDialog } from '../common/TypedPhraseConfirmDialog';
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
 * `AllowWriteQueries` lets any Admin run UPDATE/DELETE/DROP from the query pane, bypassing
 * per-table guards, so enabling it requires a typed-phrase confirmation dialog. Disabling
 * needs no confirmation. Timeout and row cap are plain tuning knobs with no security dimension.
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
        <TypedPhraseConfirmDialog
          phrase={CONFIRM_PHRASE}
          input={confirmInput}
          onInput={setConfirmInput}
          onCancel={() => { setPendingEnable(false); setConfirmInput(''); }}
          onConfirm={acceptEnable}
          title={t('adminSettings:dbAdmin.confirmEnableTitle')}
          body={t('adminSettings:dbAdmin.confirmEnableBody')}
          prompt={t('adminSettings:dbAdmin.confirmEnablePrompt', { phrase: CONFIRM_PHRASE })}
          confirmLabel={t('adminSettings:dbAdmin.confirmEnableButton')}
        />
      )}
    </Card>
  );
}
