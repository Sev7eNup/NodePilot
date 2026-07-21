import { context, trace, type Span, type Tracer } from '@opentelemetry/api';
import { ZoneContextManager } from '@opentelemetry/context-zone';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { registerInstrumentations } from '@opentelemetry/instrumentation';
import { DocumentLoadInstrumentation } from '@opentelemetry/instrumentation-document-load';
import { FetchInstrumentation } from '@opentelemetry/instrumentation-fetch';
import { UserInteractionInstrumentation } from '@opentelemetry/instrumentation-user-interaction';
import { XMLHttpRequestInstrumentation } from '@opentelemetry/instrumentation-xml-http-request';
import { resourceFromAttributes } from '@opentelemetry/resources';
import { BatchSpanProcessor, WebTracerProvider } from '@opentelemetry/sdk-trace-web';
import type { ObservabilityConfig } from '../types/api';

const TRACER_NAME = 'nodepilot-ui';

let initialized = false;
let tracer: Tracer | null = null;

/**
 * Initialize the OpenTelemetry Web SDK. Idempotent — safe to call multiple times.
 * Returns the tracer when initialization succeeded, otherwise null. If the server
 * hasn't configured a browser endpoint the SDK is not started and all tracing
 * calls become cheap no-ops.
 */
export function initTelemetry(config: ObservabilityConfig): Tracer | null {
  if (initialized) return tracer;
  if (!config.enabled || !config.browserOtlpEndpoint) return null;

  const provider = new WebTracerProvider({
    // OTel JS 2.x removed the `Resource` class in favour of the resourceFromAttributes()
    // factory (security-fix migration off @opentelemetry/core <2.8.0, W3C-Baggage DoS).
    resource: resourceFromAttributes({
      'service.name': config.serviceName ?? 'nodepilot-ui',
      'service.version': '1.0.0',
      'deployment.environment': config.environment ?? 'dev',
      'nodepilot.node.role': 'ui',
    }),
    spanProcessors: [
      new BatchSpanProcessor(
        new OTLPTraceExporter({ url: config.browserOtlpEndpoint }),
        { maxQueueSize: 2048, scheduledDelayMillis: 3000 },
      ),
    ],
  });

  provider.register({
    contextManager: new ZoneContextManager(),
  });

  registerInstrumentations({
    instrumentations: [
      new DocumentLoadInstrumentation(),
      new UserInteractionInstrumentation({ eventNames: ['click', 'submit'] }),
      new FetchInstrumentation({
        // Propagate traceparent to our own API so backend spans attach as children
        propagateTraceHeaderCorsUrls: [/.*/],
        clearTimingResources: true,
        ignoreUrls: [/\/api\/observability\//, /\/hubs\//],
      }),
      new XMLHttpRequestInstrumentation({
        propagateTraceHeaderCorsUrls: [/.*/],
        ignoreUrls: [/\/api\/observability\//, /\/hubs\//],
      }),
    ],
  });

  tracer = trace.getTracer(TRACER_NAME);
  initialized = true;
   
  console.info('[otel] web SDK initialized', { endpoint: config.browserOtlpEndpoint });
  return tracer;
}

export function getTracer(): Tracer | null {
  return tracer;
}

/**
 * Run a function inside a manually-named span. No-ops when telemetry is disabled.
 */
export function withSpan<T>(name: string, fn: (span: Span | null) => T, attributes?: Record<string, string | number | boolean>): T {
  if (!tracer) return fn(null);
  const span = tracer.startSpan(name);
  if (attributes) span.setAttributes(attributes);
  return context.with(trace.setSpan(context.active(), span), () => {
    try {
      const result = fn(span);
      if (result instanceof Promise) {
        return (result as unknown as Promise<unknown>).then(
          (v) => { span.end(); return v; },
          (err) => { span.recordException(err); span.setStatus({ code: 2, message: String(err) }); span.end(); throw err; },
        ) as unknown as T;
      }
      span.end();
      return result;
    } catch (err) {
      span.recordException(err as Error);
      span.setStatus({ code: 2, message: String(err) });
      span.end();
      throw err;
    }
  });
}
