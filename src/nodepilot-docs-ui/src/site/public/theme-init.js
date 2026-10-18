// Resolves the theme before first paint, otherwise light-mode visitors see a dark flash.
// Classic, non-deferred script in <head>; it is a file because the site is served under a
// `script-src 'self'` CSP that blocks inline script. The documentation reads the same key.
try {
  var t =
    localStorage.getItem('np-docs-theme') ||
    (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  document.documentElement.classList.toggle('dark', t === 'dark');
} catch {
  /* storage disabled: keep the dark markup default */
}
