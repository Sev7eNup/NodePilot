import catalog from './videos.json' with { type: 'json' }
import type { Lang } from '../i18n/languages'

/** One language version of a training episode. Paths are relative to the site root. */
export interface VideoText {
  title: string
  video: string
  poster: string
  bytes: number
  /** Whole seconds. */
  duration: number
  youtube?: string
}

export interface VideoEpisode {
  number: number
  slug: string
  publishedAt: string
  text: Record<Lang, VideoText>
}

/** Written by scripts/site-videos.mjs; only publishedAt and youtube are edited by hand. */
export const videoEpisodes: readonly VideoEpisode[] = (catalog as { episodes: VideoEpisode[] }).episodes

export function formatDuration(seconds: number): string {
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}
