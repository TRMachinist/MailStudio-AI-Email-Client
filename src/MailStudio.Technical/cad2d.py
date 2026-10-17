"""2D CAD viewing: DXF (ezdxf, MIT) and DWG (converted to DXF first: ODA File Converter if installed, otherwise the bundled
LibreDWG dwg2dxf). ezdxf's drawing frontend resolves dimensions, hatches, blocks, text, linetypes and colors; the records
are grouped by layer/color/lineweight and drawn as vectors in WPF (unlimited zoom, layer toggles, measuring)."""
import os, sys, glob, subprocess, tempfile, shutil, math

doc = None; src_path = None; converted_from = None; texts_cache = {}

def _base_dir():
    return os.path.dirname(sys.executable) if getattr(sys, 'frozen', False) else os.path.dirname(os.path.abspath(__file__))

def _oda():
    for root in (os.environ.get('ProgramFiles', r'C:\Program Files'), os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)')):
        hits = sorted(glob.glob(os.path.join(root, 'ODA', 'ODAFileConverter*', 'ODAFileConverter.exe')))
        if hits: return hits[-1]
    return None

def _libredwg():
    for cand in (os.path.join(_base_dir(), 'libredwg', 'dwg2dxf.exe'), os.path.join(_base_dir(), '..', 'libredwg', 'dwg2dxf.exe'),
                 os.environ.get('MAILSTUDIO_DWG2DXF', '')):
        if cand and os.path.exists(cand): return os.path.abspath(cand)
    return None

def converters():
    return {'oda': _oda(), 'libredwg': _libredwg()}

def dwg_to_dxf(path):
    out_dir = tempfile.mkdtemp(prefix='ms-dwg-')
    name = os.path.splitext(os.path.basename(path))[0]
    errors = []
    oda = _oda()
    if oda:
        in_dir = tempfile.mkdtemp(prefix='ms-dwgin-'); shutil.copy2(path, os.path.join(in_dir, os.path.basename(path)))
        try:
            subprocess.run([oda, in_dir, out_dir, 'ACAD2018', 'DXF', '0', '1', os.path.basename(path)], timeout=180,
                           capture_output=True, creationflags=0x08000000)
            hits = glob.glob(os.path.join(out_dir, '*.dxf'))
            if hits: return hits[0], 'ODA File Converter'
            errors.append('ODA File Converter çıktı üretmedi')
        except Exception as ex: errors.append('ODA: ' + str(ex))
        finally: shutil.rmtree(in_dir, ignore_errors=True)
    lib = _libredwg()
    if lib:
        dest = os.path.join(out_dir, name + '.dxf')
        try:
            r = subprocess.run([lib, '-y', '-o', dest, path], timeout=180, capture_output=True, cwd=os.path.dirname(lib), creationflags=0x08000000)
            if os.path.exists(dest) and os.path.getsize(dest) > 0: return dest, 'LibreDWG'
            errors.append('LibreDWG: ' + (r.stderr or b'').decode('utf-8', 'replace')[-300:])
        except Exception as ex: errors.append('LibreDWG: ' + str(ex))
    if not oda and not lib: raise ValueError('DWG dönüştürücü bulunamadı (LibreDWG paketi eksik; ODA File Converter da kurulu değil).')
    raise ValueError('DWG okunamadı. ' + ' | '.join(errors))

def _fix_fonts():
    """If the text style font lacks letters used in the drawing (e.g. Ş, ğ, İ), Arial is used in memory only; the file does not change."""
    try:
        from ezdxf.fonts import fonts
        used = set()
        for e in doc.modelspace().query('TEXT MTEXT ATTRIB'):
            t = e.dxf.text if e.dxftype() != 'MTEXT' else e.text
            used.update(ch for ch in (t or '') if ord(ch) > 127 and not ch.isspace())
        for b in doc.blocks:
            for e in b.query('TEXT MTEXT ATTDEF'):
                t = e.dxf.text if e.dxftype() != 'MTEXT' else e.text
                used.update(ch for ch in (t or '') if ord(ch) > 127 and not ch.isspace())
        if not used: return
        for style in doc.styles:
            name = style.dxf.get('font', '') or 'txt'
            try:
                cmap = fonts.make_font(name, 1.0).glyph_cache.cmap
                if all(ord(ch) in cmap for ch in used): continue
            except Exception: pass
            style.dxf.font = 'arial.ttf'
    except Exception: pass

