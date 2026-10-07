import manifest from './image-manifest.json'

export type ImageKey = keyof typeof manifest
export function imageAttributes(key: ImageKey, base: string, fullSize = false) {
  const image = manifest[key]
  const url = (width: number) => `${base}site-images/${key}-${width}.webp`
  return {
    src: url(fullSize ? image.width : image.widths.find(width => width >= 1600) ?? image.width),
    srcset: image.widths.map(width => `${url(width)} ${width}w`).join(', '),
    sizes: '(max-width: 700px) calc(100vw - 40px), (max-width: 1200px) calc(100vw - 300px), 1000px',
    width: String(image.width), height: String(image.height), loading: 'lazy', decoding: 'async',
  }
}

export function configureImage(image: Element, key: ImageKey, base: string): void {
  image.setAttribute('data-media', key)
  for (const [name, value] of Object.entries(imageAttributes(key, base))) image.setAttribute(name, value)
}
