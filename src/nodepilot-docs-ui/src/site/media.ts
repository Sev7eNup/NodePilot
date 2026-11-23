import manifest from './image-manifest.json'

export type ImageKey = keyof typeof manifest
export function imageAttributes(key: ImageKey, base: string, fullSize = false) {
  const image = manifest[key]
  const url = (width: number) => `${base}site-images/${key}-${width}.webp`
  return {
    src: url(fullSize ? image.width : image.widths.find(width => width >= 1600) ?? image.width),
    srcset: image.widths.map(width => `${url(width)} ${width}w`).join(', '),
    // Follows the layout breakpoints: no rail, icon rail (70px), narrow rail (184px), full rail (212px).
    sizes: '(max-width: 760px) calc(100vw - 40px), (max-width: 1060px) calc(100vw - 130px), (max-width: 1270px) calc(100vw - 280px), 1000px',
    width: String(image.width), height: String(image.height), loading: 'lazy', decoding: 'async',
  }
}

export function configureImage(image: Element, key: ImageKey, base: string): void {
  image.setAttribute('data-media', key)
  for (const [name, value] of Object.entries(imageAttributes(key, base))) image.setAttribute(name, value)
}
