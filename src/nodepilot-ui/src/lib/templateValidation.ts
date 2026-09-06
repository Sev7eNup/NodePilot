import i18n from '../i18n';
import type { UpstreamVariable } from './upstreamVariables';

export type TemplateValidationSeverity = 'error' | 'warning';

export interface TemplateValidationIssue {
  severity: TemplateValidationSeverity;
  message: string;
  token?: string;
}

export interface TemplateValidationResult {
  status: 'ok' | 'warning' | 'error';
  issues: TemplateValidationIssue[];
}

// manual.* is injected at runtime (trigger data, forEach child params like {{manual.item}}) and
// must be tolerated like globals.*, matching workflowLint's runtimePrefixes.
// Mirrors VariableResolver's grammar exactly. A shape this accepts but the engine does not
// resolve is worse than a false warning: the placeholder survives, no unresolved-template
// check sees it (that check scans for the engine's own patterns), and the step reports success
// having written the literal `{{...}}` wherever the value belonged.
const ENGINE_REFERENCE_RE = /^(globals\.[A-Za-z0-9_-]+|manual\.[A-Za-z0-9_-]+|[A-Za-z0-9_-]+\.(output|error|success|param\.[\w-]+))$/;

// Namespaces resolved at runtime whose members cannot be enumerated at lint time.
// These never produce a "not in upstream" warning.
const RUNTIME_HEADS = new Set(['globals', 'manual']);

export function hasTemplatePlaceholder(value: string): boolean {
  // Field values are typed `string`, but malformed node data can carry a non-string (e.g. a
  // restApi `headers` object). Treat non-strings as having no placeholder so one bad field
  // cannot crash the properties panel.
  return typeof value === 'string' && (value.includes('{{') || value.includes('}}'));
}

export function validateTemplateExpression(
  value: string,
  upstreamVars: UpstreamVariable[] = [],
): TemplateValidationResult {
  const issues: TemplateValidationIssue[] = [];
  if (!hasTemplatePlaceholder(value)) return { status: 'ok', issues };

  const knownRefs = new Set(upstreamVars.map((v) => stripBraces(v.expression)));
  const knownHeads = new Set(upstreamVars.map((v) => v.variable.split('.')[0]).filter(Boolean));

  let cursor = 0;
  while (cursor < value.length) {
    const open = value.indexOf('{{', cursor);
    const closeBeforeOpen = value.indexOf('}}', cursor);
    if (closeBeforeOpen !== -1 && (open === -1 || closeBeforeOpen < open)) {
      issues.push({ severity: 'error', message: i18n.t('properties:template.closingWithoutOpening'), token: '}}' });
      cursor = closeBeforeOpen + 2;
      continue;
    }
    if (open === -1) break;
    const close = value.indexOf('}}', open + 2);
    if (close === -1) {
      issues.push({ severity: 'error', message: i18n.t('properties:template.missingClosing'), token: value.slice(open) });
      break;
    }
    const token = value.slice(open + 2, close).trim();
    if (!token) {
      issues.push({ severity: 'error', message: i18n.t('properties:template.empty'), token: '{{}}' });
    } else if (token.includes('{{')) {
      // Nested construction ({{globals.{{x}}}} etc.) cannot be tokenized reliably by this
      // linear scan, so skip it. Consume one closing '}}' per nested open so the leftovers
      // are not reported as a closing without an opening.
      let extraOpens = (token.match(/\{\{/g) ?? []).length;
      let scan = close + 2;
      while (extraOpens > 0) {
        const nextClose = value.indexOf('}}', scan);
        if (nextClose === -1) break;
        scan = nextClose + 2;
        extraOpens--;
      }
      cursor = scan;
      continue;
    } else if (!ENGINE_REFERENCE_RE.test(token)) {
      issues.push({
        severity: 'error',
        message: i18n.t('properties:template.unsupportedShape'),
        token: `{{${token}}}`,
      });
    } else if (!RUNTIME_HEADS.has(token.split('.')[0]) && upstreamVars.length > 0 && !knownRefs.has(token)) {
      const head = token.split('.')[0];
      const tail = token.slice(head.length + 1);
      // .error/.success/.errorOutput exist on every step, but the picker only lists .output
      // and params, so a known producer with one of these tails is not a warning.
      const engineTailOnKnownHead = knownHeads.has(head)
        && (tail === 'error' || tail === 'success' || tail === 'errorOutput');
      if (!engineTailOnKnownHead) {
        issues.push({
          severity: 'warning',
          message: knownHeads.has(head)
            ? i18n.t('properties:template.notInUpstream')
            : i18n.t('properties:template.notUpstreamProducer'),
          token: `{{${token}}}`,
        });
      }
    }
    cursor = close + 2;
  }

  const status = issues.some((i) => i.severity === 'error')
    ? 'error'
    : issues.length > 0
      ? 'warning'
      : 'ok';
  return { status, issues };
}

function stripBraces(expression: string): string {
  return expression.startsWith('{{') && expression.endsWith('}}')
    ? expression.slice(2, -2).trim()
    : expression.trim();
}
