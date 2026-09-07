"""Create an eight-second vertical short from the approved tour and original synth audio."""
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
from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'out/product-tour-liveops-v2/NodePilot-Product-Tour.mp4'
OUT = ROOT / 'out/nodepilot-short-8s'
RAW = OUT / 'raw'
FFMPEG = os.environ.get('FFMPEG') or shutil.which('ffmpeg')
if not FFMPEG:
    sys.path.insert(0, str(ROOT / '.tmp/nodepilot-video/tools'))
    import imageio_ffmpeg
    FFMPEG = imageio_ffmpeg.get_ffmpeg_exe()

W, H, FPS, SECONDS = 1080, 1920, 60, 8
BLUE, GREEN, WHITE = '#5599ff', '#c1ff6c', '#f4f7ff'
SHOTS = [
    dict(at=0, length=1.5, source=24.45, speed=1.4, crop=(820,330,1210,880),
         a='YOUR SCRIPTS.', b='SUPERCHARGED.', label='POWERSHELL', sub='Windows automation. Visually.', color=BLUE),
    dict(at=1.5, length=1, source=6.7, speed=1.2, crop=(760,460,1100,820),
         a='IMPORT.', b='KEEP BUILDING.', label='SCORCH IMPORT', sub='.ois_export → NodePilot', color=GREEN),
    dict(at=2.5, length=1, source=15.9, speed=1.2, crop=(910,390,1220,900),
         a='CONNECT.', b='AUTOMATE.', label='VISUAL WORKFLOWS', sub='PowerShell. File Copy. LLM.', color=BLUE),
    dict(at=3.5, length=1, source=41.1, speed=1.6, crop=(465,295,1240,920),
         a='LIVE OPS.', b='ONE VIEW.', label='LIVE OPERATIONS', sub='See your automation in motion.', color=GREEN),
    dict(at=4.5, length=1, source=61.1, speed=1.4, crop=(960,270,990,735),
         a='ASK AI.', b='GET CONTEXT.', label='AI CHAT', sub='Your workflows. In the conversation.', color=BLUE),
    dict(at=5.5, length=1, source=15.9, speed=1.5, crop=(900,390,1300,950),
         a='OPEN.', b='SOURCE.', label='BUILT FOR WINDOWS', sub='Self-hosted · Apache-2.0', color=GREEN),
]
FONT = Path('C:/Windows/Fonts/segoeuib.ttf')
REGULAR = Path('C:/Windows/Fonts/seguisb.ttf')
MONO = Path('C:/Windows/Fonts/consola.ttf')
font_cache = {}

def font(size, face=FONT):
    key = (str(face), round(size))
    if key not in font_cache:
        font_cache[key] = ImageFont.truetype(str(face), round(size))
    return font_cache[key]

def run(args, name):
    print(name, flush=True)
    with (RAW / f'{name}.log').open('w', encoding='utf-8') as log:
        subprocess.run([FFMPEG, '-hide_banner', '-y', *args], check=True, stdout=log, stderr=log)

def prepare():
    OUT.mkdir(exist_ok=True)
    RAW.mkdir(exist_ok=True)
    if not (OUT / 'preserved-versions.json').exists():
        hashes = {}
        for directory in (ROOT / 'out').glob('product-tour*'):
            if directory.is_dir():
                for p in directory.iterdir():
                    if p.is_file():
                        hashes[str(p.relative_to(ROOT))] = hashlib.sha256(p.read_bytes()).hexdigest()
        (OUT / 'preserved-versions.json').write_text(json.dumps(hashes, indent=2))
    for i, shot in enumerate(SHOTS):
        directory = RAW / f'shot-{i}'
        directory.mkdir(exist_ok=True)
        count = round(shot['length'] * FPS)
        if len(list(directory.glob('*.png'))) == count:
            continue
        x,y,w,h = shot['crop']
        filters = f"setpts=(PTS-STARTPTS)/{shot['speed']},fps={FPS},crop={w}:{h}:{x}:{y},scale=1080:-2:flags=lanczos"
        run(['-ss',str(shot['source']),'-threads','2','-i',str(SOURCE),
             '-vf',filters,'-frames:v',str(count),'-an','-threads','2',
             str(directory / '%04d.png')], f'extract-{i}')
        assert len(list(directory.glob('*.png'))) == count

