# NodePilot product tour

Records the actual React frontend with fictional, isolated API fixtures. Produces a silent English product tour, a short GIF and a poster. It does not start workflows, use stored login sessions, contact target machines, or publish anything.

## Capture

Requires the UI's installed npm dependencies, its matching Playwright Chromium, and Node 22.22 or newer. Start the local UI with `npm run dev` from `src/nodepilot-ui` if it is not running. The backend is not needed.

From the repository root:

```powershell
node --experimental-strip-types scripts/product-tour/record.mjs --preview
node --experimental-strip-types scripts/product-tour/record.mjs
python scripts/product-tour/render.py
```

The optional preview checks the click sequence and captures stills without recording video. The real capture records lossless PNG frames through Chromium's CDP screencast API and retains elapsed frame timestamps for pauses and animations. The renderer requires FFmpeg with libx264 and GIF filters; set the `FFMPEG` environment variable to its executable, put it on PATH, or install `imageio-ffmpeg`. The renderer also looks for that package in `.tmp/nodepilot-video/tools`.

`TOUR_BASE_URL` defaults to `http://localhost:5173` and must use loopback. `TOUR_OUTPUT` defaults to the git-ignored `out/product-tour`. A fresh browser context intercepts API requests and rejects unexpected mutations; only local frontend assets may load. The hostnames, user, workflow history and statistics are all fictional. The existing browser profile and database are not accessed.

## Review and export

Open `out/product-tour/Ansehen.html` after rendering. The result is approximately 72 seconds, captured natively at 2560 × 1440 and exported as H.264/yuv420p, CRF 15, 30 fps, without audio. The application is shown at 88% of its previous relative size, exposing more content. A smaller 1920 × 1080 MP4 is also exported. Captions and the demo-data label are part of the video. The 16-second GIF is a condensed overview at 960 × 540.

The capture checks the graph, Monaco editor, Gantt chart, step output, execution drilldown, structured/plain-text logs and AI/identity settings. Identity settings are marked as Preview, consistent with the project documentation. Inspect `capture.json` for browser errors and endpoints served by the shared empty/default UI fixtures. The renderer decodes both finished MP4s and writes file sizes and validation status to `export.json`. Review screenshots and video visually as well.

The nine sets of PNG frames, concat timelines, stills and render logs live in `out/product-tour/raw/`. Final media stays outside source control. The accompanying `POST-TEXT.md` is only a publication draft.

To replace one scene after a complete capture, pass e.g. `--only=04-liveops` to `record.mjs`, then render again. Keep the other raw clips from that capture.