def _units(d):
    u = d.header.get('$INSUNITS', 0)
    return {0: '', 1: 'in', 2: 'ft', 4: 'mm', 5: 'cm', 6: 'm', 14: 'dm'}.get(u, '')

def open_file(a):
    global doc, src_path, converted_from, texts_cache
    import ezdxf
    from ezdxf import recover
    path = os.path.abspath(a['path']); src_path = path; converted_from = None; texts_cache = {}
    if path.lower().endswith('.dwg'):
        dxf, how = dwg_to_dxf(path); converted_from = how; load = dxf
    else: load = path
    # Converters may write UTF-8 bytes into old DXF versions; if the file is valid UTF-8 it is read that way for Turkish characters.
    enc = None
    try:
        raw = open(load, 'rb').read()
        if any(b > 127 for b in raw):
            raw.decode('utf-8'); enc = 'utf-8'
    except UnicodeDecodeError: enc = None
    try: doc = ezdxf.readfile(load, encoding=enc) if enc else ezdxf.readfile(load)
    except Exception:
        doc, auditor = recover.readfile(load)
    _fix_fonts()
    layouts = ['Model'] + [l.name for l in doc.layouts if l.name != 'Model']
    layers = []
    for layer in doc.layers:
        try: color = layer.rgb or None
        except Exception: color = None
        hexc = '#%02X%02X%02X' % color if color else _aci_hex(layer.color)
        layers.append({'name': layer.dxf.name, 'color': hexc, 'on': layer.is_on() and not layer.is_frozen()})
    return {'layouts': layouts, 'layers': layers, 'units': _units(doc), 'version': doc.dxfversion, 'converted': converted_from,
            'entities': len(doc.modelspace())}

def _aci_hex(aci):
    try:
        from ezdxf.colors import aci2rgb
        r, g, b = aci2rgb(abs(int(aci)) or 7)
        return '#%02X%02X%02X' % (r, g, b)
    except Exception: return '#000000'

