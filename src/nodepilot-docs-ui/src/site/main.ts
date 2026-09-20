/* NodePilot project website. No backend, analytics or real workflow execution: every
   interaction stays in the page, apart from the links a visitor opens. */
import { mountExperience, type ExperienceController } from './experience/controller'
import designerDark from '../../../../docs/images/designer-dark.png'
import logDark from '../../../../docs/images/log-dark.png'
import aiDark from '../../../../docs/images/ai-dark.png'
import appIconUrl from '../assets/logo-dark.png'
import { detectLang, isLang } from '../i18n/languages'
import {
  EDGES,
  ICON_OFFSET,
  LAYOUTS,
  NODE_KEYS,
  NODE_SHAPE,
  layoutEdge,
  shapePoints,
  type GraphLayout,
  type NodeKey,
} from './graph'
import { applyLanguage, currentLang, format, onLanguageChange, persistLang, setBasePrefix, t } from './i18n'
import { resolveRoute, type SitePage, type SiteRoute } from './router'

const REPO = 'https://github.com/Sev7eNup/NodePilot'
const PAGES: readonly SitePage[] = ['experience', 'home', 'product', 'blog', 'article', 'impressum', 'datenschutz', 'notfound']
const SCREENSHOTS = {
  designer: { file: 'designer-dark.png', src: designerDark },
  logs: { file: 'log-dark.png', src: logDark },
  ai: { file: 'ai-dark.png', src: aiDark },
}
// German-only legal texts, supplied as files. A missing file leaves its page without text.
const LEGAL_TEXTS = import.meta.glob<string>('./legal/*.de.html', { query: '?raw', import: 'default', eager: true })

type ScreenKey = keyof typeof SCREENSHOTS
type ImageState = 'loading' | 'loaded' | 'error'

function isNodeKey(value: string | undefined): value is NodeKey {
  return (NODE_KEYS as readonly (string | undefined)[]).includes(value)
}

function isScreenKey(value: string | undefined): value is ScreenKey {
  return value !== undefined && Object.hasOwn(SCREENSHOTS, value)
}

function $<T extends Element = HTMLElement>(selector: string, root: ParentNode = document): T {
  const element = root.querySelector<T>(selector)
  if (!element) throw new Error(`Missing element: ${selector}`)
  return element
}

function $$<T extends Element = HTMLElement>(selector: string, root: ParentNode = document): T[] {
  return [...root.querySelectorAll<T>(selector)]
}

const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)')
const downloadDialog = $<HTMLDialogElement>('#download-dialog')
const galleryDialog = $<HTMLDialogElement>('#gallery-dialog')
const sidebar = $('#sidebar')
const menuButton = $<HTMLButtonElement>('#menu-button')
const backdrop = $<HTMLButtonElement>('#nav-backdrop')
const main = $('#main-content')
const playButton = $<HTMLButtonElement>('#play-example')
const toast = $('#toast')
const screenreaderStatus = $('#screenreader-status')
const blogSearch = $<HTMLInputElement>('#blog-search')
const productImage = $<HTMLImageElement>('#product-image')
const galleryImage = $<HTMLImageElement>('#gallery-image')

let experience: ExperienceController | undefined
let currentRoute: SiteRoute | null = null
let selectedNode: NodeKey = 'check'
let currentProductTab: ScreenKey = 'designer'
let currentGalleryTab: ScreenKey = 'designer'
// The example runs in a loop while it is on screen, unless the visitor paused it or prefers
// reduced motion.
let demoRun: AbortController | null = null
let demoPaused = reducedMotion.matches
let demoOnScreen = false
let graphLayout: GraphLayout = LAYOUTS.wide
let toastTimer: number | undefined
let currentFilter = 'all'
const imageStates = new Map<HTMLElement, ImageState>()

// Original product icon. If it fails, the "np" text stays instead of an invented logo.
// Set from script because the dev server cannot serve an HTML path outside the site root.
const appIcon = $<HTMLImageElement>('#app-icon')
appIcon.addEventListener('load', () => appIcon.parentElement?.classList.add('loaded'))
appIcon.addEventListener('error', () => {
  appIcon.hidden = true
})
appIcon.src = appIconUrl