def make_audio():
    sr = 48000
    rng = np.random.default_rng(20260907)
    track = np.zeros((sr*SECONDS,2),dtype=np.float64)
    def add(sound, start, level=1, pan=0):
        offset = round(start*sr)
        n = min(len(sound),len(track)-offset)
        if n <= 0: return
        track[offset:offset+n,0] += sound[:n]*level*math.sqrt((1-pan)/2)
        track[offset:offset+n,1] += sound[:n]*level*math.sqrt((1+pan)/2)
    # 120 BPM, four bars. All oscillators and noise are synthesized locally.
    for beat in range(16):
        start = beat*.5
        t = np.arange(round(sr*.28))/sr
        phase = 2*np.pi*np.cumsum(48 + 125*np.exp(-t*32))/sr
        kick = np.sin(phase)*np.exp(-t*18) + rng.normal(0,1,len(t))*np.exp(-t*420)*.1
        add(kick,start,.74)
        if beat%2:
            t = np.arange(round(sr*.18))/sr
            noise = rng.normal(0,1,len(t))
            noise = np.concatenate([[0],np.diff(noise)])
            env = np.exp(-t*36)*(1+.45*np.cos(2*np.pi*55*t))
            add(noise*env,start,.19)
    for step in range(32):
        t = np.arange(round(sr*.055))/sr
        noise = rng.normal(0,1,len(t))
        high = np.concatenate([[0],np.diff(noise)])
        add(high*np.exp(-t*105),step*.25,.065 if step%2 else .037,(-1 if step%2 else 1)*.38)
    bass_notes = [38,38,41,36,34,34,36,38]
    for i,note in enumerate(bass_notes):
        t = np.arange(round(sr*.9))/sr
        hz = 440*2**((note-69)/12)
        bass = sum(np.sin(2*np.pi*hz*h*t)/h**1.6 for h in range(1,5))
        env = (1-np.exp(-t*60))*np.exp(-t*4.2)
        duck = .4+.6*np.minimum(1,(t%.5)/.13)
        add(bass*env*duck,i,.3)
    notes = [74,81,77,76,74,77,81,84]
    for i in range(32):
        t = np.arange(round(sr*.22))/sr
        hz = 440*2**((notes[i%8]-69)/12)
        tone = np.sin(2*np.pi*hz*t)+.28*np.sin(2*np.pi*hz*2.006*t)
        add(tone*(1-np.exp(-t*150))*np.exp(-t*23),i*.25,.055 if i<26 else .038,math.sin(i)*.48)
    for cut in [1.5,2.5,3.5,4.5,5.5,6.5]:
        t = np.arange(round(sr*.13))/sr
        noise = rng.normal(0,1,len(t))
        sweep = np.sin(2*np.pi*(1700*t-5600*t*t))
        add((noise*.22+sweep*.18)*np.sin(np.pi*t/.13)**2,cut-.1,.14)
        t = np.arange(round(sr*.2))/sr
        add(np.sin(2*np.pi*(240*t-370*t*t))*np.exp(-t*25),cut,.16)
    t = np.arange(round(sr*1.4))/sr
    chord = sum(np.sin(2*np.pi*440*2**((n-69)/12)*t) for n in [62,69,74,77])/4
    add(chord*(1-np.exp(-t*60))*np.exp(-t*2.8),6.5,.16)
    track = np.tanh(track*1.15)
    track *= 10**(-2/20)/np.max(np.abs(track))
    track[:240] *= np.linspace(0,1,240)[:,None]
    track[-5760:] *= np.linspace(1,0,5760)[:,None]
    with wave.open(str(RAW/'soundtrack.wav'),'wb') as wav:
        wav.setnchannels(2); wav.setsampwidth(2); wav.setframerate(sr)
        wav.writeframes((track*32767).astype('<i2').tobytes())

def background():
    yy,xx = np.mgrid[0:H,0:W]
    glow = np.exp(-((xx-920)**2/(850**2)+(yy-850)**2/(1150**2)))
    cool = np.exp(-((xx-60)**2/(650**2)+(yy-1420)**2/(500**2)))
    rgb = np.stack([5+glow*8+cool*2,9+glow*15+cool*8,17+glow*36+cool*10],axis=-1)
    image = Image.fromarray(rgb.astype(np.uint8))
    draw = ImageDraw.Draw(image)
    for x in range(30,W,60):
        for y in range(25,H,60):
            draw.ellipse((x,y,x+2,y+2),fill='#233044')
    draw.text((-20,1705),'NODEPILOT',font=font(188),fill='#101c2b')
    return image

