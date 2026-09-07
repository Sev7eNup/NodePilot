# NodePilot designer short — original 8-second concept

## Revision with repository artwork

The current revision uses `docs/images/reddit-avatar.png` for the opening and the complete `docs/images/og-image.png` for the ending. All three generated images are excluded from this revision. The native designer sequence (0.6–6.6 seconds), cut timings and soundtrack are unchanged. The original output folder remains preserved.

```powershell
python scripts/product-tour/render-designer-short.py --docs-images --preview
python scripts/product-tour/render-designer-short.py --docs-images
node scripts/product-tour/check-designer-short.mjs --docs-images
```

Output: `out/nodepilot-designer-short-8s-v2/Ansehen.html`.

## Previous revision

Output: `out/nodepilot-designer-short-8s/Ansehen.html` and `NodePilot-Designer-8s.mp4`.

This film uses no previous tour or short footage. The existing exports remain intact.
It combines fresh native Workflow Designer captures with three original cinematic assets and an original 150 BPM synthesized soundtrack.

## Reproduce

With the local frontend already available at `http://localhost:5173`:

```powershell
node --experimental-strip-types scripts/product-tour/capture-designer-short.mjs
python scripts/product-tour/render-designer-short.py --preview
python scripts/product-tour/render-designer-short.py
node scripts/product-tour/check-designer-short.mjs
```

The capture script uses the real frontend and fictional, read-only mocked API data.
It blocks remote origins and fails on any API mutation or browser page error.
The new six-node workflow branches into File Copy and PowerShell, waits for both, produces an LLM summary and returns the report.
Native screenshots use 2x pixel density. SVG edge geometry is sampled for editorial light accents in the video; those accents do not claim a real workflow execution.

Generated source images, original prompts and capture checks are kept in `assets/`.
The image mode is the built-in `image_gen` tool; no CLI/API generation path is used.
Source PNGs remain unchanged during rendering.

## Edit

| Time | Scene |
| --- | --- |
| 0.0–0.6 | Script chaos, blue transformation |
| 0.6–1.4 | File Copy macro |
| 1.4–2.2 | PowerShell macro |
| 2.2–3.4 | Native parallel branches |
| 3.4–4.6 | LLM Summary macro |
| 4.6–6.6 | Pull back to the complete designer |
| 6.6–8.0 | NodePilot brand and GitHub reveal |

The designer occupies 75% of the film. English text stays inside the principal mobile safe area.
Export: exactly 480 frames / 8 seconds, 1080 × 1920, 60 fps, H.264 CRF 15, slow preset, yuv420p, BT.709, fast-start MP4.
Audio: original stereo synthesis at 48 kHz, AAC 256 kbit/s. A silent export is included.

`export.json`, `playback-check.json`, the FFmpeg decode/loudness logs and SHA-256 preservation checks document validation. No app source code is changed for this short.
