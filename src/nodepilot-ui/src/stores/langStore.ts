import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import i18n, { SUPPORTED_LANGS, type AppLang } from '../i18n';

interface LangStore {
  lang: AppLang;
  setLang: (l: AppLang) => void;
}

function applyLang(l: AppLang) {
  if (i18n.language !== l) {
    void i18n.changeLanguage(l);
  }
  if (typeof document !== 'undefined') {
    document.documentElement.setAttribute('lang', l);
  }
}

function detectInitial(): AppLang {
  const fromI18n = i18n.language?.split('-')[0] as AppLang | undefined;
  return fromI18n && (SUPPORTED_LANGS as readonly string[]).includes(fromI18n) ? fromI18n : 'de';
}

export const useLangStore = create<LangStore>()(
  persist(
    (set) => ({
      lang: detectInitial(),
      setLang: (l) => {
        applyLang(l);
        set({ lang: l });
      },
    }),
    {
      name: 'nodepilot.lang.store',
      partialize: (s) => ({ lang: s.lang }),
      onRehydrateStorage: () => (state) => {
        if (!state) return;
        applyLang(state.lang);
      },
    },
  ),
);
