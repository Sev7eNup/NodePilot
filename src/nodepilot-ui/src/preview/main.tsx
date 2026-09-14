// Throwaway preview: compare the Minimal palettes without initializing the application.
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createInstance } from 'i18next';
import { I18nextProvider, initReactI18next } from 'react-i18next';
import '../index.css';
import '@xyflow/react/dist/style.css';
import './preview.css';
import './designer-preview.css';
import { previewResources } from './copy';
import { designerResources } from './designer-copy';
import { SkinPreview } from './SkinPreview';

const params = new URLSearchParams(location.search);
const variant = params.get('variant') === 'dark' ? 'dark' : 'light';
document.documentElement.dataset.previewVariant = variant;
document.documentElement.classList.toggle('dark', variant === 'dark');

const i18n = createInstance();
await i18n.use(initReactI18next).init({
  lng: 'de',
  fallbackLng: 'de',
  defaultNS: 'preview',
  interpolation: { escapeValue: false },
  resources: {
    de: { preview: previewResources.de, designerPreview: designerResources.de },
    en: { preview: previewResources.en, designerPreview: designerResources.en },
  },
});

createRoot(document.getElementById('root')!).render(
  <StrictMode><I18nextProvider i18n={i18n}><SkinPreview initialVariant={variant} /></I18nextProvider></StrictMode>,
);
