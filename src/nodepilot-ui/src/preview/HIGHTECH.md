# ION dark skin study (throwaway)

Question: can a futuristic control-room aesthetic feel expressive while keeping NodePilot's
forms, workflow states and designer easy to read? One dark concept, as requested.

Run `npm run dev` in `src/nodepilot-ui` and open:

- `http://localhost:5173/hightech-preview.html`
- `http://localhost:5173/hightech-preview.html?view=designer`

The separate Vite entry reuses the existing preview's in-memory interactions. No production
skin, backend, authentication or persistent application state is connected. The normal
production build does not include this HTML entry or its styles. The Minimal preview stays
available at `skin-preview.html` for comparison.

Direction: deep navy glass, cyan light edges, machined panel details and luminous activity
silhouettes. Status colours stay distinct. Existing IBM Plex typography and practical UI
density stay familiar. Light effects can be reduced in the preview bar; reduced motion is
also respected automatically.

Try both views, node selection, pan/zoom, all four execution states, editable properties,
search, dropdowns, dialog creation and keyboard focus. All changes reset on reload.

Decision: approved. The production implementation is the `dark-ion` skin (ION Dark / ION
Dunkel) in the shared theme system. This isolated study is retained as a visual reference,
like the Minimal preview; its demo components and effects toggle are not application UI.