BACKGROUND = background()
LOGO = Image.open(ROOT/'src/nodepilot-ui/public/appicon-dark.png').convert('RGBA')

def write_text(image, text, xy, size, fill, max_width=920, face=FONT):
    f = font(size,face)
    while f.getlength(text)>max_width:
        size -= 1; f=font(size,face)
    ImageDraw.Draw(image).text(xy,text,font=f,fill=fill,anchor='lt',stroke_width=0)

def common(image,t,index):
    d = ImageDraw.Draw(image)
    bright = index == 5
    ink = '#101c24' if bright else WHITE
    muted = '#36513a' if bright else '#8598b8'
    # Slow traveling rails provide movement without full-screen flashes.
    for k in range(3):
        y = ((t*180+k*610)%2100)-120
        d.line([(0,y+260),(W,y)],fill='#ace662' if bright else '#172d4c',width=2)
    logo = LOGO.resize((66,66),Image.Resampling.LANCZOS)
    image.paste(logo,(80,113),logo)
    write_text(image,'NodePilot',(164,120),42,ink)
    write_text(image,'WINDOWS AUTOMATION',(82,204),24,muted,face=REGULAR)
    for j in range(7):
        x=80+j*117
        color=('#152c2b' if bright else BLUE) if j<=index else ('#91c950' if bright else '#293649')
        d.rounded_rectangle((x,1587,x+98,1594),radius=3,fill=color)
    write_text(image,'REAL APP · DEMO DATA',(80,1631),21,muted if bright else '#8294ad',face=REGULAR)
    for j in range(20):
        height=5+int(20*abs(math.sin(t*math.pi*4+j*.7)))
        x=710+j*9
        d.rounded_rectangle((x,1651-height,x+4,1651),radius=2,fill=ink if bright else BLUE)

def footage_card(t,index,shot):
    local=t-shot['at']
    frame=min(round(shot['length']*FPS)-1,int(local*FPS+1e-4))+1
    with Image.open(RAW/f'shot-{index}'/f'{frame:04d}.png') as f:
        video=f.convert('RGB')
    # Punch in at a cut, then glide slowly through the shot.
    entrance=math.exp(-local*18)
    scale=1.01+.075*entrance+.027*local/shot['length']+.024*math.exp(-(t%.5)*16)
    target_w,target_h=908,690
    factor=max(target_w/video.width,target_h/video.height)*scale
    video=video.resize((round(video.width*factor),round(video.height*factor)),Image.Resampling.LANCZOS)
    left=(video.width-target_w)//2
    top=(video.height-target_h)//2
    video=video.crop((left,top,left+target_w,top+target_h))
    card=Image.new('RGBA',(948,820),'#101822')
    d=ImageDraw.Draw(card)
    d.rounded_rectangle((0,0,947,819),radius=29,outline='#3b587d',width=2)
    d.rounded_rectangle((20,22,31,33),radius=5,fill=shot['color'])
    write_text(card,shot['label'],(49,19),25,'#d5e3f6',max_width=680,face=REGULAR)
    write_text(card,f'0{index+1}',(850,19),25,'#8096b5',face=MONO)
    card.paste(video,(20,65))
    d.line([(20,773),(927,773)],fill='#2b3e57',width=1)
    write_text(card,'NODEPILOT / IN ACTION',(25,788),16,'#8698b4',face=MONO)
    mask=Image.new('L',card.size,0)
    ImageDraw.Draw(mask).rounded_rectangle((0,0,947,819),radius=29,fill=255)
    card.putalpha(mask)
    # A restrained tilt catches the cut; the interface settles immediately.
    angle=(1 if index%2 else -1)*(.9*entrance+.14*math.sin(local*2))
    card=card.rotate(angle,Image.Resampling.BICUBIC,expand=True)
    return card,entrance

