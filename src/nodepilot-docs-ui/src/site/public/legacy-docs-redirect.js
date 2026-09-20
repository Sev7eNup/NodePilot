// Forwards old documentation links (for example #/en/deployment/logs) to docs/ before the
// website renders. Classic, non-deferred script, so it runs while the parser is still in <head>.
// The list repeats SITE_ROUTE_SEGMENTS from src/site/router.ts; legacy-redirect.test.ts keeps both equal.
(function () {
  var siteRoutes = ['erleben', 'produkt', 'blog', 'impressum', 'datenschutz'];
  var hash = location.hash;
  if (hash.indexOf('#/') !== 0) return;
  var segment = hash.slice(2).split('/')[0];
  if (segment && siteRoutes.indexOf(segment) === -1) location.replace('docs/' + hash);
})();