for (const body of $$('[data-legal]')) {
  // Author-supplied legal HTML.
  body.innerHTML = LEGAL_TEXTS[`./legal/${body.dataset.legal}.de.html`] ?? ''
}

// The compact icon rail hides the labels, so each item carries its label as name and tooltip.
function labelNavItems(): void {
  for (const link of $$('.nav-item')) {
    const label = link.querySelector('span')?.textContent?.trim()
    if (label) {
      link.setAttribute('aria-label', label)
      link.title = label
    }
  }
}

function setMenu(open: boolean, restoreFocus = false): void {
  const mobile = window.innerWidth <= 760
  const isOpen = open && mobile
  sidebar.classList.toggle('is-open', isOpen)
  backdrop.hidden = !isOpen
  document.body.classList.toggle('menu-open', isOpen)
  menuButton.setAttribute('aria-expanded', String(isOpen))
  renderMenuLabel()
  sidebar.inert = mobile && !isOpen
  if (isOpen) requestAnimationFrame(() => $('.main-nav a', sidebar).focus())
  else if (restoreFocus && mobile) menuButton.focus()
}

function renderMenuLabel(): void {
  menuButton.setAttribute('aria-label', sidebar.classList.contains('is-open') ? t().nav.close : t().nav.open)
}

menuButton.addEventListener('click', () => setMenu(!sidebar.classList.contains('is-open'), true))
backdrop.addEventListener('click', () => setMenu(false, true))
sidebar.addEventListener('click', (event) => {
  if (event.target instanceof Element && event.target.closest('a')) setMenu(false)
})
document.addEventListener('keydown', (event) => {
  if (!sidebar.classList.contains('is-open')) return
  if (event.key === 'Escape') setMenu(false, true)
  if (event.key === 'Tab') {
    // Keep focus inside the open off-canvas navigation.
    const focusables = [menuButton, ...$$('a[href], button', sidebar)]
    const first = focusables[0]
    const last = focusables[focusables.length - 1]
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault()
      last.focus()
    }
    if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault()
      first.focus()
    }
  }
})

// Moves focus to the content without changing the hash, which holds the route.
$('.skip-link').addEventListener('click', (event) => {
  event.preventDefault()
  main.focus()
})

function showToast(message: string): void {
  window.clearTimeout(toastTimer)
  toast.textContent = message
  toast.hidden = false
  toastTimer = window.setTimeout(() => {
    toast.hidden = true
  }, 3200)
}

async function copyText(text: string, trigger: HTMLElement): Promise<void> {
  let copied = false
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text)
      copied = true
    }
  } catch {
    // file:// and browser policies can reject the Clipboard API.
  }
  if (!copied) {
    const input = document.createElement('textarea')
    input.value = text
    input.setAttribute('readonly', '')
    input.style.cssText = 'position:fixed;left:-9999px;top:0'
    document.body.append(input)
    input.select()
    try {
      copied = document.execCommand('copy')
    } catch {
      copied = false
    }
    input.remove()
    trigger.focus({ preventScroll: true })
  }
  showToast(copied ? t().copy.done : t().copy.failed)
}

function showDialog(dialog: HTMLDialogElement): void {
  for (const openDialog of $$<HTMLDialogElement>('.modal[open]')) {
    if (openDialog !== dialog) openDialog.close()
  }
  if (!dialog.open) dialog.showModal()
  document.body.classList.add('dialog-open')
}

for (const dialog of $$<HTMLDialogElement>('.modal')) {
  dialog.addEventListener('close', () => {
    if (!document.querySelector('.modal[open]')) document.body.classList.remove('dialog-open')
  })
  // A click on the backdrop targets the dialog itself, outside its box.
  dialog.addEventListener('click', (event) => {
    if (event.target !== dialog) return
    const bounds = dialog.getBoundingClientRect()
    if (
      event.clientX < bounds.left ||
      event.clientX > bounds.right ||
      event.clientY < bounds.top ||
      event.clientY > bounds.bottom
    ) {
      dialog.close()
    }
  })
}

