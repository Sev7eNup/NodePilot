import './experience.css'
import { currentLang, sitePath, t } from '../i18n'

export interface ExperienceController { updateLanguage(): void; dispose(): void }

export function mountExperience(root: HTMLElement): ExperienceController {
  root.innerHTML = `<div class="experience-missions">
    <article class="experience-mission"><span class="experience-mission-number" aria-hidden="true">01</span><div class="section-kicker" data-experience-text="firstKicker"></div><h2 data-experience-text="firstTitle"></h2><p data-experience-text="firstText"></p><ol><li data-experience-text="firstStep"></li><li data-experience-text="secondStep"></li><li data-experience-text="thirdStep"></li></ol><a class="button button-primary" data-tour="file" data-experience-text="firstAction"></a></article>
    <article class="experience-mission"><span class="experience-mission-number" aria-hidden="true">02</span><div class="section-kicker" data-experience-text="secondKicker"></div><h2 data-experience-text="secondTitle"></h2><p data-experience-text="secondText"></p><blockquote><code>File Copy → Access denied</code><span data-experience-text="clue"></span></blockquote><a class="button button-secondary" data-tour="diagnose" data-experience-text="secondAction"></a></article>
  </div><p class="experience-notice" data-experience-text="notice"></p>`
  function render() {
    const copy = t().experience
    for (const element of root.querySelectorAll<HTMLElement>('[data-experience-text]')) {
      element.textContent = copy[element.dataset.experienceText as keyof typeof copy]
    }
    for (const link of root.querySelectorAll<HTMLAnchorElement>('[data-tour]')) link.href = sitePath(`demo/?tour=${link.dataset.tour}&lang=${currentLang()}`)
  }
  render()
  return { updateLanguage: render, dispose() { root.replaceChildren() } }
}
