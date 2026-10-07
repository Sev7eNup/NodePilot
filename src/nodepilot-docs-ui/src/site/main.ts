import { configureImage, imageAttributes } from './media'
import { articleBySlug, isBlogPreview } from './blog'
/* NodePilot project website. No backend, analytics or real workflow execution: every
   interaction stays in the page, apart from the links a visitor opens. */
import { mountExperience } from './experience/controller'
import appIconUrl from './images/logo-dark.webp'
import { isLang } from '../i18n/languages'
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
import {
  BOX_KEYS,
  LAYOUTS as TOPOLOGY_LAYOUTS,
  LINKS,
  LINK_KEYS,
  SERVICE_PARTS,
  TRIGGER_KEYS,
  chipRect,
  linkGeometry,
  machineRect,
  targetsNoteY,
  triggerRowY,
} from './topology'
import {
  applyLanguage,
  currentLang,
  format,
  persistLang,
  setBasePrefix,
  t,
} from './i18n'
import { resolveRoute, routeLanguage } from './router'
import { renderSiteContent, renderSiteHead } from './seo'

const REPO = 'https://github.com/Sev7eNup/NodePilot'
const SCREENSHOTS = {
  designer: { file: 'designer-dark.png' },
  logs: { file: 'log-dark.png' },
  ai: { file: 'ai-dark.png' },
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
const videoDialog = $<HTMLDialogElement>('#video-dialog')
const videoPlayer = $<HTMLVideoElement>('#video-player')
const sidebar = $('#sidebar')
const menuButton = $<HTMLButtonElement>('#menu-button')
const backdrop = $<HTMLButtonElement>('#nav-backdrop')
const main = $('#main-content')
const toast = $('#toast')
const screenreaderStatus = $('#screenreader-status')
const galleryImage = $<HTMLImageElement>('#gallery-image')
// Each page file carries only its own section, so these exist on one page each.
const playButton = document.querySelector<HTMLButtonElement>('#play-example')
const blogSearch = document.querySelector<HTMLInputElement>('#blog-search')
const productImage = document.querySelector<HTMLImageElement>('#product-image')

let selectedNode: NodeKey = 'check'
let currentProductTab: ScreenKey = 'designer'
let currentGalleryTab: ScreenKey = 'designer'
// The example runs in a loop while it is on screen, unless the visitor paused it or prefers
// reduced motion.
let demoRun: AbortController | null = null
let demoPaused = reducedMotion.matches
let demoOnScreen = false
let graphLayout: GraphLayout = LAYOUTS.wide
let archBreakpoint: 'wide' | 'compact' = 'wide'
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

// Moves focus to the content without changing the hash.
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

let videoOpener: HTMLElement | null = null

/** Plays a training video from its gallery card in the large dialog. */
function openVideo(card: HTMLAnchorElement): void {
  videoOpener = card
  $('#video-dialog-episode').textContent = card.querySelector('.video-episode')?.textContent ?? ''
  $('#video-dialog-name').textContent = card.querySelector('.video-title')?.textContent ?? ''
  const youtube = $<HTMLAnchorElement>('#video-youtube-link')
  youtube.hidden = !card.dataset.youtube
  youtube.href = card.dataset.youtube ?? ''
  $<HTMLAnchorElement>('#video-error-link').href = card.href
  $('#video-error').hidden = true
  videoPlayer.poster = card.querySelector('img')?.src ?? ''
  videoPlayer.src = card.href
  showDialog(videoDialog)
  // Blocked autoplay leaves the player paused with its controls; a failed load reports through 'error'.
  videoPlayer.play().catch(() => undefined)
}

videoPlayer.addEventListener('error', () => {
  if (videoPlayer.getAttribute('src')) $('#video-error').hidden = false
})
// Stops the download too, not only the sound, and hands focus back to the card.
videoDialog.addEventListener('close', () => {
  videoPlayer.pause()
  videoPlayer.removeAttribute('src')
  videoPlayer.load()
  videoOpener?.focus({ preventScroll: true })
  videoOpener = null
})

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
  configureImage(image, key, basePath)
  image.loading = 'eager'
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
  $<HTMLAnchorElement>('#gallery-full-size-link').href = imageAttributes(key, basePath, true).src
  loadOriginalImage(galleryImage, $('#gallery-image-holder'), key)
}

