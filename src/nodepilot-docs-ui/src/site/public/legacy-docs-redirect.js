// Forwards two kinds of old links before the website renders: documentation links such as
// #/en/deployment/logs to docs/, and the website's former addresses to the current ones. The
// website used hash routes with German segments; both changed, so an old link needs the hash
// stripped and the segments renamed. Classic, non-deferred script, so it runs while the parser
// is still in <head>.
// The list repeats SITE_ROUTE_SEGMENTS from src/site/router.ts; legacy-redirect.test.ts keeps both equal.
(function () {
  var siteRoutes = ['walkthrough', 'product', 'blog', 'impressum', 'datenschutz'];
  var languages = ['en', 'de'];
  var renamed = { erleben: 'walkthrough', produkt: 'product', 'warum-nodepilot': 'why-nodepilot' };
  var hash = location.hash;
  if (hash.indexOf('#/') !== 0) return;
  var path = hash.slice(2).replace(/\/+$/, '');
  // A hash like #///evil.com leaves an empty first segment, which would join back into a
  // protocol-relative URL and send the visitor off-origin. Such a link is not an old address.
  if (path.charAt(0) === '/' || path.charAt(0) === '\\') {
    location.replace('.');
    return;
  }
  var parts = path.split('/');
  var segment = parts[0];
  if (segment && siteRoutes.indexOf(segment) === -1 && !renamed[segment]) {
    // The documentation has real addresses too, so this lands on the page itself rather than
    // on another hash that the documentation would have to forward a second time. A link
    // without a language segment used to be resolved in the browser; the default language is
    // what those links came from.
    var docsPath = languages.indexOf(segment) === -1 ? languages[0] + '/' + path : path;
    location.replace('docs/' + docsPath + '/');
    return;
  }
  for (var i = 0; i < parts.length; i++) parts[i] = renamed[parts[i]] || parts[i];
  // Old links only ever pointed at the site root, so the path is relative to this document.
  location.replace(path === '' ? '.' : parts.join('/') + '/');
})();
