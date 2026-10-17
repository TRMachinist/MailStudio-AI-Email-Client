"""Lightweight PDF studio: page rendering, word/object boxes, OCR boxes, search and writing edits to a copy.

Coordinate convention ("view point"): the page's own /Rotate value is ignored (unrotated page),
origin at the top-left of the crop box, unit PDF point (1/72 inch), y grows downwards.
The UI applies rotation to the view only; on save the page's /Rotate value is updated.
"""
import os, io, base64, ctypes, math, tempfile
import pypdfium2 as pdfium
import pypdfium2.raw as R

doc = None; path = None; original_rotation = []; ocr_engine = None; _keep = []
OBJ_TYPES = {1: 'text', 2: 'path', 3: 'image', 4: 'shading', 5: 'form'}
WS = set(' \t\r\n\u00a0\u3000')


def _need():
    if doc is None: raise ValueError('Önce PDF açın.')


def _page(i):
    _need(); i = int(i)
    if not 0 <= i < len(doc): raise ValueError('Sayfa numarası geçersiz.')
    return doc[i]


def _box(page):
    l, b, r, t = page.get_cropbox()
    return l, b, r, t


def _to_view(page_box, l, b, r, t):
    cl, cb, cr, ct = page_box
    return {'x': round(l - cl, 3), 'y': round(ct - t, 3), 'w': round(r - l, 3), 'h': round(t - b, 3)}


def open_doc(p, password=None):
    global doc, path, original_rotation
    if doc is not None:
        try: doc.close()
        except Exception: pass
    doc = pdfium.PdfDocument(os.path.abspath(p), password=password); path = os.path.abspath(p)
    pages = []; original_rotation = []
    for i in range(len(doc)):
        page = doc[i]; rot = page.get_rotation(); original_rotation.append(rot)
        if rot: page.set_rotation(0)  # the view is computed on the unrotated page
        l, b, r, t = _box(page)
        pages.append({'w': round(r - l, 3), 'h': round(t - b, 3), 'rotation': rot})
        page.close()
    meta = {}
    try: meta = {k: v for k, v in doc.get_metadata_dict().items() if v}
    except Exception: pass
    return {'pages': len(pages), 'sizes': pages, 'metadata': meta}


def render(a):
    """Renders the page. If 'px' is given the result has exactly that pixel size: first it is drawn 'ss' times larger, then reduced
    with Lanczos (supersampling). So it is shown 1:1 on screen and thin lines and small text stay sharp."""
    page = _page(a.get('page', 0))
    w, h = page.get_size()
    px = a.get('px')
    if px:
        tw, th = max(1, int(px[0])), max(1, int(px[1]))
        ss = max(1.0, float(a.get('ss', 2.0)))
        ss = min(ss, 8000.0 / max(tw, th))
        scale = tw / w * max(1.0, ss)
        bitmap = page.render(scale=scale, draw_annots=True, may_draw_forms=True)
        pil = bitmap.to_pil()
        if pil.width != tw or pil.height != th:
            from PIL import Image
            big = pil; pil = big.resize((tw, th), Image.LANCZOS); big.close()
        eff = tw / w
    else:
        scale = float(a.get('scale', 1.3333))
        limit = float(a.get('max', 5200)); scale = max(0.05, min(scale, limit / max(w, h)))
        bitmap = page.render(scale=scale, draw_annots=True, may_draw_forms=True)
        pil = bitmap.to_pil(); eff = scale
    buf = io.BytesIO()
    if a.get('format') == 'jpeg': pil.convert('RGB').save(buf, format='JPEG', quality=86)
    else: pil.save(buf, format='PNG', compress_level=1)
    out = {'page': int(a.get('page', 0)), 'scale': eff, 'px': [pil.width, pil.height], 'image': base64.b64encode(buf.getvalue()).decode('ascii')}
    pil.close(); bitmap.close(); page.close()
    return out


