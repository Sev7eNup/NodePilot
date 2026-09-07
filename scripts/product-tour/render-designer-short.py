"""New 8-second designer film: fresh native UI, original generated art and synth audio.

No existing video is opened as an input. Generated source images stay unchanged.
"""
from pathlib import Path
import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
import wave

import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageEnhance

ROOT = Path(__file__).resolve().parents[2]
DOCS_IMAGES = '--docs-images' in sys.argv
ORIGINAL_OUT = ROOT / 'out/nodepilot-designer-short-8s'
OUT = ROOT / ('out/nodepilot-designer-short-8s-v2' if DOCS_IMAGES else 'out/nodepilot-designer-short-8s')
ASSETS, RAW = OUT / 'assets', OUT / 'raw'
RAW.mkdir(parents=True, exist_ok=True)
if DOCS_IMAGES:
    ASSETS.mkdir(exist_ok=True)
    for name in ['designer-canvas.png', 'designer-context.png', 'designer-geometry.json', 'demo-workflow.json', 'capture-check.json']:
        shutil.copy2(ORIGINAL_OUT / 'assets' / name, ASSETS / name)
    for name in ['reddit-avatar.png', 'og-image.png']:
        shutil.copy2(ROOT / 'docs/images' / name, ASSETS / name)
W, H, FPS, DURATION = 1080, 1920, 60, 8
FFMPEG = os.environ.get('FFMPEG') or shutil.which('ffmpeg')
if not FFMPEG:
    sys.path.insert(0, str(ROOT / '.tmp/nodepilot-video/tools'))
    import imageio_ffmpeg
    FFMPEG = imageio_ffmpeg.get_ffmpeg_exe()
BOLD = 'C:/Windows/Fonts/segoeuib.ttf'
MEDIUM = 'C:/Windows/Fonts/seguisb.ttf'
MONO = 'C:/Windows/Fonts/consola.ttf'
WHITE, BLUE, CYAN, AMBER, PURPLE = '#f4f8ff', '#69a5ff', '#66e3ff', '#ffbd65', '#c6a4ff'
FONTS = {}
SILENT = OUT / 'NodePilot-Designer-8s-Silent.mp4'
FINAL = OUT / 'NodePilot-Designer-8s.mp4'
GEOMETRY = json.loads((ASSETS / 'designer-geometry.json').read_text(encoding='utf8'))
DPR = GEOMETRY['deviceScaleFactor']
CANVAS = Image.open(ASSETS / 'designer-canvas.png').convert('RGB')
IMAGES = {n: Image.open(ASSETS / f'{n}.png').convert('RGB') for n in (['reddit-avatar', 'og-image'] if DOCS_IMAGES else ['chaos', 'control', 'hero'])}
LOGO = Image.open(ROOT / 'src/nodepilot-ui/public/appicon-dark.png').convert('RGBA')
NODES = {n['id']: n for n in GEOMETRY['nodes']}
EDGES = GEOMETRY['edges']
CUTS = [.6, 1.4, 2.2, 3.4, 4.6, 6.6]

def clamp(x): return max(0., min(1., x))
def ease(x): return 1 - (1 - clamp(x)) ** 3
def lerp(a, b, x): return a + (b - a) * x

def font(size, face=BOLD):
    key = (round(size), face)
    if key not in FONTS:
        FONTS[key] = ImageFont.truetype(face, key[0])
    return FONTS[key]

def caption(im, words, y, size, color=WHITE, center=True, face=BOLD, opacity=1):
    layer = Image.new('RGBA', (W, H))
    d = ImageDraw.Draw(layer)
    f = font(size, face)
    while d.textlength(words, font=f) > 900:
        size -= 1
        f = font(size, face)
    x = (W - d.textlength(words, font=f)) / 2 if center else 84
    d.text((x + 1, y + 4), words, font=f, fill=(0, 0, 0, int(160 * opacity)), anchor='lt', stroke_width=2)
    rgb = tuple(int(color[i:i + 2], 16) for i in (1, 3, 5))
    d.text((x, y), words, font=f, fill=(*rgb, int(255 * opacity)), anchor='lt')
    im.paste(layer, (0, 0), layer)

