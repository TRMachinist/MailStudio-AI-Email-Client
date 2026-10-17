"""Turns the showcase frames (MailStudio.exe --demo --showcase <dir>) into README media.
Usage: python make_gifs.py <showcase-dir> <media-dir> [suffix]"""
import os, sys, glob
from PIL import Image

src, dst = sys.argv[1], sys.argv[2]
suffix = sys.argv[3] if len(sys.argv) > 3 else ''
os.makedirs(dst, exist_ok=True)

GIFS = {  # name: (frame duration ms, width, extra hold on last frame ms)
    'tour': (700, 960, 600),
    'ai-summary': (170, 960, 1800),
    'pdf-zoom': (180, 880, 600),
    'dxf-zoom': (170, 880, 600),
    'step-orbit': (110, 880, 0),
}


def fit(img, width):
    if img.width <= width: return img
    return img.resize((width, round(img.height * width / img.width)), Image.LANCZOS)


for name, (ms, width, hold) in GIFS.items():
    files = sorted(glob.glob(os.path.join(src, name, 'f*.png')))
    if not files: print('missing', name); continue
    frames = [fit(Image.open(f).convert('RGB'), width) for f in files]
    if name == 'tour':  # one frame per selected message (every 4th capture is the settled view)
        frames = frames[3::4] or frames
    # one shared palette keeps colours stable and the file small
    pal = frames[len(frames) // 2].quantize(colors=200, method=Image.MEDIANCUT, dither=Image.NONE)
    q = [f.quantize(palette=pal, dither=Image.NONE) for f in frames]
    durations = [ms] * len(q); durations[-1] += hold
    out = os.path.join(dst, f'{name}{suffix}.gif')
    q[0].save(out, save_all=True, append_images=q[1:], duration=durations, loop=0, optimize=True, disposal=1)
    print(f'{out}  {len(q)} frames  {os.path.getsize(out) / 1e6:.2f} MB')

for f in glob.glob(os.path.join(src, '*.png')):
    img = fit(Image.open(f).convert('RGB'), 1440)
    out = os.path.join(dst, os.path.basename(f).replace('.png', f'{suffix}.png'))
    img.save(out, optimize=True)
    print(f'{out}  {os.path.getsize(out) / 1e3:.0f} KB')
