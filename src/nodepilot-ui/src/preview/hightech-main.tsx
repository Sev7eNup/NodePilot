// Throwaway dark-only skin study, sharing the existing in-memory preview interactions.
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createInstance } from 'i18next';
import { I18nextProvider, initReactI18next } from 'react-i18next';
import '../index.css';
import '@xyflow/react/dist/style.css';
import './preview.css';
import './designer-preview.css';
import './hightech-preview.css';
import './hightech-designer.css';
import { hightechResources } from './hightech-copy';
import { designerResources } from './designer-copy';
import { SkinPreview } from './SkinPreview';

document.documentElement.dataset.previewVariant = 'dark';
document.documentElement.dataset.previewSkin = 'hightech';
document.documentElement.classList.add('dark');

const i18n = createInstance();
await i18n.use(initReactI18next).init({
  lng: 'de', fallbackLng: 'de', defaultNS: 'preview',
  interpolation: { escapeValue: false },
  resources: {
    de: { preview: hightechResources.de, designerPreview: designerResources.de },
    en: { preview: hightechResources.en, designerPreview: designerResources.en },
  },
});

createRoot(document.getElementById('root')!).render(
  <StrictMode><I18nextProvider i18n={i18n}><SkinPreview initialVariant="dark" presentation="hightech" /></I18nextProvider></StrictMode>,
);
