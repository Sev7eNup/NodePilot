/**
 * Response builders for the demo backend.
 *
 * The content type is load-bearing, not cosmetic: `useDatabaseHealth` treats a non-JSON
 * content type as an outage and puts the app-wide banner up, and `api/client.ts` parses the
 * error envelope out of `application/problem+json`.
 */

const JSON_TYPE = 'application/json';
const PROBLEM_TYPE = 'application/problem+json';

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': JSON_TYPE },
  });
}

export function noContent(): Response {
  return new Response(null, { status: 204 });
}

/** Problem response in the shape `ApiError` expects (ADR 0007: a stable `code`). */
export function problem(status: number, code: string, message: string): Response {
  return new Response(JSON.stringify({ code, message, title: code, status }), {
    status,
    headers: { 'content-type': PROBLEM_TYPE },
  });
}

export function notFound(what: string): Response {
  return problem(404, 'NOT_FOUND', `${what} was not found.`);
}

export function badRequest(code: string, message: string): Response {
  return problem(400, code, message);
}

export function conflict(code: string, message: string): Response {
  return problem(409, code, message);
}

/**
 * Explains why an action cannot work in the demo. Used for anything that would need a real
 * server: remote execution, connectivity probes, SMTP, backups, raw SQL.
 *
 * 501 rather than an error status the UI would retry or treat as an outage.
 */
export function notInDemo(what: string): Response {
  return problem(
    501,
    'DEMO_NOT_AVAILABLE',
    `${what} needs a real NodePilot server. This demo runs entirely in your browser.`,
  );
}

/** A text/plain body, for the few endpoints that return raw text. */
export function text(body: string, status = 200): Response {
  return new Response(body, { status, headers: { 'content-type': 'text/plain; charset=utf-8' } });
}

/** A file download, for export endpoints. */
export function download(body: string, filename: string, type = JSON_TYPE): Response {
  return new Response(body, {
    status: 200,
    headers: {
      'content-type': type,
      'content-disposition': `attachment; filename="${filename}"`,
    },
  });
}