function setProductTab(key: ScreenKey): void {
  if (!productImage) return
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
  const videoCard = target.closest<HTMLAnchorElement>('a.video-card')
  if (videoCard) {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    openVideo(videoCard)
    return
  }
  const language = target.closest<HTMLAnchorElement>('a[data-lang]')
  if (language) {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    setLanguage(language)
    return
  }
  const node = target.closest<SVGGElement>('[data-node]')
  if (node) selectNode(node.dataset.node)
})
// The screenshot itself opens the gallery too; the button stays for keyboard users.
if (productImage) {
  for (const opener of [$('#product-enlarge'), productImage]) {
    opener.addEventListener('click', () => {
      setGallery(currentProductTab)
      showDialog(galleryDialog)
    })
  }
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

/** Sizes the WinRM and SignalR pills to their text. Needs the fonts loaded. */
function layoutArchLabels(): void {
  for (const link of $$<SVGGElement>('.arch-link')) {
    const text = link.querySelector<SVGTextElement>('.link-label text')
    if (!text) continue
    const width = text.getComputedTextLength()
    if (!width) continue
    const x = Number(text.getAttribute('x'))
    const y = Number(text.getAttribute('y'))
    setAttributes($('.link-label rect', link), { x: x - width / 2 - 6, y: y - 9, width: width + 12, height: 18 })
  }
}

// Narrow canvases stack the architecture boxes instead of placing them side by side.
function renderTopology(): void {
  const canvas = $('#arch-canvas')
  if (!canvas.clientWidth) return
  archBreakpoint = canvas.clientWidth < 620 ? 'compact' : 'wide'
  const layout = TOPOLOGY_LAYOUTS[archBreakpoint]
  const { inset } = layout.metrics
  canvas.classList.toggle('is-compact', archBreakpoint === 'compact')
  $<SVGSVGElement>('#architecture-svg').setAttribute('viewBox', `0 0 ${layout.width} ${layout.height}`)

  for (const key of BOX_KEYS) {
    const group = $<SVGGElement>('#arch-' + key)
    const box = layout.boxes[key]
    setAttributes(group.querySelector('.box-frame'), box)
    setAttributes(group.querySelector('.box-title'), { x: box.x + box.width / 2, y: box.y + box.height / 2 })
    setAttributes(group.querySelector('.box-heading'), { x: box.x + inset + 2, y: box.y + 22 })
  }

  const triggers = $<SVGGElement>('#arch-triggers')
  TRIGGER_KEYS.forEach((key, index) => {
    const row = $<SVGGElement>(`[data-trigger="${key}"]`, triggers)
    const y = triggerRowY(layout, index)
    setAttributes(row.querySelector('circle'), { cx: layout.boxes.triggers.x + inset + 4, cy: y })
    setAttributes(row.querySelector('text'), { x: layout.boxes.triggers.x + inset + 16, y })
  })

  const service = $<SVGGElement>('#arch-service')
  setAttributes($('.box-note', service), { x: layout.boxes.service.x + inset + 2, y: layout.boxes.service.y + 44 })
  SERVICE_PARTS.forEach((part, index) => {
    const chip = $<SVGGElement>(`[data-part="${part}"]`, service)
    const rect = chipRect(layout, index)
    setAttributes(chip.querySelector('rect'), rect)
    setAttributes(chip.querySelector('text'), { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 })
  })

  const targets = $<SVGGElement>('#arch-targets')
  $$<SVGGElement>('.machine', targets).forEach((machine, index) => {
    const rect = machineRect(layout, index)
    setAttributes(machine.querySelector('rect'), rect)
    setAttributes(machine.querySelector('text'), { x: rect.x + 12, y: rect.y + rect.height / 2 })
  })
  setAttributes($('.box-note', targets), { x: layout.boxes.targets.x + inset + 2, y: targetsNoteY(layout) })

  for (const key of LINK_KEYS) {
    const link = $<SVGGElement>('#arch-link-' + key)
    const geometry = linkGeometry(layout, LINKS[key][archBreakpoint])
    for (const path of $$<SVGPathElement>('path', link)) path.setAttribute('d', geometry.path)
    $<SVGPolygonElement>('.link-arrow', link).setAttribute('points', geometry.arrow)
    setAttributes(link.querySelector('.link-label text'), { x: geometry.labelX, y: geometry.labelY })
  }
  layoutArchLabels()
}

window.addEventListener('resize', () => {
  if (window.innerWidth > 760) setMenu(false)
  else sidebar.inert = !sidebar.classList.contains('is-open')
})

function renderDemo(): void {
  if (!playButton) return
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

/** Starts or stops the loop to match pause state and visibility. */
function syncDemo(): void {
  const shouldRun = !demoPaused && demoOnScreen && document.visibilityState === 'visible'
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

if (playButton) {
  new ResizeObserver(renderGraph).observe($('.flow-canvas'))
  new ResizeObserver(renderTopology).observe($('#arch-canvas'))
  // The pulse along the links only runs while the diagram is on screen.
  new IntersectionObserver((entries) => {
    for (const entry of entries) entry.target.classList.toggle('is-visible', entry.isIntersecting)
  }, { threshold: 0.25 }).observe($('.architecture-section'))
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
  void document.fonts.ready.then(layoutArchLabels)
}

function applyBlogFilter(announce = true): void {
  if (!blogSearch) return
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

if (blogSearch) {
  for (const button of $$('[data-filter]')) {
    button.addEventListener('click', () => setFilter(button.dataset.filter ?? 'all'))
  }
  blogSearch.addEventListener('input', () => applyBlogFilter())
  $('#reset-search').addEventListener('click', () => {
    blogSearch.value = ''
    setFilter('all')
    blogSearch.focus()
  })
}

/**
 * Where the site root is, relative to the current document. Each prerendered route lives in its
 * own directory, so `/product/` has to reach one level up for links and assets.
 */
const basePrefix = $<HTMLMetaElement>('meta[name="np-site-base"]').content || './'
const basePath = new URL(basePrefix, location.href).pathname
const siteOrigin = document.querySelector<HTMLMetaElement>('meta[name="np-site-origin"]')?.content || location.origin

/** The current address as a path relative to the site root. */
function currentPath(): string {
  const path = location.pathname
  return path.startsWith(basePath) ? path.slice(basePath.length) : path.replace(/^\/+/, '')
}

/** Opens this page in the chosen language, keeping query and fragment, and remembers the choice. */
function setLanguage(link: HTMLAnchorElement): void {
  const value = link.dataset.lang
  if (!isLang(value) || value === currentLang()) return
  persistLang(value)
  location.assign(`${link.href}${location.search}${location.hash}`)
}

window.addEventListener('pagehide', () => {
  demoRun?.abort()
  window.clearTimeout(toastTimer)
})

setBasePrefix(basePrefix)
applyLanguage(routeLanguage(currentPath()))
let route = resolveRoute(currentPath())
if (route.page === 'article' && !isBlogPreview(document) && articleBySlug[route.slug].status !== 'published') route = { page: 'notfound' }
// An address without a file of its own is answered with the 404 page, which carries only that section.
if (!document.getElementById(`${route.page}-page`)) route = { page: 'notfound' }
renderSiteContent(document, route, currentLang(), basePath)
renderSiteHead(document, route, currentLang(), siteOrigin)
labelNavItems()
setMenu(false)
setProductTab(currentProductTab)
applyBlogFilter(false)
const experienceRoot = document.querySelector<HTMLElement>('#experience-root')
if (experienceRoot) mountExperience(experienceRoot)
if (playButton) selectNode(selectedNode)
