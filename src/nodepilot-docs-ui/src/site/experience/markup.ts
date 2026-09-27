import { messages } from '../i18n'
import type { Lang } from '../../i18n/languages'

export function experienceMarkup(lang: Lang, base: string): string {
  const copy = messages[lang].experience
  return `<div class="experience-missions">
    <article class="experience-mission"><span class="experience-mission-number" aria-hidden="true">01</span><div class="section-kicker" data-experience-text="firstKicker"></div><h2 data-experience-text="firstTitle"></h2><p data-experience-text="firstText"></p><ol><li data-experience-text="firstStep"></li><li data-experience-text="secondStep"></li><li data-experience-text="thirdStep"></li></ol><a class="button button-primary" data-tour="file" data-experience-text="firstAction"></a></article>
    <article class="experience-mission"><span class="experience-mission-number" aria-hidden="true">02</span><div class="section-kicker" data-experience-text="secondKicker"></div><h2 data-experience-text="secondTitle"></h2><p data-experience-text="secondText"></p><blockquote><code>File Copy → Access denied</code><span data-experience-text="clue"></span></blockquote><a class="button button-secondary" data-tour="diagnose" data-experience-text="secondAction"></a></article>
  </div><p class="experience-notice" data-experience-text="notice"></p>`.replace(/data-experience-text="([^"]+)"><\//g, (_match, key: keyof typeof copy) => `data-experience-text="${key}">${copy[key]}</`)
    .replace(/data-tour="([^"]+)"/g, (_match, tour: string) => `data-tour="${tour}" href="${base}demo/?tour=${tour}&amp;lang=${lang}"`)
}
