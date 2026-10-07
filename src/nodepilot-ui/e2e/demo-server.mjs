/**
 * Static server for the demo smoke test.
 *
 * Serves the built demo under /demo/ with the same page fallback as Apache.
 * Asset misses remain 404 so deep-link tests detect an incorrect asset base.
 */
import { createReadStream, existsSync, statSync } from 'node:fs'
import { createServer } from 'node:http'
import { extname, join, normalize, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)), 'dist-demo')
const PREFIX = '/demo'
const PORT = Number(process.env.DEMO_PORT ?? 4180)

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2',
  '.map': 'application/json; charset=utf-8',
}

const server = createServer((request, response) => {
  const { pathname } = new URL(request.url, `http://localhost:${PORT}`)

  // A sibling of the demo, so the in-app documentation link has somewhere real to point.
  if (pathname === '/docs/' || pathname === '/docs') {
    response.writeHead(200, { 'content-type': TYPES['.html'] })
    response.end('<!doctype html><title>Docs</title><h1>Documentation</h1>')
    return
  }

  if (pathname !== PREFIX && !pathname.startsWith(`${PREFIX}/`)) {
    response.writeHead(404).end('not found')
    return
  }

  const relative = pathname.slice(PREFIX.length) || '/'
  let candidate = join(ROOT, normalize(relative === '/' ? '/index.html' : relative))
  // Mirrors the Apache demo fallback: asset misses never receive the HTML shell.
  if (!existsSync(candidate) && !relative.includes('.') && !relative.startsWith('/assets/')) {
    candidate = join(ROOT, 'index.html')
  }
  if (!candidate.startsWith(ROOT) || !existsSync(candidate) || !statSync(candidate).isFile()) {
    response.writeHead(404).end('not found')
    return
  }

  response.writeHead(200, {
    'content-type': TYPES[extname(candidate)] ?? 'application/octet-stream',
    'cache-control': 'no-store',
  })
  createReadStream(candidate).pipe(response)
})

server.listen(PORT, '127.0.0.1', () => {
  console.log(`demo server listening on http://127.0.0.1:${PORT}${PREFIX}/`)
})
