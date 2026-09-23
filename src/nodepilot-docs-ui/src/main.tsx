import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import App from './App'
import { docsBase } from './lib/docsBase'
import './i18n'
import './index.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter basename={docsBase()}>
      <App />
    </BrowserRouter>
  </StrictMode>,
)
