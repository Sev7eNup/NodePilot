import { experienceMarkup } from './markup'
import './experience.css'
import { currentLang, sitePath } from '../i18n'

/** Writes the walkthrough in the page language. In the build it is already there; dev needs it. */
export function mountExperience(root: HTMLElement): void {
  root.innerHTML = experienceMarkup(currentLang(), sitePath())
}