def words(a):
    """Words of the text layer, in reading order, with view-point boxes."""
    page = _page(a.get('page', 0)); pb = _box(page); tp = page.get_textpage()
    n = tp.count_chars(); out = []; cur = None; last = None
    def flush():
        nonlocal cur
        if cur and cur['t'].strip():
            v = _to_view(pb, cur['l'], cur['b'], cur['r'], cur['tp'])
            v.update({'text': cur['t'], 'nl': cur['nl']}); out.append(v)
        cur = None
    newline_pending = False
    for i in range(n):
        ch = chr(R.FPDFText_GetUnicode(tp.raw, i) or 32)
        if ch in WS:
            flush(); newline_pending = newline_pending or ch in '\r\n'; continue
        l, b, r, t = tp.get_charbox(i, loose=False)
        if r - l <= 0 and t - b <= 0: continue
        if ch == '\xad': ch = '-'
        h = max(t - b, 1)
        if cur is not None and last is not None:
            # line change or wide gap → new word (the word height is the reference for small characters)
            ref = max(h, cur['tp'] - cur['b'], 2)
            if abs(((b + t) / 2) - ((cur['b'] + cur['tp']) / 2)) > ref * 0.75 or l - last[2] > ref * 0.45 or l < last[0] - ref:
                flush()
        if cur is None:
            cur = {'t': '', 'l': l, 'b': b, 'r': r, 'tp': t, 'nl': newline_pending}; newline_pending = False
        cur['t'] += ch; cur['l'] = min(cur['l'], l); cur['b'] = min(cur['b'], b); cur['r'] = max(cur['r'], r); cur['tp'] = max(cur['tp'], t)
        last = (l, b, r, t)
    flush(); tp.close(); page.close()
    return {'page': int(a.get('page', 0)), 'words': out, 'source': 'text'}


def objects(a):
    page = _page(a.get('page', 0)); pb = _box(page); out = []
    count = R.FPDFPage_CountObjects(page.raw)
    for i in range(count):
        h = R.FPDFPage_GetObject(page.raw, i)
        t = R.FPDFPageObj_GetType(h)
        l, b, r, tt = ctypes.c_float(), ctypes.c_float(), ctypes.c_float(), ctypes.c_float()
        if not R.FPDFPageObj_GetBounds(h, l, b, r, tt): continue
        v = _to_view(pb, l.value, b.value, r.value, tt.value); v.update({'index': i, 'type': OBJ_TYPES.get(t, 'other')})
        if v['w'] <= 0 and v['h'] <= 0: continue
        out.append(v)
    page.close()
    return {'page': int(a.get('page', 0)), 'objects': out}


def image_object(a):
    page = _page(a.get('page', 0)); i = int(a['index'])
    h = R.FPDFPage_GetObject(page.raw, i)
    if not h or R.FPDFPageObj_GetType(h) != R.FPDF_PAGEOBJ_IMAGE: raise ValueError('Seçilen nesne bir görsel değil.')
    img = pdfium.PdfImage(h, page=page, pdf=doc)
    bm = img.get_bitmap(render=False); pil = bm.to_pil(); buf = io.BytesIO(); pil.save(buf, format='PNG')
    out = {'image': base64.b64encode(buf.getvalue()).decode('ascii'), 'px': [pil.width, pil.height]}
    pil.close(); bm.close(); page.close(); return out


def ocr(a):
    global ocr_engine
    import numpy as np
    from rapidocr import RapidOCR
    page = _page(a.get('page', 0))
    if ocr_engine is None:
        ocr_engine = RapidOCR(params={'Global.log_level': 'error', 'EngineConfig.onnxruntime.intra_op_num_threads': 2, 'EngineConfig.onnxruntime.inter_op_num_threads': 1})
    scale = min(3.0, 2600 / max(page.get_size()))
    bm = page.render(scale=scale); pil = bm.to_pil(); result = ocr_engine(np.asarray(pil)); pil.close(); bm.close(); page.close()
    texts = list(result.txts) if result.txts is not None else []
    scores = [float(x) for x in result.scores] if result.scores is not None else []
    boxes = result.boxes if result.boxes is not None else []
    out = []
    for k, text in enumerate(texts):
        q = boxes[k]; xs = [float(p[0]) / scale for p in q]; ys = [float(p[1]) / scale for p in q]
        out.append({'x': round(min(xs), 2), 'y': round(min(ys), 2), 'w': round(max(xs) - min(xs), 2), 'h': round(max(ys) - min(ys), 2),
                    'text': text, 'nl': True, 'score': round(scores[k], 3) if k < len(scores) else None})
    out.sort(key=lambda w: (round(w['y'] / 6), w['x']))
    return {'page': int(a.get('page', 0)), 'words': out, 'source': 'ocr', 'text': '\n'.join(w['text'] for w in out),
            'notice': 'OCR metni tahmindir. Ölçü, tolerans ve sayıları kaynak sayfada doğrulayın.'}


