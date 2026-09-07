"""Render the recorded tour with FFmpeg; no Python imaging packages required."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / os.environ.get('TOUR_OUTPUT', 'out/product-tour-with-import')
FFMPEG = os.environ.get('FFMPEG') or shutil.which('ffmpeg')
if not FFMPEG:
    sys.path.insert(0, str(ROOT / '.tmp/nodepilot-video/tools'))
    try:
        import imageio_ffmpeg
        FFMPEG = imageio_ffmpeg.get_ffmpeg_exe()
    except ImportError:
        raise SystemExit('Set FFMPEG to an FFmpeg executable, or install imageio-ffmpeg.')


def run(args, name):
    if '--verify-only' in sys.argv and name.startswith('render-'):
        if not Path(args[-1]).is_file():
            raise SystemExit(f'Missing export: {args[-1]}')
        return
    print(name, flush=True)
    with (OUT / 'raw' / f'{name}.log').open('w', encoding='utf-8') as log:
        subprocess.run([FFMPEG, '-hide_banner', '-y', *args], check=True, stdout=log, stderr=log)


capture = json.loads((OUT / 'capture.json').read_text(encoding='utf-8'))
if capture['preview'] or capture['errors']:
    raise SystemExit('Record a clean non-preview capture first.')
segments = capture['segments']
fade = 0.4
inputs = []
filters = []
for i, segment in enumerate(segments):
    inputs += ['-f', 'concat', '-safe', '0', '-threads', '1', '-i', str(OUT / 'raw' / f"{segment['name']}.ffconcat")]
    # The repeated final PNG already retains a scene's final pause. tpad assumes
    # evenly spaced source frames, so it must not precede the VFR-to-CFR step.
    # Reassert fps after setpts so xfade also receives constant-rate metadata.
    filters.append(f"[{i}:v]fps=30,trim=end_frame={round(segment['duration']*30)},setpts=N/(30*TB),setsar=1,format=yuv420p,fps=30,settb=1/30[v{i}]")
end = segments[0]['duration']
previous = 'v0'
for i, segment in enumerate(segments[1:], 1):
    name = f'x{i}'
    filters.append(f'[{previous}][v{i}]xfade=transition=fade:duration={fade}:offset={end-fade:.3f}[{name}]')
    end += segment['duration'] - fade
    previous = name
filters.append(f'[{previous}]format=yuv420p[final]')
master = OUT / 'NodePilot-Product-Tour.mp4'
run([*inputs, '-filter_complex_threads', '2', '-filter_complex', ';'.join(filters), '-map', '[final]',
     '-an', '-c:v', 'libx264', '-preset', 'slow', '-crf', '15', '-threads', '4',
     '-pix_fmt', 'yuv420p', '-movflags', '+faststart', '-metadata',
     'title=NodePilot — Product Tour', '-metadata',
     'comment=Real NodePilot frontend with fictional demo data. No live backend execution.', str(master)], 'render-master')

share = OUT / 'NodePilot-Product-Tour-1080p.mp4'
run(['-i', str(master), '-vf', 'scale=1920:1080:flags=lanczos', '-an', '-c:v', 'libx264',
     '-preset', 'slow', '-crf', '17', '-threads', '4', '-pix_fmt', 'yuv420p',
     '-movflags', '+faststart', str(share)], 'render-1080p')

# Select by name so inserting an import scene preserves the existing excerpts.
starts = {}
cursor = 0
for segment in segments:
    starts[segment['name']] = cursor
    cursor += segment['duration'] - fade
excerpts = [('00-import',2.8,4.8)] if '00-import' in starts else []
excerpts += [(name,0.8,2.8) for name in ['01-designer','02-history','03-dashboard','04-liveops']]
excerpts += [('05-log',2.1,5.1),('06-chat',5,7),('07-ai',0.5,2.5),('08-auth',3.9,6.9)]
cuts = [(starts[name]+start, starts[name]+stop) for name,start,stop in excerpts]
gif_duration = sum(stop-start for _,start,stop in excerpts)
gif_filters = [f'[0:v]fps=8,scale=960:-1:flags=lanczos,split={len(cuts)}' + ''.join(f'[s{i}]' for i in range(len(cuts)))]
for i, (start, stop) in enumerate(cuts):
    gif_filters.append(f'[s{i}]trim=start={start}:end={stop},setpts=PTS-STARTPTS[c{i}]')
gif_filters.append(''.join(f'[c{i}]' for i in range(len(cuts))) + f'concat=n={len(cuts)}:v=1:a=0,split[a][b]')
gif_filters += ['[a]palettegen=max_colors=128:stats_mode=diff[p]', '[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle[g]']
run(['-i', str(master), '-filter_complex_threads', '2', '-filter_complex', ';'.join(gif_filters),
     '-map', '[g]', '-loop', '0', str(OUT / 'NodePilot-Preview.gif')], 'render-gif')
shutil.copyfile(OUT / 'raw/01-designer-start.png', OUT / 'NodePilot-Poster.png')

# Decode the entire final file, rather than only checking that the encoder exited.
for video, label in [(master, 'verify-decode'), (share, 'verify-1080p-decode')]:
    progress = OUT / 'raw' / f'{label}-timing.txt'
    run(['-v', 'error', '-i', str(video), '-progress', str(progress), '-f', 'null', '-'], label)
    if (OUT / 'raw' / f'{label}.log').read_text().strip():
        raise SystemExit(f'{video.name} reported decode errors; inspect raw/{label}.log.')
    timing = dict(line.split('=', 1) for line in progress.read_text().splitlines() if '=' in line)
    # Frame endpoint rounding may differ by one frame between the filter graph
    # and the MP4 muxer. Longer deviations indicate missing or extended scenes.
    actual_duration = int(timing['out_time_us']) / 1_000_000
    if abs(actual_duration - end) > 0.05 or abs(int(timing['frame']) - round(end * 30)) > 1:
        raise SystemExit(f'{video.name} has an unexpected duration or frame count: {timing}')
report = {'durationSeconds': round(actual_duration, 3), 'plannedDurationSeconds':round(end,3), 'width':capture['width'], 'height':capture['height'], 'fps':30,
          'uiScale':capture['uiScale'], 'capture':'lossless PNG', 'crf':15, 'gifDurationSeconds':gif_duration,
          'codec':'H.264', 'pixelFormat':'yuv420p', 'audio':False, 'demoData':True,
          'files': {p.name: p.stat().st_size for p in [master, share, OUT/'NodePilot-Preview.gif', OUT/'NodePilot-Poster.png']},
          'frameCount':int(timing['frame']), 'decodeValidation':'passed', 'timingValidation':'passed', 'browserErrors':capture['errors']}
(OUT / 'export.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, indent=2))

preview_html = '''<!doctype html><html lang="de"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>NodePilot · Produkttour</title><style>body{margin:40px auto;padding:0 24px;max-width:1200px;font:17px/1.6 system-ui;background:#0b1019;color:#eaf1ff}h1{font-weight:600}p{color:#b3c2d9}video{width:100%;border:1px solid #354255;border-radius:12px}a{color:#91b9ff}nav{display:flex;gap:26px;flex-wrap:wrap}</style><h1>NodePilot · Produkttour</h1><p>51 Sekunden · Full HD · Englische Einblendungen · Ohne Ton · Fiktive Demo-Daten</p><video controls playsinline preload="metadata" poster="NodePilot-Poster.png"><source src="NodePilot-Product-Tour.mp4" type="video/mp4"></video><nav><a href="NodePilot-Product-Tour.mp4" download>MP4 herunterladen</a><a href="NodePilot-Preview.gif" download>GIF-Vorschau</a><a href="NodePilot-Poster.png" download>Vorschaubild</a><a href="POST-TEXT.md">Post-Entwurf</a></nav></html>'''
preview_html = preview_html.replace('51 Sekunden · Full HD', f'{end:.1f} Sekunden · 1440p · Kompaktere Oberfläche')
preview_html = preview_html.replace('MP4 herunterladen</a>', 'MP4 in 1440p herunterladen</a><a href="NodePilot-Product-Tour-1080p.mp4" download>MP4 in 1080p herunterladen</a>')
(OUT / 'Ansehen.html').write_text(preview_html, encoding='utf-8')
shutil.copyfile(Path(__file__).with_name('POST-TEXT.md'), OUT / 'POST-TEXT.md')