function renderImageFallback(holder: HTMLElement): void {
  const state = imageStates.get(holder)
  if (!state) return
  const fallback = $('.image-fallback', holder)
  $('strong', fallback).textContent = state === 'error' ? t().screens.unavailable : t().screens.loading
  $('p', fallback).hidden = state !== 'error'
}

function loadOriginalImage(image: HTMLImageElement, holder: HTMLElement, key: ScreenKey): void {
  const fallback = $('.image-fallback', holder)
  // The image stays hidden until its load event. Lazy loading would wait for visibility under
  // display:none and never start. The source is still set only when the view opens.
  image.loading = 'eager'
  image.hidden = true
  fallback.hidden = false
  imageStates.set(holder, 'loading')
  renderImageFallback(holder)
  image.alt = t().screens[key].alt
  image.dataset.request = key
  image.onload = () => {
    if (image.dataset.request !== key) return
    image.hidden = false
    fallback.hidden = true
    imageStates.set(holder, 'loaded')
  }
  image.onerror = () => {
    if (image.dataset.request !== key) return
    image.hidden = true
    fallback.hidden = false
    imageStates.set(holder, 'error')
    renderImageFallback(holder)
  }
  image.src = SCREENSHOTS[key].src
}

function sourceUrl(key: ScreenKey): string {
  return `${REPO}/blob/main/docs/images/${SCREENSHOTS[key].file}`
}

function renderGalleryCaption(): void {
  $('#gallery-caption').textContent = format(t().gallery.caption, { title: t().screens[currentGalleryTab].title })
}

function setGallery(key: ScreenKey): void {
  currentGalleryTab = key
  for (const button of $$('[data-gallery-tab]')) {
    const active = button.dataset.galleryTab === key
    button.classList.toggle('is-active', active)
    button.setAttribute('aria-pressed', String(active))
  }
  renderGalleryCaption()
  $<HTMLAnchorElement>('#gallery-source-link').href = sourceUrl(key)
  $<HTMLAnchorElement>('#gallery-fallback-link').href = sourceUrl(key)
  // The bundled file, not GitHub: opening it shows the screenshot at its full 2544px, where the
  // UI text in it stays readable.
  $<HTMLAnchorElement>('#gallery-full-size-link').href = SCREENSHOTS[key].src
  loadOriginalImage(galleryImage, $('#gallery-image-holder'), key)
}

function setProductTab(key: ScreenKey): void {
  currentProductTab = key
  for (const button of $$('[data-product-tab]')) {
    const active = button.dataset.productTab === key
    button.classList.toggle('is-active', active)
    button.setAttribute('aria-pressed', String(active))
  }
  $('#product-screenshot-title').textContent = t().screens[key].title
  $<HTMLAnchorElement>('#product-image-link').href = sourceUrl(key)
  loadOriginalImage(productImage, $('#product-image-holder'), key)
}

function renderScreenshotTexts(): void {
  const screens = t().screens
  $('#product-screenshot-title').textContent = screens[currentProductTab].title
  for (const image of [productImage, galleryImage]) {
    const request = image.dataset.request
    if (isScreenKey(request)) image.alt = screens[request].alt
  }
  renderGalleryCaption()
  for (const holder of imageStates.keys()) renderImageFallback(holder)
}

document.addEventListener('click', (event) => {
  if (!(event.target instanceof Element)) return
  const target = event.target
  if (target.closest('[data-download]')) {
    event.preventDefault()
    showDialog(downloadDialog)
    return
  }
  const close = target.closest('[data-close]')
  if (close) {
    close.closest('dialog')?.close()
    return
  }
  const copy = target.closest<HTMLElement>('[data-copy]')
  if (copy) {
    void copyText(copy.dataset.copy ?? '', copy)
    return
  }
  const gallery = target.closest<HTMLElement>('[data-gallery]')
  if (gallery) {
    const key = gallery.dataset.gallery
    if (isScreenKey(key)) setGallery(key)
    showDialog(galleryDialog)
    return
  }
  const galleryTab = target.closest<HTMLElement>('[data-gallery-tab]')
  if (galleryTab) {
    const key = galleryTab.dataset.galleryTab
    if (isScreenKey(key)) setGallery(key)
    return
  }
  const productTab = target.closest<HTMLElement>('[data-product-tab]')
  if (productTab) {
    const key = productTab.dataset.productTab
    if (isScreenKey(key)) setProductTab(key)
    return
  }
  const language = target.closest<HTMLElement>('[data-lang]')
  if (language) {
    setLanguage(language.dataset.lang)
    return
  }
  const node = target.closest<SVGGElement>('[data-node]')
  if (node) selectNode(node.dataset.node)
})
// The screenshot itself opens the gallery too; the button stays for keyboard users.
for (const opener of [$('#product-enlarge'), $('#product-image')]) {
  opener.addEventListener('click', () => {
    setGallery(currentProductTab)
    showDialog(galleryDialog)
  })
}

