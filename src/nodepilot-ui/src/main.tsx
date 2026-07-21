import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
// Atelier designer skin — imported AFTER index.css on purpose: its selectors tie with the
// per-skin `.np-designer` overrides on specificity, so source order decides the cascade.
import './styles/designer-atelier.css'
import './i18n'
import App from './App.tsx'
import { initTelemetry } from './telemetry/otel'
import type { ObservabilityConfig } from './types/api'

// Bootstrap telemetry before rendering so the document-load span is captured.
// Fire-and-forget: telemetry config failures must never block the UI.
fetch('/api/observability/config')
  .then((r) => (r.ok ? (r.json() as Promise<ObservabilityConfig>) : null))
  .then((cfg) => { if (cfg) initTelemetry(cfg); })
  .catch(() => { /* ignore — UI works without telemetry */ });

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