def render_frame(frame):
    t=frame/FPS
    image=BACKGROUND.copy().convert('RGBA')
    index=next((i for i,s in enumerate(SHOTS) if s['at']<=t<s['at']+s['length']),6)
    if index==5:
        image=Image.new('RGBA',(W,H),GREEN)
        ImageDraw.Draw(image).text((-20,1705),'NODEPILOT',font=font(188),fill='#b1ed63')
    common(image,t,index)
    if index<6:
        shot=SHOTS[index]; local=t-shot['at']
        # Headline remains readable from the first frame; movement settles in 120 ms.
        offset=round(24*math.exp(-local*25)) if frame else 0
        write_text(image,shot['a'],(80+offset,312),112,'#101c24' if index==5 else WHITE,max_width=900)
        write_text(image,shot['b'],(80-offset,446),126,'#101c24' if index==5 else shot['color'],max_width=900)
        card,entrance=footage_card(t,index,shot)
        x=(W-card.width)//2+round((1 if index%2 else -1)*44*entrance)
        y=636+round(18*entrance)
        shadow=Image.new('RGBA',(W,H),(0,0,0,0))
        ImageDraw.Draw(shadow).rounded_rectangle((65,651,1024,1477),radius=45,fill=(0,0,0,130))
        image=Image.alpha_composite(image,shadow.filter(ImageFilter.GaussianBlur(14)))
        image.alpha_composite(card,(x,y))
        write_text(image,shot['sub'],(80,1493),31,'#223e31' if index==5 else '#b8c9e2',max_width=900,face=REGULAR)
        if local<.15:
            layer=Image.new('RGBA',(W,H),(0,0,0,0))
            d=ImageDraw.Draw(layer)
            x=round(-280+1500*local/.15)
            d.polygon([(x,570),(x+35,570),(x+350,1490),(x+285,1490)],fill=(75,148,255,42))
            image=Image.alpha_composite(image,layer)
    else:
        local=t-6.5
        d=ImageDraw.Draw(image)
        # The original logo and a growing network close the feature montage.
        for j,(x,y) in enumerate([(865,380),(930,800),(840,1200),(600,1450)]):
            d.line([(150,630),(x,y)],fill='#27486d',width=2)
            r=12+3*math.sin(local*3+j)
            d.ellipse((x-r,y-r,x+r,y+r),outline=BLUE,width=3)
        write_text(image,'MEET YOUR NEXT WORKFLOW.',(80,312),33,'#9ab4d7',face=REGULAR)
        size=round(170+8*math.exp(-local*16))
        logo=LOGO.resize((size,size),Image.Resampling.LANCZOS)
        image.alpha_composite(logo,(80,468))
        write_text(image,'NodePilot',(71,693),166,WHITE,max_width=930)
        write_text(image,'WINDOWS. AUTOMATED.',(82,900),55,'#c9dcf6',max_width=910)
        d.rounded_rectangle((80,1011,485,1082),radius=14,fill=GREEN)
        write_text(image,'OPEN SOURCE',(104,1028),39,'#0c1618',max_width=365)
        write_text(image,'TRY IT ON GITHUB',(80,1205),56,WHITE,max_width=900)
        write_text(image,'Sev7eNup / NodePilot',(81,1292),39,BLUE,max_width=900,face=MONO)
        # Arrow resolves with the final beat, directing attention toward the CTA.
        x=850+int(8*math.sin(local*4))
        d.line([(x-70,1261),(x,1191)],fill=GREEN,width=9)
        d.line([(x-52,1191),(x,1191),(x,1243)],fill=GREEN,width=9)
    return image.convert('RGB')

def render_video():
    silent=OUT/'NodePilot-Short-8s-Silent.mp4'
    print('render-vertical-60fps',flush=True)
    with (RAW/'render-vertical.log').open('w',encoding='utf-8') as log:
        proc=subprocess.Popen([FFMPEG,'-hide_banner','-y','-f','rawvideo','-pixel_format','rgb24',
            '-video_size',f'{W}x{H}','-framerate',str(FPS),'-i','pipe:0','-an',
            '-c:v','libx264','-preset','slow','-crf','16','-threads','4','-pix_fmt','yuv420p',
            '-movflags','+faststart','-frames:v',str(FPS*SECONDS),str(silent)],stdin=subprocess.PIPE,stdout=log,stderr=log)
        try:
            for frame in range(FPS*SECONDS):
                image=render_frame(frame)
                proc.stdin.write(image.tobytes())
                if frame in [30,105,165,225,285,345,435]:
                    image.save(RAW/f'review-{frame:03d}.png')
                if frame%120==0: print(f'frames {frame}/{FPS*SECONDS}',flush=True)
        finally:
            proc.stdin.close()
        if proc.wait()!=0: raise RuntimeError('Vertical encoder failed')
    run(['-i',str(silent),'-i',str(RAW/'soundtrack.wav'),'-map','0:v:0','-map','1:a:0',
         '-c:v','copy','-af','loudnorm=I=-14:TP=-1.5:LRA=8,aresample=48000,atrim=duration=8',
         '-c:a','aac','-b:a','256k','-t','8','-movflags','+faststart',
         '-metadata','title=NodePilot — 8-second vertical short',
         '-metadata','comment=Approved product-tour footage. Fictional demo data. Original synthesized audio.',
         str(OUT/'NodePilot-Short-8s.mp4')],'mux-original-audio')

