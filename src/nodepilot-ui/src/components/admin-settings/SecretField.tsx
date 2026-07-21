import { Close, View, ViewOff } from '@carbon/icons-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

/**
 * Sentinel value the backend recognises as "keep the currently persisted ciphertext".
 * Mirrors `SettingsSchema.UnchangedSecretSentinel` on the server.
 */
export const UNCHANGED_SECRET_SENTINEL = '__unchanged__';

export type SecretFieldMode =
  /** A value is persisted; the user has not opened the edit affordance yet. */
  | 'keep'
  /** The user wants to enter a new plaintext value (rotate / set). */
  | 'change'
  /** The user wants to drop the persisted value entirely. */
  | 'clear';

type Props = {
  /** Label rendered above the input (already translated). */
  label: string;
  /** True when the backend reports a persisted (non-null) value for this field. */
  hasPersistedValue: boolean;
  /** Disable all interaction (e.g. because EnvVars override this field). */
  disabled?: boolean;
  /** Current draft value for the "change" mode. */
  value: string;
  /** Mode change callback — drives the parent's serialise-on-save logic. */
  onModeChange: (mode: SecretFieldMode) => void;
  /** Plaintext input handler — only relevant in the "change" mode. */
  onValueChange: (value: string) => void;
  /** Current mode. */
  mode: SecretFieldMode;
  /** Optional id for the input (a11y). */
  inputId?: string;
};

/**
 * Single source of truth for how the UI handles encrypted secret fields. The same
 * widget renders three states: a persisted-but-hidden value, a fresh value the user
 * is currently typing, and an intentionally-cleared field. The serialisation contract
 * for the parent's save handler is:
 *
 * <list type="bullet">
 *   <item><c>mode==='keep'</c>  → send the {@link UNCHANGED_SECRET_SENTINEL} string</item>
 *   <item><c>mode==='change'</c> → send the typed plaintext (server encrypts it)</item>
 *   <item><c>mode==='clear'</c>  → send <c>null</c> (server drops the field)</item>
 * </list>
 *
 * Defaults to <c>'keep'</c> on first render when the backend reports a persisted value,
 * so accidental edits to unrelated fields don't silently reset the secret.
 */
export function SecretField({
  label,
  hasPersistedValue,
  disabled,
  value,
  mode,
  onModeChange,
  onValueChange,
  inputId,
}: Readonly<Props>) {
  const { t } = useTranslation(['adminSettings']);
  const [show, setShow] = useState(false);

  return (
    <div>
      <label className="block text-xs font-medium text-on-surface-variant mb-1" htmlFor={inputId}>
        {label}
      </label>
      {mode === 'keep' && hasPersistedValue && (
        <div className="flex items-center gap-2">
          <input
            type="text"
            value={t('adminSettings:secretMasked')}
            disabled
            className="flex-1 px-3 py-2 border border-outline-variant rounded-md text-sm bg-surface-low text-on-surface-variant"
          />
          {!disabled && (
            <>
              <button
                type="button"
                onClick={() => onModeChange('change')}
                className="px-3 py-1.5 text-sm text-blue-600 hover:bg-blue-50 rounded-md whitespace-nowrap"
              >
                {t('adminSettings:secretActionChange')}
              </button>
              <button
                type="button"
                onClick={() => onModeChange('clear')}
                className="p-1.5 text-red-600 hover:bg-red-50 rounded-md"
                aria-label={t('adminSettings:secretActionClear')}
              >
                <Close size={14} />
              </button>
            </>
          )}
        </div>
      )}
      {(mode === 'change' || !hasPersistedValue) && (
        <div className="flex items-center gap-2">
          <input
            id={inputId}
            type={show ? 'text' : 'password'}
            value={value}
            onChange={(e) => {
              onValueChange(e.target.value);
              if (mode !== 'change') onModeChange('change');
            }}
            disabled={disabled}
            autoComplete="new-password"
            className="flex-1 px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low"
          />
          <button
            type="button"
            onClick={() => setShow((s) => !s)}
            disabled={disabled}
            className="p-1.5 text-on-surface-variant hover:bg-surface-low rounded-md"
            aria-label={show ? t('adminSettings:secretToggleHide') : t('adminSettings:secretToggleShow')}
          >
            {show ? <ViewOff size={16} /> : <View size={16} />}
          </button>
          {hasPersistedValue && (
            <button
              type="button"
              onClick={() => {
                onValueChange('');
                onModeChange('keep');
              }}
              className="px-3 py-1.5 text-sm text-on-surface-variant hover:bg-surface-low rounded-md whitespace-nowrap"
            >
              {t('adminSettings:secretActionKeep')}
            </button>
          )}
        </div>
      )}
      {mode === 'clear' && (
        <div className="flex items-center gap-2">
          <input
            type="text"
            value=""
            disabled
            placeholder=""
            className="flex-1 px-3 py-2 border border-red-300 rounded-md text-sm bg-red-50 text-red-700"
          />
          <button
            type="button"
            onClick={() => onModeChange('keep')}
            className="px-3 py-1.5 text-sm text-on-surface-variant hover:bg-surface-low rounded-md whitespace-nowrap"
          >
            {t('adminSettings:cancelButton')}
          </button>
        </div>
      )}
    </div>
  );
}

/**
 * Translate a {@link SecretFieldMode} + plaintext into the payload value the backend
 * expects. Single helper so every section's save handler ends up using the same shape.
 *
 * <para>Empty plaintext in "change" mode is treated as <c>null</c> — the operator
 * cleared the input and left it blank, which is semantically "no value", not "empty
 * string is the new password".</para>
 */
export function serializeSecretField(mode: SecretFieldMode, value: string): string | null {
  if (mode === 'keep') return UNCHANGED_SECRET_SENTINEL;
  if (mode === 'clear') return null;
  return value.length === 0 ? null : value;
}