function renderInspector(): void {
  const data = t().demo.nodes[selectedNode]
  $('#inspector-type').textContent = data.type
  $('#inspector-name').textContent = data.name
  $('#inspector-detail').textContent = data.detail
  $('#inspector-code').textContent = data.code
}

function selectNode(id: string | undefined, announce = false): void {
  if (!isNodeKey(id)) return
  selectedNode = id
  for (const node of $$<SVGGElement>('[data-node]')) {
    const selected = node.dataset.node === id
    node.classList.toggle('is-selected', selected)
    node.setAttribute('aria-pressed', String(selected))
  }
  renderInspector()
  if (announce) {
    const data = t().demo.nodes[id]
    screenreaderStatus.textContent = `${data.name}: ${data.detail}`
  }
}

for (const node of $$<SVGGElement>('[data-node]')) {
  node.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      selectNode(node.dataset.node, true)
    }
    if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
      event.preventDefault()
      const direction = event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 1
      const index = NODE_KEYS.findIndex((key) => key === node.dataset.node)
      const next = NODE_KEYS[(index + direction + NODE_KEYS.length) % NODE_KEYS.length]
      $<SVGGElement>('#node-' + next).focus()
      selectNode(next, true)
    }
  })
}

/** Frame and border widths of the designer's layered node shapes, in canvas units. */
const ENTRY_FRAME = 3
const BORDER = 2
const ICON_SCALE = 0.42

function setAttributes(element: Element | null, values: Record<string, string | number>): void {
  if (!element) return
  for (const [name, value] of Object.entries(values)) element.setAttribute(name, String(value))
}

function renderNode(key: NodeKey, layout: GraphLayout): void {
  const { x, y, box } = layout.nodes[key]
  const shape = NODE_SHAPE[key]
  const node = $<SVGGElement>('#node-' + key)
  const points = (selector: string, inset: number) =>
    setAttributes(node.querySelector(selector), { points: shapePoints(shape, box, inset) })
  const frame = node.querySelector('.node-entry') ? ENTRY_FRAME : 0

  node.setAttribute('transform', `translate(${x} ${y})`)
  setAttributes(node.querySelector('.node-hit'), { x: -56, y: -box / 2 - 10, width: 112, height: box + 52 })
  points('.node-ping', 0)
  points('.selection-ring', -3)
  points('.node-entry', 0)
  points('.node-border', frame)
  points('.node-fill', frame + BORDER)
  points('.node-sheen', frame + BORDER)

  const iconSize = box * ICON_SCALE
  const [offsetX, offsetY] = ICON_OFFSET[shape]
  setAttributes(node.querySelector('.node-icon'), {
    x: -iconSize / 2 + offsetX * box,
    y: -iconSize / 2 + offsetY * box,
    width: iconSize,
    height: iconSize,
  })
  node.querySelector('.entry-badge')?.setAttribute('transform', `translate(0 ${-box / 2})`)
  setAttributes(node.querySelector('.node-label'), { x: 0, y: box / 2 + 17 })
  $$<SVGCircleElement>('.node-health circle', node).forEach((dot, index) =>
    setAttributes(dot, { cx: (index - 1.5) * 5, cy: box / 2 + 28 }),
  )
}

/** Sizes the label pills to their text. Needs the page visible and the fonts loaded. */
function layoutEdgeLabels(): void {
  for (const [id, from, to] of EDGES) {
    const edge = $<SVGGElement>('#edge-' + id)
    const text = $<SVGTextElement>('.edge-label text', edge)
    const width = text.getComputedTextLength()
    if (!width) continue
    const { labelX, labelY } = layoutEdge(graphLayout, from, to)
    setAttributes(text, { x: labelX, y: labelY })
    setAttributes($('.edge-label rect', edge), { x: labelX - width / 2 - 6, y: labelY - 8, width: width + 12, height: 16 })
  }
}

