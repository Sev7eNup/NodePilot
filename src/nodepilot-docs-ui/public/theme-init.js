// Resolves the theme before first paint, otherwise dark-mode readers see a white flash.
// Loaded as a classic, non-deferred script from index.html so it still runs while the parser
// is in <head>. It lives here rather than inline because the API serves this bundle at /docs
// under a `script-src 'self'` CSP with no nonce, which blocks inline script.
// useTheme seeds its state from the class this sets.
try {
  var t =
    localStorage.getItem('np-docs-theme') ||
    (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  document.documentElement.classList.toggle('dark', t === 'dark');
} catch (e) {
  /* private mode / storage disabled — fall through to the light default */
}
