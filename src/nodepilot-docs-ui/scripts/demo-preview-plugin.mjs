/** Mirrors the Apache demo fallback for the assembled local preview. */
export function demoPreviewFallback() {
  return {
    name: 'np-demo-preview-fallback',
    configurePreviewServer(server) {
      server.middlewares.use((request, _response, next) => {
        const url = new URL(request.url ?? '/', 'http://localhost')
        if (/^\/demo\/(?!assets(?:\/|$))[^.]*$/.test(url.pathname)) {
          request.url = `/demo/index.html${url.search}`
        }
        next()
      })
    },
  }
}
