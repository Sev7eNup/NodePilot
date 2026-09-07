# Eight-second vertical short

`python scripts/product-tour/render-short.py` creates a separate export in `out/nodepilot-short-8s`. It uses the approved `out/product-tour-liveops-v2/NodePilot-Product-Tour.mp4` as footage and retains every earlier tour file. `--stills` produces design previews without encoding the short.

The composition is 1080 × 1920 at 60 fps, exactly 480 frames. Seven scenes show PowerShell, SCOrch import, the workflow graph, Live-Ops, AI Chat, open source and the GitHub call to action. Crops, timing, animated typography and camera movement are defined in the renderer. The source footage contains fictional demo data, including a scripted AI Chat response.

The renderer synthesizes an original 120 BPM electronic beat and transition effects from oscillators and seeded noise. No music recordings, samples or external audio services are used. Deliverables include an H.264/AAC version, a silent H.264 version, a PNG poster, a local preview player and a post draft. The soundtrack is normalized toward −14 LUFS with a −1.5 dBTP target before AAC encoding.

Requires Python with Pillow and NumPy, Windows Segoe UI/Consolas fonts, and FFmpeg. `FFMPEG` can point to the executable; otherwise the same local `imageio-ffmpeg` fallback as the product tour is used. The renderer verifies frame count, duration, video/audio decoding and checksums of the existing tour files. Its report is saved as `out/nodepilot-short-8s/export.json`.
