import {
  BareMetalServer,
  ColorPalette,
  Translate,
  User,
} from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import { useRole } from '../lib/rbac';
import { useThemeStore, THEMES, type Theme } from '../stores/themeStore';
import { useLangStore } from '../stores/langStore';
import type { AppLang } from '../i18n';
import { SystemSettingsPage } from './SystemSettingsPage';

type TopTab = 'personal' | 'system';

export function SettingsPage() {
  const { t } = useTranslation(['settings', 'adminSettings']);
  const { isAdmin } = useRole();
  // Deep-link support: the dashboard banner shortcuts navigate to /settings?tab=system
  // (LLM config lives under the System -> Integrations sub-tab) or ?tab=personal. The
  // Keep tab state in the URL so the app-header breadcrumb, deep links and browser
  // back/forward navigation always describe the same view.
  const [searchParams, setSearchParams] = useSearchParams();
  const topTab: TopTab = isAdmin && searchParams.get('tab') === 'system' ? 'system' : 'personal';
  const setTopTab = (next: TopTab) => {
    const params = new URLSearchParams(searchParams);
    params.set('tab', next);
    if (next === 'personal') params.delete('section');
    else if (!params.get('section')) params.set('section', 'integrations');
    setSearchParams(params);
  };

  // The System view has a sub-tab bar with 8 entries (Integrations …
  // Database) plus wide data sections (notably the DB-Admin table). At max-w-4xl
  // the tab bar wraps onto two rows — so System gets a wider max-width so all tabs
  // fit on one row. Personal stays narrow (it's just forms).
  const systemView = isAdmin && topTab === 'system';

  return (
    <div className={`space-y-4 mx-auto np-fade-up ${systemView ? 'max-w-7xl' : 'max-w-4xl'}`}>
      <header>
        <p className="text-sm text-on-surface-variant font-label max-w-3xl">{t('settings:subtitle')}</p>
      </header>
      {/* Top-level tab bar — Personal / System. The System tab is only rendered for
          Admins; non-admins see the page exactly as before (just the Personal content). */}
      {isAdmin && (
        <div className="np-tab-list">
          <button
            type="button"
            onClick={() => setTopTab('personal')}
            className={`np-tab ${topTab === 'personal' ? 'is-active' : ''}`}
          >
            <User size={14} />
            {t('adminSettings:tabPersonal')}
          </button>
          <button
            type="button"
            onClick={() => setTopTab('system')}
            className={`np-tab ${topTab === 'system' ? 'is-active' : ''}`}
          >
            <BareMetalServer size={14} />
            {t('adminSettings:tabSystem')}
          </button>
        </div>
      )}
      {systemView ? <SystemSettingsPage /> : <PersonalSettings />}
    </div>
  );
}

/**
 * Theme and language, extracted into its own component so the top-level tab bar can switch
 * between Personal and the Admin System sub-page without breaking the React component tree.
 */
function PersonalSettings() {
  const { t } = useTranslation(['settings']);
  const { theme, setTheme } = useThemeStore();
  const { lang, setLang } = useLangStore();

  // Derived from the THEMES registry (+ the OS-following `system`) so a new skin
  // shows up here automatically — no per-scheme wiring.
  const themeOptions: { value: Theme; key: string }[] = [
    ...THEMES.map((th) => ({ value: th.id as Theme, key: th.labelKey })),
    { value: 'system', key: 'themeSystem' },
  ];

  const langOptions: { value: AppLang; key: 'languageDe' | 'languageEn' }[] = [
    { value: 'de', key: 'languageDe' },
    { value: 'en', key: 'languageEn' },
  ];

  return (
    <div className="space-y-6">
      {/* Appearance section: theme + language */}
      <div className="np-card p-4">
        <h3 className="font-semibold text-on-surface flex items-center gap-2 mb-4">
          <ColorPalette size={18} /> {t('settings:appearance')}
        </h3>
        <div className="space-y-5">
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-2">{t('settings:theme')}</label>
            <div className="flex flex-wrap gap-2">
              {themeOptions.map(({ value, key }) => (
                <button
                  key={value}
                  type="button"
                  onClick={() => setTheme(value)}
                  className={`np-btn np-btn-sm ${theme === value ? 'np-btn-selected' : 'np-btn-secondary'}`}
                >
                  {t(`settings:${key}`)}
                </button>
              ))}
            </div>
          </div>
          <div className="border-t border-outline-variant pt-4">
            <label className="text-xs font-medium text-on-surface-variant mb-2 flex items-center gap-1.5">
              <Translate size={12} /> {t('settings:language')}
            </label>
            <div className="flex flex-wrap gap-2">
              {langOptions.map(({ value, key }) => (
                <button
                  key={value}
                  type="button"
                  onClick={() => setLang(value)}
                  className={`np-btn np-btn-sm ${lang === value ? 'np-btn-selected' : 'np-btn-secondary'}`}
                >
                  {t(`settings:${key}`)}
                </button>
              ))}
            </div>
            <p className="text-[11px] text-outline mt-2">{t('settings:languageHint')}</p>
          </div>
        </div>
      </div>
    </div>
  );
}
