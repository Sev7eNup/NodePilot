import { messages } from '../i18n'
import type { Lang } from '../../i18n/languages'
import { additionalMissions } from './missions'

export function experienceMarkup(lang: Lang, base: string): string {
  const copy = messages[lang].experience
  const card = (mission: { id: string; title: string; text: string; steps: readonly string[]; duration: string }, number: number) => `<article class="experience-mission" data-mission="${mission.id}">
    <span class="experience-mission-number" aria-hidden="true">${String(number).padStart(2, '0')}</span>
    <div class="section-kicker">${mission.duration}</div><h2>${mission.title}</h2><p>${mission.text}</p>
    <ol>${mission.steps.map(step => `<li>${step}</li>`).join('')}</ol>
    <a class="button ${number === 1 ? 'button-primary' : 'button-secondary'}" data-tour="${mission.id}">${copy.moreAction}</a>
  </article>`
  const build = additionalMissions[lang].find(mission => mission.id === 'build')!
  const extra = additionalMissions[lang].filter(mission => mission.id !== 'build').map((mission, index) => card(mission, index + 4)).join('')
  return `<div class="experience-missions">
    ${card(build, 1)}
    <article class="experience-mission" data-mission="diagnose"><span class="experience-mission-number" aria-hidden="true">02</span><div class="section-kicker" data-experience-text="secondKicker"></div><h2 data-experience-text="secondTitle"></h2><p data-experience-text="secondText"></p><blockquote><code>File Copy → Access denied</code><span data-experience-text="clue"></span></blockquote><a class="button button-secondary" data-tour="diagnose" data-experience-text="secondAction"></a></article>
    <article class="experience-mission" data-mission="file"><span class="experience-mission-number" aria-hidden="true">03</span><div class="section-kicker" data-experience-text="firstKicker"></div><h2 data-experience-text="firstTitle"></h2><p data-experience-text="firstText"></p><ol><li data-experience-text="firstStep"></li><li data-experience-text="secondStep"></li><li data-experience-text="thirdStep"></li></ol><a class="button button-secondary" data-tour="file" data-experience-text="firstAction"></a></article>
    ${extra}
  </div><p class="experience-notice" data-experience-text="notice"></p>`.replace(/data-experience-text="([^"]+)"><\//g, (_match, key: keyof typeof copy) => `data-experience-text="${key}">${copy[key]}</`)
    .replace(/data-tour="([^"]+)"/g, (_match, tour: string) => `data-tour="${tour}" href="${base}demo/?tour=${tour}&amp;lang=${lang}"`)
}