def render(a):
    if doc is None: raise ValueError('Önce çizimi açın.')
    from ezdxf.addons.drawing import RenderContext, Frontend
    from ezdxf.addons.drawing.recorder import Recorder, PointsRecord, SolidLinesRecord, PathRecord, FilledPathsRecord
    from ezdxf.addons.drawing.config import Configuration, BackgroundPolicy, ColorPolicy, LinePolicy
    name = a.get('layout') or 'Model'
    layout = doc.layouts.get(name) if name != 'Model' else doc.modelspace()
    ctx = RenderContext(doc)
    rec = Recorder()
    cfg = Configuration(background_policy=BackgroundPolicy.WHITE, color_policy=ColorPolicy.COLOR, line_policy=LinePolicy.ACCURATE)
    Frontend(ctx, rec, config=cfg).draw_layout(layout, finalize=True)
    player = rec.player()
    bbox = player.bbox()
    if not bbox.has_data: return {'layout': name, 'bbox': [0, 0, 100, 100], 'groups': [], 'count': 0}
    ext = max(bbox.size.x, bbox.size.y, 1e-9)
    tol = ext / 40000.0
    groups = {}; count = 0
    def grp(props):
        key = (props.layer or '0', props.color[:7] if props.color else '#000000', round(float(props.lineweight or 0), 2))
        g = groups.get(key)
        if g is None: g = groups[key] = {'layer': key[0], 'color': key[1], 'lw': key[2], 'lines': [], 'fills': [], 'points': []}
        return g
    def flat(path):
        pts = list(path.flattening(tol, segments=4))
        return [c for p in pts for c in (round(p.x, 6), round(p.y, 6))]
    def area(poly):
        s = 0.0
        for i in range(0, len(poly) - 2, 2): s += poly[i] * poly[i + 3] - poly[i + 2] * poly[i + 1]
        return s / 2
    for record, props in player.recordings():
        g = grp(props); count += 1
        if isinstance(record, SolidLinesRecord):
            pts = record.lines.vertices() if hasattr(record.lines, 'vertices') else record.lines
            arr = [v for v in pts]
            for i in range(0, len(arr) - 1, 2):
                g['lines'].append([round(arr[i].x, 6), round(arr[i].y, 6), round(arr[i + 1].x, 6), round(arr[i + 1].y, 6)])
        elif isinstance(record, PathRecord):
            for sub in record.path.sub_paths():
                f = flat(sub)
                if len(f) >= 4: g['lines'].append(f)
        elif isinstance(record, FilledPathsRecord):
            polys = []
            for path in record.paths:
                for sub in path.sub_paths():
                    f = flat(sub)
                    if len(f) >= 6: polys.append(f)
            if not polys: continue
            g['fills'].append(polys)
        elif isinstance(record, PointsRecord):
            # The recorder stores a single point and lines/polylines with the same record type: 1 point = point, more = line.
            pts = list(record.points.vertices() if hasattr(record.points, 'vertices') else record.points)
            if len(pts) == 1: g['points'].extend([round(pts[0].x, 6), round(pts[0].y, 6)])
            elif len(pts) > 1: g['lines'].append([c for v in pts for c in (round(v.x, 6), round(v.y, 6))])
    return {'layout': name, 'bbox': [bbox.extmin.x, bbox.extmin.y, bbox.extmax.x, bbox.extmax.y], 'groups': list(groups.values()),
            'count': count, 'texts': texts(name)}

def texts(name):
    if name in texts_cache: return texts_cache[name]
    layout = doc.layouts.get(name) if name != 'Model' else doc.modelspace()
    out = []
    def add(e, text):
        text = (text or '').strip()
        if not text: return
        try: ins = e.dxf.insert
        except Exception: return
        h = getattr(e.dxf, 'height', None) or getattr(e.dxf, 'char_height', None) or 2.5
        out.append({'text': text, 'x': round(ins.x, 4), 'y': round(ins.y, 4), 'h': round(float(h), 4), 'layer': e.dxf.layer})
    def walk(entities, depth=0):
        for e in entities:
            t = e.dxftype()
            if t == 'TEXT' or t == 'ATTRIB': add(e, e.dxf.text)
            elif t == 'MTEXT':
                try: add(e, e.plain_text())
                except Exception: add(e, e.text)
            elif t == 'INSERT':
                for att in getattr(e, 'attribs', []): add(att, att.dxf.text)
                if depth < 3:
                    try: walk(e.virtual_entities(), depth + 1)
                    except Exception: pass
            elif t == 'DIMENSION':
                try:
                    m = e.get_measurement(); txt = e.dxf.text
                    val = txt.replace('<>', f'{m:g}') if txt and txt not in ('', '<>') else (f'{m:g}' if isinstance(m, (int, float)) else '')
                    p = e.dxf.text_midpoint if e.dxf.hasattr('text_midpoint') else e.dxf.defpoint
                    if val: out.append({'text': val, 'x': round(p.x, 4), 'y': round(p.y, 4), 'h': 2.5, 'layer': e.dxf.layer, 'dim': True})
                except Exception: pass
            if len(out) > 20000: return
    walk(layout)
    texts_cache[name] = out
    return out

def dispatch(op, a):
    if op == 'cad2d_open': return open_file(a)
    if op == 'cad2d_render': return render(a)
    if op == 'cad2d_converters': return converters()
    return None
