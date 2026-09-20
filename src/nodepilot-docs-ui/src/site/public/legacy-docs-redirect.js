// Forwards two kinds of old links before the website renders: documentation links such as
// #/en/deployment/logs to docs/, and the website's former addresses to the current ones. The
// website used hash routes with German segments; both changed, so an old link needs the hash
// stripped and the segments renamed. Classic, non-deferred script, so it runs while the parser
// is still in <head>.
// The list repeats SITE_ROUTE_SEGMENTS from src/site/router.ts; legacy-redirect.test.ts keeps both equal.
(function () {
  var siteRoutes = ['walkthrough', 'product', 'blog', 'impressum', 'datenschutz'];
  var renamed = { erleben: 'walkthrough', produkt: 'product', 'warum-nodepilot': 'why-nodepilot' };
  var hash = location.hash;
  if (hash.indexOf('#/') !== 0) return;
  var path = hash.slice(2).replace(/\/+$/, '');
  var parts = path.split('/');
  var segment = parts[0];
  if (segment && siteRoutes.indexOf(segment) === -1 && !renamed[segment]) {
    location.replace('docs/' + hash);
    return;
  }
  for (var i = 0; i < parts.length; i++) parts[i] = renamed[parts[i]] || parts[i];
  // Old links only ever pointed at the site root, so the path is relative to this document.
  location.replace(path === '' ? '.' : parts.join('/') + '/');
})();
