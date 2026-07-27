import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
// Atelier designer skin — imported AFTER index.css on purpose: its selectors tie with the
// per-skin `.np-designer` overrides on specificity, so source order decides the cascade.
import './styles/designer-atelier.css'
import './i18n'
import App from './App.tsx'
import type { ObservabilityConfig } from './types/api'

// Bootstrap telemetry before rendering so the document-load span is captured.
// Fire-and-forget: telemetry config failures must never block the UI.
//
// The OpenTelemetry web SDK is loaded dynamically, not statically: OpenTelemetry:Enabled
// defaults to false, so a static import pulls the whole SDK (the largest single contributor
// to the boot chunk) into the first payload of every user, including the ones who will never
// emit a span. The import now happens only once the server has said telemetry is on — which
// is exactly the condition initTelemetry itself checks first.
fetch('/api/observability/config')
  .then((r) => (r.ok ? (r.json() as Promise<ObservabilityConfig>) : null))
  .then(async (cfg) => {
    if (!cfg?.enabled || !cfg.browserOtlpEndpoint) return;
    const { initTelemetry } = await import('./telemetry/otel');
    initTelemetry(cfg);
  })
  .catch(() => { /* ignore — UI works without telemetry */ });

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
