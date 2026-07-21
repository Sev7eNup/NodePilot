# Easter Eggs

## 1. Konami Code → SCORCH-Grabstein

**Trigger:** Konami-Sequenz auf der Tastatur eingeben:  
`↑ ↑ ↓ ↓ ← → ← → B A`

**Effekt:** Vollbild-Overlay im Stil einer deutschen Zeitungs-Todesanzeige — weißes Karten-Layout mit Doppelrahmen, SVG-Grabstein, flankierenden Kerzen mit Flacker-Animation und dunklem Humor auf Kosten von SCORCH.

**Schließen:** Klick oder `ESC`

**Verfügbar:** überall in der App (auch im Workflow-Editor)

**Implementierung:** [`AppLayout.tsx`](../src/nodepilot-ui/src/components/layout/AppLayout.tsx), [`ScorchEasterEgg.tsx`](../src/nodepilot-ui/src/components/easter-eggs/ScorchEasterEgg.tsx)

---

## 2. Klaus Kinski — Output-Tab

**Trigger:** Im Workflow-Editor den **Output-Tab** im Execution-Panel **10× hintereinander anklicken**

**Effekt:** Vollbild-Overlay mit dem Kinski-Meme (`/kinskimeme.jpg`) und folgenden Effekten:

| Effekt | Details |
|---|---|
| Roter Flash | Hintergrund leuchtet kurz rot auf, beruhigt sich zu Schwarz |
| Slam-Zoom | Bild erscheint riesig (scale 2.4, −5° Rotation) und slamt auf Normalgröße |
| Film-Grain | SVG-feTurbulence-Noise-Overlay mit Flicker-Animation |
| Vignette | Radiales Gradient-Overlay dunkelt die Ecken ab |
| Bildfilter | `contrast(1.12) saturate(0.88)` für einen leicht verwitterten Look |

**Schließen:** Klick oder `ESC`

**Verfügbar:** nur im Workflow-Editor (Execution-Panel sichtbar)

**Implementierung:** [`ExecutionPanel.tsx`](../src/nodepilot-ui/src/components/designer/ExecutionPanel.tsx), [`KinskiEasterEgg.tsx`](../src/nodepilot-ui/src/components/easter-eggs/KinskiEasterEgg.tsx)
