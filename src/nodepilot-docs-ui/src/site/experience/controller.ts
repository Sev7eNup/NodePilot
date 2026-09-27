import { experienceMarkup } from './markup'
import './experience.css'
import { currentLang, sitePath, t } from '../i18n'

export interface ExperienceController { updateLanguage(): void; dispose(): void }

export function mountExperience(root: HTMLElement): ExperienceController {
  root.innerHTML = experienceMarkup(currentLang(), sitePath())
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
