import { useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import { LANGUAGES, LANGUAGE_LABELS, type Lang } from '../i18n/languages'

/**
 * Language control for the sidebar footer: a segmented `DE | EN`.
 *
 * Both languages are shown so the choice is visible without clicking, and each segment targets
 * its own language directly instead of cycling, so the control still works if `LANGUAGES` gains
 * a third entry.
 *
 * Switching keeps the reader on the same page: only the language segment of the route changes,
 * so `/de/security/hardening` becomes `/en/security/hardening`.
 */
export default function LanguageSwitcher({ lang, current }: { lang: Lang; current: string }) {
  const navigate = useNavigate()
  const { t } = useTranslation()

  return (
    <div className="np-sb-lang" role="group" aria-label={t('ui.language')}>
      {LANGUAGES.map((code) => {
        const active = code === lang
        return (
          <button
            key={code}
            type="button"
            // The active segment stays an enabled button so the group keeps a predictable
            // tab order; pressing it does nothing.
            onClick={() => !active && navigate(`/${code}/${current}`)}
            className={active ? 'is-active' : undefined}
            aria-current={active ? 'true' : undefined}
            // The visible label is a two-letter code; screen readers get the full language name.
            aria-label={LANGUAGE_LABELS[code]}
            title={LANGUAGE_LABELS[code]}
          >
            {code.toUpperCase()}
          </button>
        )
      })}
    </div>
  )
}