// Narrow canvases get a taller layout so the node labels stay readable.
function renderGraph(): void {
  const width = $('.flow-canvas').clientWidth
  if (!width) return
  graphLayout = width < 430 ? LAYOUTS.compact : LAYOUTS.wide
  const layout = graphLayout
  $<SVGSVGElement>('#workflow-svg').setAttribute('viewBox', `0 0 ${layout.width} ${layout.height}`)
  for (const key of NODE_KEYS) renderNode(key, layout)

  for (const [id, from, to] of EDGES) {
    const edge = $<SVGGElement>('#edge-' + id)
    const geometry = layoutEdge(layout, from, to)
    for (const path of $$<SVGPathElement>('path', edge)) path.setAttribute('d', geometry.path)
    $<SVGPolygonElement>('.edge-arrow', edge).setAttribute('points', geometry.arrow)
    setAttributes($('.edge-label text', edge), { x: geometry.labelX, y: geometry.labelY })
  }
  layoutEdgeLabels()

  const minimap = $<SVGGElement>('.canvas-minimap')
  minimap.style.display = layout.minimap ? '' : 'none'
  if (layout.minimap) {
    const { x, y, width: mapWidth, height: mapHeight } = layout.minimap
    setAttributes($('.minimap-frame', minimap), { x, y, width: mapWidth, height: mapHeight })
    setAttributes($('.minimap-view', minimap), { x: x + 3, y: y + 3, width: mapWidth - 6, height: mapHeight - 6 })
    for (const key of NODE_KEYS) {
      const node = layout.nodes[key]
      setAttributes($(`[data-mini="${key}"]`, minimap), {
        x: x + (node.x / layout.width) * mapWidth - 3,
        y: y + (node.y / layout.height) * mapHeight - 3,
        width: 6,
        height: 6,
      })
    }
  }
}

const graphObserver = new ResizeObserver(renderGraph)
graphObserver.observe($('.flow-canvas'))
window.addEventListener('resize', () => {
  if (window.innerWidth > 760) setMenu(false)
  else sidebar.inert = !sidebar.classList.contains('is-open')
})

function renderDemo(): void {
  const demo = t().demo
  $('span', playButton).textContent = demoPaused ? demo.resume : demo.pause
  playButton.setAttribute('aria-label', demoPaused ? demo.resumeLabel : demo.pauseLabel)
  playButton.classList.toggle('is-paused', demoPaused)
  $('#simulation-label').textContent = demoRun ? demo.running : demo.idle
}

function resetDemoClasses(): void {
  for (const node of $$<SVGGElement>('.flow-node')) node.classList.remove('is-running', 'is-success', 'is-skipped')
  for (const edge of $$<SVGGElement>('.edge')) edge.classList.remove('is-running', 'is-done', 'is-skipped')
}

/** Starts or stops the loop to match pause state, visibility and the current page. */
function syncDemo(): void {
  const shouldRun =
    !demoPaused && demoOnScreen && document.visibilityState === 'visible' && currentRoute?.page === 'home'
  if (shouldRun && !demoRun) void runDemoLoop()
  if (!shouldRun && demoRun) {
    demoRun.abort()
    demoRun = null
    resetDemoClasses()
  }
  renderDemo()
}

function wait(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      reject(new DOMException('Aborted', 'AbortError'))
      return
    }
    const onAbort = () => {
      window.clearTimeout(timer)
      reject(new DOMException('Aborted', 'AbortError'))
    }
    const timer = window.setTimeout(() => {
      signal.removeEventListener('abort', onAbort)
      resolve()
    }, ms)
    signal.addEventListener('abort', onAbort, { once: true })
  })
}

