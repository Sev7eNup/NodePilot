# Minimal skin preview (throwaway)

Question: do flat neutral surfaces, restrained blue accents and clear semantic states make
NodePilot feel simpler while retaining its familiar typography and layout?

Run `npm run dev` from `src/nodepilot-ui`, then open:

- `http://localhost:5173/skin-preview.html?variant=light`
- `http://localhost:5173/skin-preview.html?variant=dark&view=designer`

The separate HTML entry is served by Vite in development and is not an input to the regular
production build. It does not initialize the real application, authentication, telemetry or
persisted stores. All sample changes live in memory. German and English are available without
using the application's persisted language setting.

Try the view/theme switches, workflow search and status filter, sample form, create dialog,
node selection, pan/zoom, and the execution-state selector. The designer is a small sample,
not a second workflow editor. Layout and sizes follow the application's existing conventions;
the new palette and flat decoration are the decision under review.

Decision: the flat neutral palette is now implemented in the application as `light-minimal`
and `dark-minimal`, with shared shell/designer tokens, accessible code colours and clearer
canvas dots. The isolated preview remains available for comparison during final acceptance;
it is not a second maintained application UI.
