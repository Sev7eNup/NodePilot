// Forwards the documentation addresses from before this bundle had real ones: #/de/cli becomes
// de/cli/ below the documentation root. Classic and non-deferred, so it runs before the bundle
// and no wrong page is ever painted.
//
// The target is resolved against the same np-docs-base meta the router uses, which makes it
// correct at every depth and on every host. An in-page anchor (#section) is left alone.
(function () {
  var hash = location.hash
  if (hash.indexOf('#/') !== 0) return
  var languages = ['en', 'de']
  var path = hash.slice(2).replace(/^\/+|\/+$/g, '')
  // A link without a language segment used to be resolved in the browser; these addresses are
  // real files now, and the default language is the one those links came from.
  if (path && languages.indexOf(path.split('/')[0]) === -1) path = languages[0] + '/' + path
  var meta = document.querySelector('meta[name="np-docs-base"]')
  var base = meta ? meta.content : './'
  location.replace(new URL(base + (path ? path + '/' : ''), location.href).pathname)
})()
