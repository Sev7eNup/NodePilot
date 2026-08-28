import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
// Atelier designer skin, imported after index.css on purpose: its selectors tie with the
// per-skin `.np-designer` overrides on specificity, so source order decides the cascade.
import './styles/designer-atelier.css'
import './i18n'
import App from './App.tsx'
import type { ObservabilityConfig } from './types/api'

// Bootstrap telemetry before rendering so the document-load span is captured.
// Fire-and-forget: telemetry config failures must never block the UI.
//
// The OpenTelemetry web SDK is imported dynamically because OpenTelemetry:Enabled defaults to
// false. A static import would put the whole SDK into the boot chunk for every user, including
// those who never emit a span. The import happens only once the server reports telemetry as
// on, which is the same condition initTelemetry checks first.
fetch('/api/observability/config')
  .then((r) => (r.ok ? (r.json() as Promise<ObservabilityConfig>) : null))
  .then(async (cfg) => {
    if (!cfg?.enabled || !cfg.browserOtlpEndpoint) return;
    const { initTelemetry } = await import('./telemetry/otel');
    initTelemetry(cfg);
  })
  .catch(() => { /* ignore: the UI works without telemetry */ });

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