// Replays the success path the way the designer shows a live run: the running step pulses in
// amber, finished steps and edges turn green, the unused branch is skipped. Nothing connects to
// a system.
async function runDemoLoop(): Promise<void> {
  const controller = new AbortController()
  demoRun = controller
  const { signal } = controller
  const node = (key: NodeKey) => $<SVGGElement>('#node-' + key).classList
  const edge = (id: (typeof EDGES)[number][0]) => $<SVGGElement>('#edge-' + id).classList
  try {
    for (;;) {
      resetDemoClasses()
      await wait(900, signal)
      node('start').add('is-running')
      await wait(700, signal)
      node('start').replace('is-running', 'is-success')
      edge('start').add('is-running')
      node('script').add('is-running')
      await wait(1300, signal)
      edge('start').replace('is-running', 'is-done')
      node('script').replace('is-running', 'is-success')
      edge('check').add('is-running')
      node('check').add('is-running')
      await wait(1300, signal)
      edge('check').replace('is-running', 'is-done')
      node('check').replace('is-running', 'is-success')
      edge('result').add('is-running')
      node('result').add('is-running')
      edge('error').add('is-skipped')
      node('error').add('is-skipped')
      await wait(900, signal)
      edge('result').replace('is-running', 'is-done')
      node('result').replace('is-running', 'is-success')
      await wait(2600, signal)
    }
  } catch (error) {
    if (!(error instanceof DOMException && error.name === 'AbortError')) throw error
  } finally {
    if (demoRun === controller) demoRun = null
  }
}

playButton.addEventListener('click', () => {
  demoPaused = !demoPaused
  syncDemo()
})
new IntersectionObserver((entries) => {
  demoOnScreen = entries.some((entry) => entry.isIntersecting)
  syncDemo()
}, { threshold: 0.2 }).observe($('#workflow-stage'))
document.addEventListener('visibilitychange', syncDemo)
reducedMotion.addEventListener('change', () => {
  if (reducedMotion.matches) demoPaused = true
  syncDemo()
})
void document.fonts.ready.then(layoutEdgeLabels)

function applyBlogFilter(announce = true): void {
  const lang = currentLang()
  const term = blogSearch.value.trim().toLocaleLowerCase(lang)
  let count = 0
  for (const row of $$('.blog-index-row')) {
    const matchCategory = currentFilter === 'all' || row.dataset.category === currentFilter
    const matchText = `${row.dataset.search ?? ''} ${row.textContent ?? ''}`.toLocaleLowerCase(lang).includes(term)
    row.hidden = !(matchCategory && matchText)
    if (!row.hidden) count++
  }
  $('#empty-search').hidden = count > 0
  if (announce) {
    screenreaderStatus.textContent = count === 1 ? t().blog.foundOne : format(t().blog.foundMany, { count })
  }
}

function setFilter(filter: string): void {
  currentFilter = filter
  for (const button of $$('[data-filter]')) {
    const active = button.dataset.filter === filter
    button.classList.toggle('is-active', active)
    button.setAttribute('aria-pressed', String(active))
  }
  applyBlogFilter()
}

for (const button of $$('[data-filter]')) {
  button.addEventListener('click', () => setFilter(button.dataset.filter ?? 'all'))
}
blogSearch.addEventListener('input', () => applyBlogFilter())
$('#reset-search').addEventListener('click', () => {
  blogSearch.value = ''
  setFilter('all')
  blogSearch.focus()
})

/** Texts that depend on the route and the language. */
function renderRouteTexts(route: SiteRoute): void {
  const messages = t()
  $('#header-current').textContent = messages.pages[route.page]
  // The prerendered file carries this route's description; applyLanguage would put the site-wide
  // one back, so it is set again here for whatever a crawler reads after the script has run.
  const description =
    route.page === 'article' ? messages.articles[route.slug].summary : messages.meta.descriptions[route.page]
  document.querySelector('meta[name="description"]')?.setAttribute('content', description)
  if (route.page === 'article') {
    const article = messages.articles[route.slug]
    $('#article-category').textContent = article.category
    $('#article-title').textContent = article.title
    $('#article-lead').textContent = article.lead
    // Only author-written article HTML from the dictionaries is inserted here.
    $('#article-body').innerHTML = article.body
    document.title = `${article.title} — NodePilot Blog`
  } else {
    document.title = `${messages.titles[route.page]} — NodePilot`
  }
}

