"""Visual revision overlay: old and new revision drawn on top of each other.

PDF and DXF: both are rasterised in the same frame; ink only in the old revision is red, only in the new one green,
unchanged ink dark grey. DXF uses the drawing's own coordinates (no guessing); PDF pages are aligned with a small
shift search, because scanned or re-exported pages are often offset by a few pixels.
STEP: faces of both models are matched by surface type, area and centre; unmatched faces of the new model are green
(new or changed), unmatched faces of the old model red (removed), all others grey. Two shaded isometric views.
Every result carries the regions that changed so the app can list them and zoom in."""
import base64, io, math, os

INK = 200            # grey level below which a pixel counts as ink
GREY, RED, GREEN, WHITE = (70, 74, 82), (214, 40, 40), (16, 150, 64), (255, 255, 255)


# ---------------------------------------------------------------- raster helpers
def _png(rgb):
    from PIL import Image
    buf = io.BytesIO(); Image.fromarray(rgb, 'RGB').save(buf, format='PNG', optimize=True)
    return base64.b64encode(buf.getvalue()).decode('ascii')


def _dilate(mask, r):
    import numpy as np
    out = mask.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if dx == 0 and dy == 0: continue
            out |= np.roll(np.roll(mask, dy, 0), dx, 1)
    return out


def _regions(removed, added, cell=24, min_px=6):
    """Bounding boxes of changed areas: changed pixels are counted per grid cell, neighbouring cells are merged."""
    import numpy as np
    h, w = removed.shape
    gh, gw = (h + cell - 1) // cell, (w + cell - 1) // cell
    def grid(m):
        p = np.zeros((gh * cell, gw * cell), bool); p[:h, :w] = m
        return p.reshape(gh, cell, gw, cell).sum(axis=(1, 3))
    gr, ga = grid(removed), grid(added)
    hot = (gr + ga) >= min_px
    seen = np.zeros_like(hot); out = []
    for y in range(gh):
        for x in range(gw):
            if not hot[y, x] or seen[y, x]: continue
            stack = [(y, x)]; seen[y, x] = True; cells = []
            while stack:
                cy, cx = stack.pop(); cells.append((cy, cx))
                for ny in range(cy - 1, cy + 2):
                    for nx in range(cx - 1, cx + 2):
                        if 0 <= ny < gh and 0 <= nx < gw and hot[ny, nx] and not seen[ny, nx]:
                            seen[ny, nx] = True; stack.append((ny, nx))
            ys = [c[0] for c in cells]; xs = [c[1] for c in cells]
            r = int(sum(gr[c] for c in cells)); a = int(sum(ga[c] for c in cells))
            kind = 'changed' if r and a else ('removed' if r else 'added')
            out.append({'x': min(xs) * cell, 'y': min(ys) * cell, 'w': min(w, (max(xs) + 1) * cell) - min(xs) * cell,
                        'h': min(h, (max(ys) + 1) * cell) - min(ys) * cell, 'kind': kind, 'removed_px': r, 'added_px': a})
    out.sort(key=lambda b: -(b['removed_px'] + b['added_px']))
    return out[:200]


def _compose(old_ink, new_ink, tolerance=1):
    """Coloured overlay of two ink masks (same shape). Anti-aliasing noise is ignored with a 1 px tolerance."""
    import numpy as np
    removed = old_ink & ~_dilate(new_ink, tolerance)
    added = new_ink & ~_dilate(old_ink, tolerance)
    rgb = np.empty(old_ink.shape + (3,), np.uint8); rgb[:] = WHITE
    rgb[old_ink | new_ink] = GREY
    # differences are drawn one pixel wider so thin lines stay visible when zoomed out
    rgb[_dilate(removed, 1)] = RED
    rgb[_dilate(added, 1)] = GREEN
    rgb[removed] = RED; rgb[added] = GREEN
    return rgb, removed, added


def _pad(a, h, w, fill):
    import numpy as np
    out = np.full((h, w), fill, a.dtype); out[:a.shape[0], :a.shape[1]] = a
    return out