def search(a):
    _need(); q = str(a.get('query', '')).strip(); hits = []
    if not q: return {'hits': hits}
    limit = int(a.get('limit', 500))
    for i in range(len(doc)):
        page = doc[i]; pb = _box(page); tp = page.get_textpage()
        searcher = tp.search(q, match_case=bool(a.get('case')), match_whole_word=bool(a.get('whole')))
        while len(hits) < limit:
            r = searcher.get_next()
            if not r: break
            idx, cnt = r; rects = []
            for k in range(R.FPDFText_CountRects(tp.raw, idx, cnt)):
                l, t, rr, b = (ctypes.c_double() for _ in range(4))
                R.FPDFText_GetRect(tp.raw, k, l, t, rr, b)
                rects.append(_to_view(pb, l.value, b.value, rr.value, t.value))
            hits.append({'page': i, 'rects': rects})
        searcher.close(); tp.close(); page.close()
        if len(hits) >= limit: break
    return {'hits': hits}


def text(a):
    page = _page(a.get('page', 0)); tp = page.get_textpage(); t = tp.get_text_bounded(); tp.close(); page.close()
    return {'text': t}


# ---------------------------------------------------------------- kaydetme

def _ws(s):
    buf = ctypes.create_string_buffer((s + '\0').encode('utf-16-le'))
    _keep.append(buf); return ctypes.cast(buf, R.FPDF_WIDESTRING)


def _rgb(hexs, default=(0, 0, 0)):
    try:
        hexs = hexs.lstrip('#'); return tuple(int(hexs[i:i + 2], 16) for i in (0, 2, 4))
    except Exception: return default


_font_cache = {}
def _font(document):
    key = id(document)
    if key in _font_cache: return _font_cache[key]
    windir = os.environ.get('WINDIR', r'C:\Windows')
    for name in ('arial.ttf', 'segoeui.ttf', 'calibri.ttf'):
        p = os.path.join(windir, 'Fonts', name)
        if os.path.exists(p):
            data = open(p, 'rb').read(); buf = (ctypes.c_uint8 * len(data)).from_buffer_copy(data); _keep.append(buf)
            f = R.FPDFText_LoadFont(document.raw, buf, len(data), R.FPDF_FONT_TRUETYPE, True)
            if f: _font_cache[key] = f; return f
    f = R.FPDFText_LoadStandardFont(document.raw, b'Helvetica'); _font_cache[key] = f; return f


def _quad(pb, rect):
    cl, cb, cr, ct = pb
    x1 = cl + rect['x']; x2 = x1 + rect['w']; yt = ct - rect['y']; yb = yt - rect['h']
    return x1, yb, x2, yt


def _annot(page, subtype, color, alpha, rect):
    a = R.FPDFPage_CreateAnnot(page.raw, subtype)
    if not a: raise ValueError('PDF notu oluşturulamadı.')
    r, g, b = color
    R.FPDFAnnot_SetColor(a, R.FPDFANNOT_COLORTYPE_Color, r, g, b, alpha)
    l, bt, rr, t = rect
    R.FPDFAnnot_SetRect(a, R.FS_RECTF(l, t, rr, bt))
    R.FPDFAnnot_SetFlags(a, R.FPDF_ANNOT_FLAG_PRINT)
    return a


def _text_obj(document, page, font, s, x, y, size, color):
    """Places the line word by word; because the embedded CID font extracts spaces as NBSP
    the words are separate objects and real spaces appear in text extraction."""
    r, g, b = color; cx = float(x)
    for word in s.split(' '):
        if word:
            o = R.FPDFPageObj_CreateTextObj(document.raw, font, float(size))
            R.FPDFText_SetText(o, _ws(word))
            R.FPDFPageObj_SetFillColor(o, r, g, b, 255)
            R.FPDFPageObj_Transform(o, 1, 0, 0, 1, cx, float(y))
            R.FPDFPage_InsertObject(page.raw, o)
            bl, bb, br, bt = ctypes.c_float(), ctypes.c_float(), ctypes.c_float(), ctypes.c_float()
            cx = br.value if R.FPDFPageObj_GetBounds(o, bl, bb, br, bt) else cx + len(word) * size * 0.55
        cx += size * 0.3