def cover(source, zoom=1, dx=0, dy=0):
    s = max(W / source.width, H / source.height) * zoom
    return source.transform((W, H), Image.Transform.AFFINE,
        (1/s, 0, source.width/2 - (W/2 + dx)/s, 0, 1/s, source.height/2 - (H/2 + dy)/s),
        Image.Resampling.BICUBIC)

def docs_image_shot(t):
    """Use supplied artwork intact, fitted to the portrait video; never crop its text."""
    intro = t < .6
    p = t/.6 if intro else (t-6.6)/1.4
    source = IMAGES['reddit-avatar' if intro else 'og-image']
    im = ImageEnhance.Brightness(cover(source).filter(ImageFilter.GaussianBlur(75))).enhance(.24)
    if intro:
        size = round(850+42*ease(p))
        art = source.resize((size, size), Image.Resampling.LANCZOS)
        im.paste(art, ((W-size)//2, round(733-20*ease(p))))
        brand(im, t, PURPLE)
        caption(im, 'BUILD.', 325-16*ease(p*5), 164)
        caption(im, 'FLOWS.', 496-16*ease(p*5), 164, PURPLE)
    else:
        width = round(1020+36*ease(p))
        art = source.resize((width, round(source.height*width/source.width)), Image.Resampling.LANCZOS)
        im.paste(art, ((W-width)//2, round(744-14*ease(p))))
        caption(im, 'Build the flow.', 407+20*(1-ease(p*6)), 67, CYAN, face=MEDIUM)
        caption(im, 'Own the automation.', 507, 67, face=MEDIUM)
        caption(im, 'OPEN SOURCE  /  WINDOWS', 1372, 27, CYAN, face=MONO)
        caption(im, 'Explore on GitHub', 1451, 52)
        caption(im, 'github.com/Sev7eNup/NodePilot', 1537, 28, '#b7c3d5', face=MONO)
    return im

# Soft editorial mattes retain native UI rendering while creating text-safe margins.
yy = np.arange(H)[:, None]
xx = np.arange(W)[None, :]
top = np.clip((510 - yy) / 240, 0, 1)
bottom = np.clip((yy - 1550) / 260, 0, 1)
side = np.clip((np.abs(xx - W/2) - 410) / 190, 0, .45)
alpha = np.maximum(np.maximum(top, bottom), side)
MATTE = Image.new('RGBA', (W, H), (9, 13, 20, 255))
MATTE.putalpha(Image.fromarray((alpha * 255).astype('uint8')))
MACRO_MATTE = MATTE.copy()
macro_alpha = np.maximum(np.maximum(alpha, np.clip((yy-1370)/115,0,1)), np.clip((620-yy)/160,0,1))
MACRO_MATTE.putalpha(Image.fromarray((macro_alpha*255).astype('uint8')))
HEADER_MATTE = Image.new('RGBA', (W, H), (4, 7, 12, 255))
HEADER_MATTE.putalpha(Image.fromarray(np.broadcast_to((np.clip((650-yy)/400, 0, .72)*255).astype('uint8'), (H, W)).copy()))

def node_center(key):
    n = NODES[key]
    return n['x'] + n['width']/2, n['y'] + n['height']/2

def native(cx, cy, scale, t, target_y=1000, full=False):
    # Render from the 2x native raster. Motion is applied only in the finished video.
    xoff, yoff = W/2 - cx*scale, target_y - cy*scale
    im = CANVAS.transform((W, H), Image.Transform.AFFINE,
        (DPR/scale, 0, -xoff*DPR/scale, 0, DPR/scale, -yoff*DPR/scale),
        Image.Resampling.BICUBIC, fillcolor=(15, 18, 23))
    glow = Image.new('RGBA', (W, H))
    g = ImageDraw.Draw(glow)
    core = Image.new('RGBA', (W, H))
    c = ImageDraw.Draw(core)
    # Cinematic light accents follow sampled real SVG edge paths; no fake run status.
    for i, edge in enumerate(EDGES):
        pts = edge['points']
        pos = ((t - .6)*.75 - i*.16) % 1
        idx = min(120, int(pos*120))
        tail = pts[max(0, idx-13):idx+1]
        line = [(x*scale+xoff, y*scale+yoff) for x, y in tail]
        if len(line) > 1:
            g.line(line, fill=(72, 223, 255, 145), width=max(9, round(scale*11)))
            c.line(line, fill=(140, 245, 255, 220), width=max(2, round(scale*2)))
        px, py = pts[idx][0]*scale+xoff, pts[idx][1]*scale+yoff
        r = max(3, scale*3)
        c.ellipse((px-r, py-r, px+r, py+r), fill=(215, 255, 255, 240))
    glow = glow.filter(ImageFilter.GaussianBlur(13))
    im.paste(glow, (0, 0), glow)
    im.paste(core, (0, 0), core)
    matte = MATTE if full else MACRO_MATTE
    im.paste(matte, (0, 0), matte)
    return im

def brand(im, t, color=BLUE):
    logo = LOGO.resize((51, 51), Image.Resampling.LANCZOS)
    im.paste(logo, (84, 150), logo)
    d = ImageDraw.Draw(im)
    d.text((150, 157), 'NodePilot', font=font(33), fill=WHITE, anchor='lt')
    d.text((84, 228), 'WORKFLOW DESIGNER', font=font(24, MONO), fill=color, anchor='lt')
    # A discreet progress rail, within the vertical social safe area.
    d.line((84, 1740, 920, 1740), fill='#26303f', width=2)
    d.line((84, 1740, 84+836*t/8, 1740), fill=color, width=3)

def particles(im, t, blue=True, intensity=1):
    d = ImageDraw.Draw(im)
    for i in range(26):
        x = ((i*389.3 + t*(25+i%4*12)) % (W+80))-40
        y = ((i*677.7 - t*(45+i%6*20)) % (H+100))-50
        if 280 < y < 620: continue
        a = .4 + .6*(.5+.5*math.sin(i+t*3))
        v = round(130*a*intensity)
        color = (min(255, v+60), min(255, v+20), 55) if not blue else (35, min(255, v+45), min(255, v+95))
        d.line((x, y, x+2, y-7-i%5), fill=color, width=1+i%2)

def frame(t):
    if DOCS_IMAGES and (t < .6 or t >= 6.6):
        im = docs_image_shot(t)
    elif t < .6:
        p = t/.6
        im = cover(IMAGES['chaos'], 1.02+p*.16, dx=-18*p, dy=-30*p)
        im.paste(HEADER_MATTE, (0, 0), HEADER_MATTE)
        particles(im, t, False)
        brand(im, t, AMBER)
        caption(im, 'SCRIPT', 325-16*ease(p*5), 164)
        caption(im, 'CHAOS?', 496-16*ease(p*5), 164, AMBER)
        # Short blue transformation on the beat; no white strobe.
        if t > .47:
            alternate = cover(IMAGES['control'], 1.16-(t-.47)*.7)
            im = Image.blend(im, alternate, .72*clamp((t-.47)/.13))
    elif t < 1.4:
        p = (t-.6)/.8
        cx, cy = node_center('copy')
        im = native(cx+16*(1-ease(p*4)), cy, 2.35-.14*ease(p), t)
        brand(im, t, AMBER)
        caption(im, 'FILES.', 315+24*(1-ease(p*5)), 164, AMBER)
        caption(im, 'Copy. Archive. Done.', 1500, 39, face=MEDIUM)
    elif t < 2.2:
        p = (t-1.4)/.8
        cx, cy = node_center('script')
        im = native(cx-12*(1-ease(p*4)), cy, 2.30-.1*ease(p), t)
        brand(im, t)
        caption(im, 'SCRIPTS.', 315+24*(1-ease(p*5)), 150, BLUE)
        caption(im, 'Put PowerShell in the flow.', 1500, 39, face=MEDIUM)
    elif t < 3.4:
        p = (t-2.2)/1.2
        im = native(710, 655+20*ease(p), .95-.07*ease(p), t, target_y=970)
        brand(im, t, CYAN)
        caption(im, 'CONNECT.', 315+24*(1-ease(p*5)), 148, CYAN)
        caption(im, 'Two branches. One workflow.', 1500, 39, face=MEDIUM)
    elif t < 4.6:
        p = (t-3.4)/1.2
        cx, cy = node_center('llm')
        im = native(cx, cy+9*ease(p), 2.32-.18*ease(p), t)
        brand(im, t, PURPLE)
        caption(im, 'ADD AI.', 315+24*(1-ease(p*5)), 150, PURPLE)
        caption(im, 'Turn results into a brief.', 1500, 39, face=MEDIUM)
    elif t < 6.6:
        p = (t-4.6)/2
        pull = ease((t-4.6)/.43)
        im = native(lerp(node_center('llm')[0], 710, pull), lerp(node_center('llm')[1], 1000, pull), lerp(2.14, .69+.012*p, pull), t, target_y=1015, full=True)
        brand(im, t, CYAN)
        caption(im, 'ONE FLOW.', 312, 138, opacity=ease((t-4.6)/.25))
        caption(im, 'Windows automation. Connected.', 1634, 34, face=MEDIUM)
    else:
        p = (t-6.6)/1.4
        im = cover(IMAGES['hero'], 1.10-.05*ease(p), dy=-65)
        particles(im, t, True, .65)
        logo = LOGO.resize((90, 90), Image.Resampling.LANCZOS)
        im.paste(logo, (495, 272), logo)
        caption(im, 'NodePilot', 427+20*(1-ease(p*6)), 152)
        caption(im, 'Build the flow.', 625, 49, CYAN, face=MEDIUM)
        caption(im, 'Own the automation.', 692, 49, face=MEDIUM)
        caption(im, 'OPEN SOURCE  /  WINDOWS', 816, 27, CYAN, face=MONO)
        caption(im, 'Explore on GitHub', 881, 43)
        caption(im, 'github.com/Sev7eNup/NodePilot', 946, 25, '#9dabbd', face=MONO)
    # Brief directional displacement creates a cut impact without repetitive flashes.
    since = min((t-c for c in CUTS if t >= c), default=1)
    if 0 <= since < .065:
        amount = (1-since/.065)
        shifted = im.transform(im.size, Image.Transform.AFFINE, (1, 0, 12*amount, 0, 1, 0), Image.Resampling.BICUBIC)
        im = Image.blend(im, shifted, .28*amount)
    return im

def run(args, name):
    with (RAW / f'{name}.log').open('w', encoding='utf8') as log:
        subprocess.run([FFMPEG, '-hide_banner', '-y', *args], stdout=log, stderr=log, check=True)

def preserve():
    target = OUT / 'preserved-versions.json'
    if not target.exists():
        hashes = {}
        for folder in [*(ROOT / 'out').glob('product-tour*'), ROOT / 'out/nodepilot-short-8s', *([ORIGINAL_OUT] if DOCS_IMAGES else [])]:
            if folder.is_dir():
                for p in folder.iterdir():
                    if p.is_file():
                        hashes[str(p.relative_to(ROOT))] = hashlib.sha256(p.read_bytes()).hexdigest()
        target.write_text(json.dumps(hashes, indent=2), encoding='utf8')

def make_audio():
    """Original 150-BPM electro break, synchronized to the new editorial cuts."""
    sr = 48000
    rng = np.random.default_rng(8080)
    track = np.zeros((sr*DURATION, 2))
    def add(sound, at, gain=1, pan=0):
        start = round(at*sr)
        if start < 0:
            sound, start = sound[-start:], 0
        n = min(len(sound), len(track)-start)
        if n <= 0: return
        track[start:start+n, 0] += sound[:n]*gain*math.sqrt((1-pan)/2)
        track[start:start+n, 1] += sound[:n]*gain*math.sqrt((1+pan)/2)
    def time(length): return np.arange(round(length*sr))/sr
    # A rising glass sweep followed by the first deep kick at the designer reveal.
    x = time(.6)
    sweep = rng.normal(0, 1, len(x))
    sweep = np.r_[0, np.diff(sweep)] * (x/.6)**2 * .075
    add(sweep, 0, 1, -.3)
    add(np.sin(2*np.pi*(250*x+1400*x*x))*np.sin(np.pi*x/.6)**2, 0, .14, .3)
    for beat in range(18):
        at = .6+beat*.4
        x = time(.32)
        phase = 2*np.pi*np.cumsum(45+155*np.exp(-x*38))/sr
        kick = np.sin(phase)*np.exp(-x*15)
        add(kick, at, .84 if beat%4==0 else .66)
        if beat%2:
            x = time(.17)
            noise = rng.normal(0, 1, len(x))
            high = np.r_[0, np.diff(noise)]
            snap = high*np.exp(-x*45)+.4*np.sin(2*np.pi*190*x)*np.exp(-x*28)
            add(snap, at, .19)
    for step in range(36):
        x = time(.055)
        noise = np.r_[0, np.diff(rng.normal(0, 1, len(x)))]
        add(noise*np.exp(-x*105), .6+step*.2, .055 if step%2 else .036, .5 if step%2 else -.5)
    # E minor ostinato with syncopated bass; entirely locally synthesized.
    notes = [40, 40, 43, 47, 40, 38, 43, 35, 40]
    for i, note in enumerate(notes):
        x = time(.70)
        hz = 440*2**((note-69)/12)
        bass = sum(np.sin(2*np.pi*hz*h*x)/h**1.8 for h in range(1, 6))
        env = (1-np.exp(-x*110))*np.exp(-x*5)*(.30+.70*np.minimum(1,(x%.4)/.11))
        add(bass*env, .6+i*.8, .43)
    arp = [76, 79, 83, 86, 83, 79, 74, 78]
    for step in range(34):
        x = time(.31)
        hz = 440*2**((arp[step%8]-69)/12)
        bell = np.sin(2*np.pi*hz*x+1.7*np.sin(2*np.pi*hz*2*x)*np.exp(-x*30))
        sound = bell*(1-np.exp(-x*600))*np.exp(-x*19)
        add(sound, .7+step*.2, .092, .55*math.sin(step*1.7))
        add(sound, .85+step*.2, .025, -.55*math.sin(step*1.7))
    for i, at in enumerate(CUTS):
        x = time(.18)
        whoosh = rng.normal(0,1,len(x))*np.sin(np.pi*x/.18)**2
        add(whoosh, at-.15, .05, (-1 if i%2 else 1)*.6)
        x = time(.25)
        impact = np.sin(2*np.pi*(105*x-35*x*x))*np.exp(-x*18)
        add(impact, at, .26)
    # Brand landing: a resonant E-minor glass chord and a controlled tail.
    x = time(1.4)
    chord = sum(np.sin(2*np.pi*f*x)*np.exp(-x*(2.5+j*.5)) for j,f in enumerate([329.6276,391.9954,493.8833]))
    add(chord*(1-np.exp(-x*130)), 6.6, .105)
    track = np.tanh(track*1.15)
    fade = np.minimum(1, np.arange(len(track))/240)
    fade *= np.minimum(1, np.arange(len(track))[::-1]/7200)
    track *= fade[:,None]
    track *= .76/max(1e-9, np.max(np.abs(track)))
    audio = RAW / 'original-designer-soundtrack.wav'
    with wave.open(str(audio), 'wb') as wav:
        wav.setnchannels(2); wav.setsampwidth(2); wav.setframerate(sr)
        wav.writeframes((track*32767).astype('<i2').tobytes())
    return audio

def previews():
    times = [.25, 1.05, 1.85, 2.85, 3.95, 5.5, 7.3]
    sheet = Image.new('RGB', (7*270, 520), '#050810')
    for i, t in enumerate(times):
        im = frame(t)
        im.save(RAW / f'preview-{t:.2f}.png')
        sheet.paste(im.resize((270,480), Image.Resampling.LANCZOS), (i*270,0))
        ImageDraw.Draw(sheet).text((i*270+12,490), f'{t:.2f}s', font=font(19,MONO), fill=WHITE)
    sheet.save(OUT / 'Storyboard.png')
    frame(5.8).save(OUT / 'NodePilot-Designer-Poster.png')

def render():
    preserve()
    previews()
    if '--preview' in sys.argv:
        print('Preview ready: ' + str(OUT / 'Storyboard.png'), flush=True)
        return
    audio = make_audio()
    with (RAW / 'encode.log').open('w',encoding='utf8') as log:
        process = subprocess.Popen([FFMPEG,'-hide_banner','-y','-f','rawvideo','-pixel_format','rgb24',
            '-video_size',f'{W}x{H}','-framerate',str(FPS),'-i','pipe:0','-an','-c:v','libx264',
            '-preset','slow','-crf','15','-threads','4','-vf','scale=in_range=pc:out_range=tv:out_color_matrix=bt709',
            '-pix_fmt','yuv420p','-movflags','+faststart',
            '-color_primaries','bt709','-color_trc','bt709','-colorspace','bt709',
            '-frames:v',str(DURATION*FPS),str(SILENT)],stdin=subprocess.PIPE,stdout=log,stderr=log)
        try:
            for i in range(DURATION*FPS):
                process.stdin.write(frame(i/FPS).tobytes())
                if i%60 == 0: print(f'Rendered {i}/{DURATION*FPS} frames',flush=True)
        finally:
            process.stdin.close()
        if process.wait(): raise RuntimeError('Video encoding failed; see raw/encode.log')
    run(['-i',str(SILENT),'-i',str(audio),'-map','0:v:0','-map','1:a:0','-c:v','copy',
         '-af','loudnorm=I=-15:TP=-3:LRA=7','-ar','48000','-c:a','aac','-b:a','256k',
         '-t',str(DURATION),'-movflags','+faststart',str(FINAL)],'mux')
    run(['-v','error','-i',str(FINAL),'-map','0:v:0','-map','0:a:0','-f','null','-','-progress','pipe:1'],'decode-check')
    run(['-i',str(FINAL),'-af','ebur128=peak=true','-f','null','-'],'audio-check')
    hashes = json.loads((OUT / 'preserved-versions.json').read_text(encoding='utf8'))
    changed = [name for name, digest in hashes.items() if hashlib.sha256((ROOT/name).read_bytes()).hexdigest()!=digest]
    if changed: raise RuntimeError('Existing outputs changed: '+str(changed))
    metadata = {
        'durationSeconds':DURATION,'width':W,'height':H,'fps':FPS,'frames':DURATION*FPS,
        'video':'H.264, CRF 15, slow, yuv420p, BT.709','audio':'Original 150 BPM synth, stereo AAC, 48 kHz, 256 kbit/s',
        'designerSeconds':6,'designerShare':.75,'previousVideoInputs':[],
        'images':({'mode':'Existing user-supplied repository artwork','sources':['docs/images/reddit-avatar.png','docs/images/og-image.png'],
                   'files':['assets/reddit-avatar.png','assets/og-image.png'],'generatedImageInputs':[]} if DOCS_IMAGES else
                  {'mode':'built-in image_gen','prompts':'assets/PROMPTS.md','files':['assets/chaos.png','assets/control.png','assets/hero.png']}),
        'nativeCapture':'Fresh NodePilot Workflow Designer, 2x pixel density, fictional read-only demo data',
        'effects':'Camera motion, kinetic captions, editorial light accents following native edge paths',
        'timeline':[{'at':0,'duration':.6,'scene':'NodePilot avatar' if DOCS_IMAGES else 'Chaos to control'},{'at':.6,'duration':.8,'scene':'File Copy'},
            {'at':1.4,'duration':.8,'scene':'PowerShell'},{'at':2.2,'duration':1.2,'scene':'Parallel workflow branches'},
            {'at':3.4,'duration':1.2,'scene':'LLM Summary'},{'at':4.6,'duration':2,'scene':'Complete Workflow Designer'},
            {'at':6.6,'duration':1.4,'scene':'Full og-image.png artwork' if DOCS_IMAGES else 'NodePilot hero reveal'}],
        **({'revisionOf':str(ORIGINAL_OUT.relative_to(ROOT)), 'unchanged':['Workflow Designer frames 36–395','all cuts and durations','original soundtrack']} if DOCS_IMAGES else {}),
        'preservedExistingFiles':len(hashes),'preservationVerified':not changed,
        'outputs':{p.name:{'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in [FINAL,SILENT]},
    }
    (OUT/'export.json').write_text(json.dumps(metadata,indent=2),encoding='utf8')
    print(json.dumps(metadata,indent=2),flush=True)

if __name__ == '__main__': render()