def _best_shift(old_ink, new_ink, limit=24):
    """Integer (dy, dx) that moves the new page onto the old one, found on a quarter-size copy then refined."""
    import numpy as np
    def small(m, f):
        h, w = m.shape[0] // f * f, m.shape[1] // f * f
        return m[:h, :w].reshape(h // f, f, w // f, f).any(axis=(1, 3))
    def search(a, b, lim, centre=(0, 0)):
        best, score = centre, -1
        for dy in range(centre[0] - lim, centre[0] + lim + 1):
            for dx in range(centre[1] - lim, centre[1] + lim + 1):
                s = int((a & np.roll(np.roll(b, dy, 0), dx, 1)).sum())
                if s > score: best, score = (dy, dx), s
        return best
    f = 4
    dy, dx = search(small(old_ink, f), small(new_ink, f), max(1, limit // f))
    return search(old_ink, new_ink, 2, (dy * f, dx * f))


def _result(rgb, removed, added, extra):
    regions = _regions(removed, added)
    return {'image': _png(rgb), 'width': int(rgb.shape[1]), 'height': int(rgb.shape[0]),
            'removed_px': int(removed.sum()), 'added_px': int(added.sum()), 'regions': regions,
            'identical': not regions, **extra}


# ---------------------------------------------------------------- PDF
def pdf(a):
    import numpy as np, pypdfium2 as pdfium
    old_doc = pdfium.PdfDocument(os.path.abspath(a['old'])); new_doc = pdfium.PdfDocument(os.path.abspath(a['new']))
    try:
        pages = min(len(old_doc), len(new_doc))
        index = int(a.get('page', 0))
        if not 0 <= index < pages: raise ValueError('Sayfa numarası geçersiz.')
        po, pn = old_doc[index], new_doc[index]
        target = float(a.get('size', 2200))
        scale = min(4.0, target / max(max(po.get_size()), max(pn.get_size())))  # same scale for both pages
        def gray(page):
            bmp = page.render(scale=scale, grayscale=True); pil = bmp.to_pil().convert('L')
            arr = np.asarray(pil).copy(); pil.close(); bmp.close(); page.close()
            return arr
        go, gn = gray(po), gray(pn)
    finally:
        old_doc.close(); new_doc.close()
    h, w = max(go.shape[0], gn.shape[0]), max(go.shape[1], gn.shape[1])
    oi = _pad(go, h, w, 255) < INK; ni = _pad(gn, h, w, 255) < INK
    dy, dx = _best_shift(oi, ni) if a.get('align', True) else (0, 0)
    if dy or dx: ni = np.roll(np.roll(ni, dy, 0), dx, 1)
    rgb, removed, added = _compose(oi, ni)
    return _result(rgb, removed, added, {'kind': 'pdf', 'page': index, 'pages': pages,
                                        'old_pages': None, 'shift_px': [int(dx), int(dy)], 'scale': scale})


# ---------------------------------------------------------------- DXF / DWG
def _dxf_paths(path):
    """Polylines and filled polygons of the model space in drawing units (text is drawn as outlines)."""
    import ezdxf
    from ezdxf import recover
    from ezdxf.addons.drawing import RenderContext, Frontend
    from ezdxf.addons.drawing.recorder import Recorder, PointsRecord, SolidLinesRecord, PathRecord, FilledPathsRecord
    from ezdxf.addons.drawing.config import Configuration, BackgroundPolicy, ColorPolicy, LinePolicy
    load = path
    if path.lower().endswith('.dwg'):
        import cad2d
        load, _ = cad2d.dwg_to_dxf(path)
    try: doc = ezdxf.readfile(load)
    except Exception: doc, _ = recover.readfile(load)
    rec = Recorder()
    cfg = Configuration(background_policy=BackgroundPolicy.WHITE, color_policy=ColorPolicy.BLACK, line_policy=LinePolicy.ACCURATE)
    Frontend(RenderContext(doc), rec, config=cfg).draw_layout(doc.modelspace(), finalize=True)
    player = rec.player(); box = player.bbox()
    if not box.has_data: return [], [], None
    tol = max(box.size.x, box.size.y, 1e-9) / 20000.0
    lines, fills = [], []
    def flat(p): return [(v.x, v.y) for v in p.flattening(tol, segments=4)]
    for record, _ in player.recordings():
        if isinstance(record, SolidLinesRecord):
            pts = list(record.lines.vertices() if hasattr(record.lines, 'vertices') else record.lines)
            for i in range(0, len(pts) - 1, 2): lines.append([(pts[i].x, pts[i].y), (pts[i + 1].x, pts[i + 1].y)])
        elif isinstance(record, PathRecord):
            for sub in record.path.sub_paths():
                f = flat(sub)
                if len(f) >= 2: lines.append(f)
        elif isinstance(record, FilledPathsRecord):
            for p in record.paths:
                for sub in p.sub_paths():
                    f = flat(sub)
                    if len(f) >= 3: fills.append(f)
        elif isinstance(record, PointsRecord):
            pts = list(record.points.vertices() if hasattr(record.points, 'vertices') else record.points)
            if len(pts) > 1: lines.append([(v.x, v.y) for v in pts])
    return lines, fills, (box.extmin.x, box.extmin.y, box.extmax.x, box.extmax.y)


def dxf(a):
    import numpy as np
    from PIL import Image, ImageDraw
    lo, fo, bo = _dxf_paths(os.path.abspath(a['old'])); ln, fn, bn = _dxf_paths(os.path.abspath(a['new']))
    boxes = [b for b in (bo, bn) if b]
    if not boxes: raise ValueError('Çizimlerde çizilecek öğe yok.')
    x0 = min(b[0] for b in boxes); y0 = min(b[1] for b in boxes); x1 = max(b[2] for b in boxes); y1 = max(b[3] for b in boxes)
    size = float(a.get('size', 2400)); margin = 24
    s = (size - 2 * margin) / max(x1 - x0, y1 - y0, 1e-9)
    w, h = int((x1 - x0) * s) + 2 * margin, int((y1 - y0) * s) + 2 * margin
    def raster(lines, fills):
        img = Image.new('L', (w, h), 255); d = ImageDraw.Draw(img)
        P = lambda p: (margin + (p[0] - x0) * s, h - margin - (p[1] - y0) * s)
        for poly in fills: d.polygon([P(p) for p in poly], fill=0)
        for poly in lines: d.line([P(p) for p in poly], fill=0, width=2)
        return np.asarray(img) < INK
    rgb, removed, added = _compose(raster(lo, fo), raster(ln, fn))
    return _result(rgb, removed, added, {'kind': 'dxf', 'bbox': [x0, y0, x1, y1], 'scale': s, 'margin': margin})


# ---------------------------------------------------------------- STEP
def _faces(path):
    from OCP.STEPCAFControl import STEPCAFControl_Reader
    from OCP.TDocStd import TDocStd_Document
    from OCP.TCollection import TCollection_ExtendedString
    from OCP.Interface import Interface_Static
    from OCP.IFSelect import IFSelect_RetDone
    from OCP.TopExp import TopExp_Explorer
    from OCP.TopAbs import TopAbs_FACE, TopAbs_REVERSED
    from OCP.TopoDS import TopoDS
    from OCP.BRepAdaptor import BRepAdaptor_Surface
    from OCP.GProp import GProp_GProps
    from OCP.BRepGProp import BRepGProp
    from OCP.BRepMesh import BRepMesh_IncrementalMesh
    from OCP.BRep import BRep_Tool
    from OCP.TopLoc import TopLoc_Location
    from cad_document import read_assembly, compound
    reader = STEPCAFControl_Reader(); reader.SetNameMode(True)
    Interface_Static.SetCVal_s('xstep.cascade.unit', 'MM')
    if reader.ReadFile(os.path.abspath(path)) != IFSelect_RetDone: raise ValueError('STEP dosyası okunamadı.')
    doc = TDocStd_Document(TCollection_ExtendedString('MailStudio'))
    if not reader.Transfer(doc): raise ValueError('STEP geometrisi aktarılamadı.')
    _, bodies = read_assembly(doc)
    if not bodies: raise ValueError('STEP dosyasında geometri yok.')
    shape = compound([b['shape'] for b in bodies])
    from cad_analysis import bbox
    b = bbox(shape); diag = math.dist(b[:3], b[3:]) or 1.0
    BRepMesh_IncrementalMesh(shape, diag / 400.0, False, 0.35, True)
    faces = []
    ex = TopExp_Explorer(shape, TopAbs_FACE)
    while ex.More():
        f = TopoDS.Face_s(ex.Current()); ex.Next()
        kind = str(BRepAdaptor_Surface(f).GetType()).split('_')[-1].split('.')[-1]
        g = GProp_GProps(); BRepGProp.SurfaceProperties_s(f, g); c = g.CentreOfMass()
        loc = TopLoc_Location(); tri = BRep_Tool.Triangulation_s(f, loc)
        tris = []
        if tri is not None:
            t = loc.Transformation(); rev = f.Orientation() == TopAbs_REVERSED
            pts = [tri.Node(i).Transformed(t) for i in range(1, tri.NbNodes() + 1)]
            pts = [(p.X(), p.Y(), p.Z()) for p in pts]
            for i in range(1, tri.NbTriangles() + 1):
                n1, n2, n3 = tri.Triangle(i).Get()
                if rev: n2, n3 = n3, n2
                tris.append((pts[n1 - 1], pts[n2 - 1], pts[n3 - 1]))
        faces.append({'type': kind, 'area': g.Mass(), 'center': (c.X(), c.Y(), c.Z()), 'tris': tris})
    return faces, b, diag


def _match(old, new, diag):
    """Pairs faces that are geometrically the same; returns the unmatched index sets."""
    tol = max(0.02, diag * 1e-4)
    used = set(); new_left = set()
    by_type = {}
    for i, f in enumerate(old): by_type.setdefault(f['type'], []).append(i)
    for j, f in enumerate(new):
        hit = None
        for i in by_type.get(f['type'], []):
            if i in used: continue
            o = old[i]
            if abs(o['area'] - f['area']) <= max(0.01, 0.002 * f['area']) and math.dist(o['center'], f['center']) <= tol:
                hit = i; break
        if hit is None: new_left.add(j)
        else: used.add(hit)
    return set(range(len(old))) - used, new_left


def _shade(tris_colors, box, size, direction, xray=()):
    """Small z-buffer rasteriser: flat shaded triangles seen from <direction> (orthographic)."""
    import numpy as np
    d = np.array(direction, float); d /= np.linalg.norm(d)
    up = np.array([0, 0, 1.0]); right = np.cross(up, d); right /= np.linalg.norm(right); up = np.cross(d, right)
    light = d * 0.6 + up * 0.5 + right * 0.3; light /= np.linalg.norm(light)
    corners = np.array([[x, y, z] for x in (box[0], box[3]) for y in (box[1], box[4]) for z in (box[2], box[5])])
    u, v = corners @ right, corners @ up
    margin = 16; s = (size - 2 * margin) / max(u.max() - u.min(), v.max() - v.min(), 1e-9)
    w = int((u.max() - u.min()) * s) + 2 * margin; h = int((v.max() - v.min()) * s) + 2 * margin
    img = np.full((h, w, 3), 255, np.uint8); zbuf = np.full((h, w), -np.inf); fid = np.full((h, w), -1, np.int64)
    for k, (tri, col) in enumerate(tris_colors):
        p = np.array(tri, float)
        n = np.cross(p[1] - p[0], p[2] - p[0]); nl = np.linalg.norm(n)
        if nl == 0: continue
        n /= nl
        px = margin + (p @ right - u.min()) * s; py = h - margin - (p @ up - v.min()) * s; pz = p @ d
        xmin, xmax = max(int(px.min()), 0), min(int(math.ceil(px.max())), w - 1)
        ymin, ymax = max(int(py.min()), 0), min(int(math.ceil(py.max())), h - 1)
        if xmin > xmax or ymin > ymax: continue
        X, Y = np.meshgrid(np.arange(xmin, xmax + 1) + 0.5, np.arange(ymin, ymax + 1) + 0.5)
        den = (py[1] - py[2]) * (px[0] - px[2]) + (px[2] - px[1]) * (py[0] - py[2])
        if abs(den) < 1e-12: continue
        a = ((py[1] - py[2]) * (X - px[2]) + (px[2] - px[1]) * (Y - py[2])) / den
        b = ((py[2] - py[0]) * (X - px[2]) + (px[0] - px[2]) * (Y - py[2])) / den
        c = 1 - a - b
        inside = (a >= -1e-6) & (b >= -1e-6) & (c >= -1e-6)
        if not inside.any(): continue
        z = a * pz[0] + b * pz[1] + c * pz[2]
        sub = zbuf[ymin:ymax + 1, xmin:xmax + 1]
        win = inside & (z > sub)
        if not win.any(): continue
        sub[win] = z[win]
        shade = 0.35 + 0.65 * abs(float(n @ light))
        img[ymin:ymax + 1, xmin:xmax + 1][win] = (np.array(col[:3], float) * shade).clip(0, 255).astype(np.uint8)
        fid[ymin:ymax + 1, xmin:xmax + 1][win] = col[3] if len(col) > 3 else k
    # removed faces are usually inside or behind the new geometry: drawn see-through on top (x-ray), never hidden
    if xray:
        cover = np.zeros((h, w), bool)
        for tri in xray:
            p = np.array(tri, float)
            px = margin + (p @ right - u.min()) * s; py = h - margin - (p @ up - v.min()) * s
            xmin, xmax = max(int(px.min()), 0), min(int(math.ceil(px.max())), w - 1)
            ymin, ymax = max(int(py.min()), 0), min(int(math.ceil(py.max())), h - 1)
            if xmin > xmax or ymin > ymax: continue
            den = (py[1] - py[2]) * (px[0] - px[2]) + (px[2] - px[1]) * (py[0] - py[2])
            if abs(den) < 1e-12: continue
            X, Y = np.meshgrid(np.arange(xmin, xmax + 1) + 0.5, np.arange(ymin, ymax + 1) + 0.5)
            a = ((py[1] - py[2]) * (X - px[2]) + (px[2] - px[1]) * (Y - py[2])) / den
            b = ((py[2] - py[0]) * (X - px[2]) + (px[0] - px[2]) * (Y - py[2])) / den
            cover[ymin:ymax + 1, xmin:xmax + 1] |= (a >= -1e-6) & (b >= -1e-6) & (1 - a - b >= -1e-6)
        img[cover] = (img[cover] * 0.75 + np.array(RED) * 0.25).astype(np.uint8)
        rim = cover & ~(np.roll(cover, 1, 0) & np.roll(cover, -1, 0) & np.roll(cover, 1, 1) & np.roll(cover, -1, 1))
        img[_dilate(rim, 1)] = RED
    # thin outline between faces of different colour / background for readability
    edge = np.zeros((h, w), bool)
    edge[:, 1:] |= fid[:, 1:] != fid[:, :-1]; edge[1:, :] |= fid[1:, :] != fid[:-1, :]
    img[edge & (fid >= 0)] = (img[edge & (fid >= 0)] * 0.55).astype(np.uint8)
    return img


def step(a):
    import numpy as np
    old, bo, do = _faces(a['old']); new, bn, dn = _faces(a['new'])
    removed, added = _match(old, new, max(do, dn))
    box = [min(bo[i], bn[i]) for i in range(3)] + [max(bo[i], bn[i]) for i in range(3, 6)]
    tris = []
    for j, f in enumerate(new):
        col = (GREEN if j in added else (196, 200, 206)) + (j,)
        tris += [(t, col) for t in f['tris']]
    xray = [t for i in removed for t in old[i]['tris']]
    size = int(a.get('size', 900))
    views = [_shade(tris, box, size, (1, -1, 0.8), xray), _shade(tris, box, size, (-1, 1, 0.8), xray)]
    h = max(v.shape[0] for v in views); gap = 12
    rgb = np.full((h, sum(v.shape[1] for v in views) + gap, 3), 255, np.uint8)
    x = 0
    for v in views: rgb[:v.shape[0], x:x + v.shape[1]] = v; x += v.shape[1] + gap
    def describe(f, state):
        return {'state': state, 'type': f['type'], 'area_mm2': round(f['area'], 3), 'center_mm': [round(c, 3) for c in f['center']]}
    changes = [describe(new[j], 'added') for j in sorted(added)] + [describe(old[i], 'removed') for i in sorted(removed)]
    return {'image': _png(rgb), 'width': int(rgb.shape[1]), 'height': int(rgb.shape[0]), 'kind': 'step',
            'faces_old': len(old), 'faces_new': len(new), 'added_faces': len(added), 'removed_faces': len(removed),
            'changes': changes[:300], 'identical': not added and not removed, 'regions': []}


def dispatch(op, a):
    if op == 'visual_diff':
        ext = os.path.splitext(a['new'])[1].lower()
        if ext == '.pdf': return pdf(a)
        if ext in ('.dxf', '.dwg'): return dxf(a)
        if ext in ('.step', '.stp'): return step(a)
        raise ValueError('Bu dosya türü için görsel fark yok.')
    return None