def _white_rect(page, l, b, r, t, color=(255, 255, 255)):
    o = R.FPDFPageObj_CreateNewRect(l, b, r - l, t - b)
    R.FPDFPageObj_SetFillColor(o, color[0], color[1], color[2], 255)
    R.FPDFPath_SetDrawMode(o, R.FPDF_FILLMODE_WINDING, False)
    R.FPDFPage_InsertObject(page.raw, o)


def save(a):
    """Reopens the source PDF, applies the edits and writes the target. The source file is never changed."""
    src = a.get('source') or path; dest = os.path.abspath(a['dest'])
    if not src: raise ValueError('Kaynak PDF yok.')
    if os.path.abspath(src) == dest: raise ValueError('Kaynak dosyanın üzerine yazılmaz; farklı bir ad seçin.')
    d = pdfium.PdfDocument(src, password=a.get('password'))
    rotations = {int(k): int(v) for k, v in (a.get('rotations') or {}).items()}
    items = a.get('items') or []
    by_page = {}
    for it in items: by_page.setdefault(int(it['page']), []).append(it)
    stats = {'annotations': 0, 'texts': 0, 'removed_text_objects': 0, 'moved': 0, 'deleted': 0, 'covered': 0}
    try:
        for pi in sorted(set(by_page) | set(rotations)):
            if not 0 <= pi < len(d): continue
            page = d[pi]; orig = page.get_rotation()
            if orig: page.set_rotation(0)
            pb = _box(page); changed_content = False
            handles = [R.FPDFPage_GetObject(page.raw, i) for i in range(R.FPDFPage_CountObjects(page.raw))]
            remove = set(); explicit = set()
            for it in by_page.get(pi, []):
                k = it['kind']
                if k == 'object_move':
                    i = int(it['index'])
                    if 0 <= i < len(handles):
                        R.FPDFPageObj_Transform(handles[i], 1, 0, 0, 1, float(it['dx']), -float(it['dy'])); stats['moved'] += 1; changed_content = True
                elif k == 'object_delete':
                    i = int(it['index'])
                    if 0 <= i < len(handles): remove.add(i); explicit.add(i)
                elif k == 'correct':
                    l, b, r, t = _quad(pb, it['rect'])
                    for i, h in enumerate(handles):
                        if R.FPDFPageObj_GetType(h) != R.FPDF_PAGEOBJ_TEXT: continue
                        bl, bb, br, bt = ctypes.c_float(), ctypes.c_float(), ctypes.c_float(), ctypes.c_float()
                        if R.FPDFPageObj_GetBounds(h, bl, bb, br, bt) and bl.value >= l - 1.5 and br.value <= r + 1.5 and bb.value >= b - 1.5 and bt.value <= t + 1.5:
                            remove.add(i)
            for i in sorted(remove, reverse=True):
                h = handles[i]
                if R.FPDFPage_RemoveObject(page.raw, h):
                    R.FPDFPageObj_Destroy(h); changed_content = True
                    stats['deleted' if i in explicit else 'removed_text_objects'] += 1
            font = None
            for it in by_page.get(pi, []):
                k = it['kind']; color = _rgb(it.get('color', '#000000'))
                if k in ('highlight', 'underline', 'strikeout'):
                    sub = {'highlight': R.FPDF_ANNOT_HIGHLIGHT, 'underline': R.FPDF_ANNOT_UNDERLINE, 'strikeout': R.FPDF_ANNOT_STRIKEOUT}[k]
                    rects = it['rects']; qs = [_quad(pb, r) for r in rects]
                    a_ = _annot(page, sub, color, int(it.get('alpha', 110 if k == 'highlight' else 255)),
                                (min(q[0] for q in qs), min(q[1] for q in qs), max(q[2] for q in qs), max(q[3] for q in qs)))
                    for (l, b, r, t) in qs:
                        R.FPDFAnnot_AppendAttachmentPoints(a_, R.FS_QUADPOINTSF(l, t, r, t, l, b, r, b))
                    if it.get('note'): R.FPDFAnnot_SetStringValue(a_, b'Contents', _ws(it['note']))
                    R.FPDFAnnot_SetStringValue(a_, b'T', _ws(it.get('author', 'MailStudio')))
                    R.FPDFPage_CloseAnnot(a_); stats['annotations'] += 1
                elif k == 'rect':
                    l, b, r, t = _quad(pb, it['rect'])
                    a_ = _annot(page, R.FPDF_ANNOT_SQUARE, color, 255, (l, b, r, t))
                    R.FPDFAnnot_SetBorder(a_, 0, 0, float(it.get('width', 1.5)))
                    if it.get('note'): R.FPDFAnnot_SetStringValue(a_, b'Contents', _ws(it['note']))
                    R.FPDFPage_CloseAnnot(a_); stats['annotations'] += 1
                elif k == 'ink':
                    cl, cb, cr, ct = pb; pts_all = []
                    for stroke in it['strokes']: pts_all += stroke
                    if not pts_all: continue
                    xs = [cl + p[0] for p in pts_all]; ys = [ct - p[1] for p in pts_all]; w = float(it.get('width', 1.5))
                    a_ = _annot(page, R.FPDF_ANNOT_INK, color, 255, (min(xs) - w, min(ys) - w, max(xs) + w, max(ys) + w))
                    for stroke in it['strokes']:
                        arr = (R.FS_POINTF * len(stroke))(*[R.FS_POINTF(cl + p[0], ct - p[1]) for p in stroke])
                        R.FPDFAnnot_AddInkStroke(a_, arr, len(stroke))
                    R.FPDFAnnot_SetBorder(a_, 0, 0, w)
                    R.FPDFPage_CloseAnnot(a_); stats['annotations'] += 1
                elif k == 'note':
                    cl, cb, cr, ct = pb; x = cl + it['x']; y = ct - it['y']
                    a_ = _annot(page, R.FPDF_ANNOT_TEXT, color, 255, (x, y - 20, x + 20, y))
                    R.FPDFAnnot_SetStringValue(a_, b'Contents', _ws(it.get('text', '')))
                    R.FPDFAnnot_SetStringValue(a_, b'T', _ws(it.get('author', 'MailStudio')))
                    R.FPDFPage_CloseAnnot(a_); stats['annotations'] += 1
                elif k in ('text', 'correct'):
                    font = font or _font(d)
                    l, b, r, t = _quad(pb, it['rect']); size = float(it.get('size', 10))
                    if k == 'correct':
                        _white_rect(page, l - 0.6, b - 0.6, r + 0.6, t + 0.6, _rgb(it.get('back', '#FFFFFF'), (255, 255, 255))); stats['covered'] += 1
                    lines = str(it.get('text', '')).split('\n'); lh = size * 1.18
                    first = (pb[3] - float(it['baseline'])) if it.get('baseline') is not None else t - size * 0.92 - (0 if k == 'correct' else 2)
                    for n, line in enumerate(lines):
                        if line: _text_obj(d, page, font, line, l + (0.8 if k == 'correct' else 2), first - n * lh, size, color)
                    changed_content = True; stats['texts'] += 1
            if changed_content: R.FPDFPage_GenerateContent(page.raw)
            new_rot = rotations.get(pi, orig)
            page.set_rotation(int(new_rot) % 360)
            page.close()
        tmp = dest + '.yaziliyor'
        d.save(tmp)
        os.replace(tmp, dest)
    finally:
        d.close()
        _keep.clear(); _font_cache.clear()
    return {'path': dest, 'bytes': os.path.getsize(dest), 'stats': stats}


def dispatch(op, a):
    if op == 'pdfs_open': return open_doc(a['path'], a.get('password'))
    if op == 'pdfs_render': return render(a)
    if op == 'pdfs_words': return words(a)
    if op == 'pdfs_objects': return objects(a)
    if op == 'pdfs_image': return image_object(a)
    if op == 'pdfs_ocr': return ocr(a)
    if op == 'pdfs_search': return search(a)
    if op == 'pdfs_text': return text(a)
    if op == 'pdfs_save': return save(a)
    return None