/** Replays the ten-second invitation on the live-demo link whenever the home page is
    shown again; an animation only starts once per element on its own. */
function restartDemoInvite(): void {
  const link = $('.hero-demo-link')
  link.classList.remove('is-inviting')
  void link.offsetWidth
  link.classList.add('is-inviting')
}

function showRoute(next: SiteRoute, initial: boolean): void {
  const previous = currentRoute?.page
  for (const page of PAGES) $('#' + page + '-page').hidden = page !== next.page
  for (const link of $$('[data-nav]')) {
    const active = link.dataset.nav === (next.page === 'article' ? 'blog' : next.page)
    link.classList.toggle('is-active', active)
    if (active) link.setAttribute('aria-current', 'page')
    else link.removeAttribute('aria-current')
  }
  if (next.page !== 'experience') { experience?.dispose(); experience = undefined }
  else if (!experience) experience = mountExperience($('#experience-root'))
  currentRoute = next
  syncDemo()
  renderRouteTexts(next)
  if (next.page === 'product' && previous !== 'product') setProductTab(currentProductTab)
  if (next.page === 'home' && previous !== 'home') restartDemoInvite()
  setMenu(false)
  if (!initial) {
    window.scrollTo({ top: 0, behavior: 'instant' })
    const focusTarget = next.page === 'article' ? $('#article-title') : main
    focusTarget.focus({ preventScroll: true })
  }
  requestAnimationFrame(renderGraph)
}

/**
 * Where the site root is, relative to the current document. Each prerendered route lives in its
 * own directory, so `/product/` has to reach one level up for links and assets.
 */
const basePrefix = $<HTMLMetaElement>('meta[name="np-site-base"]').content || './'
const basePath = new URL(basePrefix, location.href).pathname

/** The current address as a path relative to the site root. */
function currentPath(): string {
  const path = location.pathname
  return path.startsWith(basePath) ? path.slice(basePath.length) : path.replace(/^\/+/, '')
}

function route(initial = false): void {
  showRoute(resolveRoute(currentPath()), initial)
}

/** Follows an internal link without reloading, and keeps the address bar honest. */
function navigate(href: string): void {
  history.pushState(null, '', href)
  route()
}

function setLanguage(value: string | undefined): void {
  if (!isLang(value) || value === currentLang()) return
  persistLang(value)
  applyLanguage(value)
}

function renderLanguageState(): void {
  const lang = currentLang()
  for (const button of $$('[data-lang]')) button.setAttribute('aria-pressed', String(button.dataset.lang === lang))
  for (const note of $$('.legal-language-note')) note.hidden = lang === 'de'
}

onLanguageChange(() => {
  experience?.updateLanguage()
  renderLanguageState()
  labelNavItems()
  renderMenuLabel()
  renderInspector()
  renderDemo()
  layoutEdgeLabels()
  renderScreenshotTexts()
  applyBlogFilter(false)
  if (currentRoute) renderRouteTexts(currentRoute)
})

window.addEventListener('popstate', () => route())
// Internal links stay in the page. Anything that leaves the site -- the docs, the demo, GitHub,
// a download, a new tab or a modified click -- is left to the browser.
document.addEventListener('click', (event) => {
  if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
  if (!(event.target instanceof Element)) return
  const link = event.target.closest('a')
  if (!link || link.target || link.hasAttribute('download') || !link.href) return

  const url = new URL(link.href)
  if (url.origin !== location.origin || url.hash || !url.pathname.startsWith(basePath)) return
  const next = resolveRoute(url.pathname.slice(basePath.length))
  if (next.page === 'notfound') return

  event.preventDefault()
  if (url.pathname === location.pathname) {
    window.scrollTo({ top: 0, behavior: reducedMotion.matches ? 'instant' : 'smooth' })
    return
  }
  navigate(link.href)
})
window.addEventListener('pageshow', () => {
  if (currentRoute?.page === 'experience' && !experience) experience = mountExperience($('#experience-root'))
})
window.addEventListener('pagehide', () => {
  experience?.dispose()
  experience = undefined
  demoRun?.abort()
  window.clearTimeout(toastTimer)
})

setBasePrefix(basePrefix)
applyLanguage(detectLang())
route(true)
selectNode(selectedNode)
