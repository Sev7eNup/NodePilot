---
paths:
  - "src/nodepilot-ui/**/*.ts"
  - "src/nodepilot-ui/**/*.tsx"
  - "src/nodepilot-docs-ui/**/*.ts"
  - "src/nodepilot-docs-ui/**/*.tsx"
---

# React / TypeScript

Gilt zusätzlich zur Root-`CLAUDE.md` und zu `src/nodepilot-ui/CLAUDE.md`. `strict` und `no-explicit-any` erzwingen tsconfig und ESLint bereits.

- Keine unsicheren Casts (`as unknown as T`), um einen Typfehler loszuwerden.
- Keinen State speichern, der sich aus Props oder anderem State ableiten lässt.
- Render- oder Event-Logik vor `useEffect`. Die Lint-Regel `react-hooks/set-state-in-effect` ist aus, also gilt das hier als Konvention, nicht als Check.
- Kein `useMemo`/`useCallback` ohne konkreten Grund (Referenzstabilität für eine Abhängigkeit, gemessene Kosten).
- Netzwerkzugriffe über `src/api/` und React Query, nicht direkt in Komponenten.
- Semantische, zugängliche Controls (`<button>`, `<label>`) statt klickbarer `<div>`s.