def verify():
    reports={}
    for name in ['NodePilot-Short-8s.mp4','NodePilot-Short-8s-Silent.mp4']:
        progress=RAW/f'{name}-timing.txt'
        run(['-v','error','-i',str(OUT/name),'-map','0:v:0','-progress',str(progress),'-f','null','-'],f'verify-{name}')
        assert not (RAW/f'verify-{name}.log').read_text().strip()
        values=dict(line.split('=',1) for line in progress.read_text().splitlines() if '=' in line)
        assert int(values['frame'])==480 and int(values['out_time_us'])==8000000,values
        reports[name]={'frames':480,'seconds':8,'bytes':(OUT/name).stat().st_size}
    run(['-v','error','-i',str(OUT/'NodePilot-Short-8s.mp4'),'-map','0:a:0','-f','null','-'],'verify-audio')
    assert not (RAW/'verify-audio.log').read_text().strip()
    preserved=json.loads((OUT/'preserved-versions.json').read_text())
    for path,digest in preserved.items():
        assert hashlib.sha256((ROOT/path).read_bytes()).hexdigest()==digest,path
    report={'width':W,'height':H,'fps':FPS,'durationSeconds':8,'codec':'H.264','pixelFormat':'yuv420p',
            'audio':'Original synthesized 120 BPM beat; AAC stereo 48 kHz',
            'source':str(SOURCE),'previousFilesUnchanged':len(preserved),'files':reports,
            'validation':'passed','shots':SHOTS}
    (OUT/'export.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='shots'}),flush=True)

def write_deliverables():
    render_frame(30).save(OUT/'NodePilot-Short-Poster.png')
    (OUT/'Ansehen.html').write_text('''<!doctype html><html lang="de"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>NodePilot · 8-Sekunden-Short</title><style>body{margin:32px auto;padding:0 20px;max-width:1050px;background:#070d16;color:#edf4ff;font:16px/1.6 system-ui}main{display:flex;gap:40px;align-items:flex-start}video{width:min(100%,380px);max-height:80vh;aspect-ratio:9/16;border:1px solid #3c5678;border-radius:20px}h1{line-height:1.2}a{color:#a8cdff}nav{display:grid;gap:14px}.muted{color:#95abc7}@media(max-width:700px){main{flex-direction:column}video{max-height:none}}</style><main><video controls playsinline loop preload="metadata" poster="NodePilot-Short-Poster.png"><source src="NodePilot-Short-8s.mp4" type="video/mp4"></video><section><h1>NodePilot in 8 Sekunden.</h1><p class="muted">1080 × 1920 · 9:16 · 60 fps<br>Englische Texte · Originaler Synth-Beat</p><nav><a href="NodePilot-Short-8s.mp4" download>Short mit Sound herunterladen</a><a href="NodePilot-Short-8s-Silent.mp4" download>Stumme Version herunterladen</a><a href="NodePilot-Short-Poster.png" download>Vorschaubild herunterladen</a><a href="../product-tour-liveops-v2/Ansehen.html">Bisherige vollständige Tour</a></nav><p class="muted">Wiedergabe starten, um den Sound zu hören.<br>Fiktive Demo-Daten aus der Produkttour.</p></section></main></html>''',encoding='utf-8')
    (OUT/'POST-TEXT.md').write_text('''Your scripts. Supercharged. ⚡

NodePilot: visual Windows automation, SCOrch imports, Live-Ops and AI Chat.
Open source. Self-hosted.

github.com/Sev7eNup/NodePilot

#PowerShell #WindowsAutomation #SysAdmin #OpenSource #NodePilot

---
8 seconds, 1080 × 1920, 60 fps. Real product-tour footage with fictional demo data.
The electronic beat and transition effects were synthesized for this clip; no sampled music.
The silent copy can be used with audio selected in the destination app.
''',encoding='utf-8')

if __name__=='__main__':
    prepare()
    if '--stills' in sys.argv:
        for i,s in enumerate(SHOTS):
            render_frame(round((s['at']+.35)*FPS)).save(RAW/f'design-{i}.png')
        render_frame(435).save(RAW/'design-6.png')
        print('Design stills ready.',flush=True)
    else:
        make_audio()
        render_video()
        verify()
        write_deliverables()
