"""Quick ballooning of a PDF drawing for first article inspection (AS9102 Form 3).

balloon_extract: finds the characteristics on every page (dimensions, diameters, radii, angles, threads, roughness,
                 tolerance notes) from the PDF text layer, or with OCR when the page has none. Every item carries
                 its box in page points (origin top-left), the parsed nominal / limits and how it was read.
balloon_save:    writes a copy of the PDF with numbered balloons and leader lines (the source file is never changed).

Numbers read with OCR are flagged review=True: OCR is a guess and the inspector must confirm them on the drawing."""
import ctypes, math, os, re

# ------------------------------------------------------------------ ISO 2768-1 general tolerances (linear, mm)
_ISO_RANGES = [(0.5, 3), (3, 6), (6, 30), (30, 120), (120, 400), (400, 1000), (1000, 2000), (2000, 4000)]
_ISO_LINEAR = {
    'f': [0.05, 0.05, 0.1, 0.15, 0.2, 0.3, 0.5, None],
    'm': [0.1, 0.1, 0.2, 0.3, 0.5, 0.8, 1.2, 2.0],
    'c': [0.2, 0.3, 0.5, 0.8, 1.2, 2.0, 3.0, 4.0],
    'v': [None, 0.5, 1.0, 1.5, 2.5, 4.0, 6.0, 8.0],
}


def iso2768(nominal, cls):
    """± deviation for a linear size and tolerance class f/m/c/v (None when the table has no value)."""
    cls = (cls or '').lower()[:1]
    if cls not in _ISO_LINEAR or nominal is None: return None
    n = abs(nominal)
    for i, (lo, hi) in enumerate(_ISO_RANGES):
        if (lo <= n <= hi) if i == 0 else (lo < n <= hi): return _ISO_LINEAR[cls][i]
    return None


# ------------------------------------------------------------------ parsing
NUM = r'\d+(?:[.,]\d+)?'
_TITLE = re.compile(r'\b(SCALE|SHEET|DWG|DRAWING|DRAWN|CHECKED|APPROVED|DATE|REV(ISION)?|MAT(ERIAL)?\.?|PART\s*NO|P/N|WEIGHT|'
                    r'ÖLÇEK|SAYFA|PAFTA|TARİH|ÇİZEN|KONTROL|ONAY|MALZEME|AĞIRLIK|PROJECTION|SECTION|DETAIL|VIEW|KESİT|DETAY|GÖRÜNÜŞ)\b', re.I)
_GENERAL = re.compile(r'(?:ISO|DIN|EN)\s*2768\s*[-–]?\s*([fmcvFMCV])\s*([HKLhkl])?')


def _f(s):
    try: return float(s.replace(',', '.'))
    except Exception: return None


def _ocr_fix(t):
    t = t.strip(' .·,;:-_=~\'"`|')
    # Ø is read as 0/O/@ by OCR: a whole number cannot start with 0 ("020" → "Ø20", "0110" → "Ø110", but "0.5" stays)
    t = re.sub(r'(?<![\w.,])[0O@ø]\s?(?=[1-9]\d*(?:[.,]\d+)?(?!\d))', 'Ø', t)
    t = t.replace('×', 'x').replace('·', '.')
    return t


def classify(text):
    """Characteristic of one text run, or None when it is not something to inspect."""
    t = ' '.join(text.split())
    if not re.search(r'\d', t): return None
    g = _GENERAL.search(t)
    if g: return {'type': 'general', 'requirement': t, 'class': g.group(1).lower(), 'class2': (g.group(2) or '').upper()}
    if _TITLE.search(t) and not re.search(r'[Ø⌀∅]|±|\bR\s?\d|\bM\d', t): return None
    if re.fullmatch(r'[A-Z]{1,3}-\d{2,}.*', t): return None                  # part numbers like NW-1042
    item = {'requirement': t, 'quantity': 1, 'nominal': None, 'upper': None, 'lower': None, 'fit': None}
    q = re.match(r'^\s*(\d+)\s*[xX×]\s*', t)
    if q: item['quantity'] = int(q.group(1))
    body = t[q.end():] if q else t
    m = re.search(r'\bM(\d+(?:[.,]\d+)?)(?:\s*[xX]\s*(' + NUM + r'))?(?:\s*-?\s*(\d[gGhH]))?', body)
    if m:
        item.update(type='thread', nominal=_f(m.group(1)), pitch=_f(m.group(2)) if m.group(2) else None, fit=m.group(3)); return item
    m = re.search(r'\bR([az])\s*(' + NUM + ')', body)
    if m: item.update(type='roughness', nominal=_f(m.group(2)), upper=_f(m.group(2)), lower=0.0, unit='µm', param='R' + m.group(1)); return item
    m = re.search(r'(' + NUM + r')\s*°(?:\s*±\s*(' + NUM + r')\s*°?)?', body)
    if m:
        item.update(type='angle', nominal=_f(m.group(1)), unit='°')
        if m.group(2): item['upper'] = item['lower'] = _f(m.group(2))
        return item
    kind = 'linear'
    m = re.search(r'(?:[Ø⌀∅]|\bDIA\.?\s*)\s*(' + NUM + ')', body)
    if m: kind = 'diameter'
    else:
        m = re.search(r'(?<![A-Za-z])(?:S?R)\s?(' + NUM + ')', body)
        if m: kind = 'radius'
        else: m = re.search(r'(?<![\w.,])(' + NUM + r')(?![\w.,])', body)
    if not m: return None
    nominal = _f(m.group(1)); rest = body[m.end():]
    item.update(type=kind, nominal=nominal, unit='mm')
    tol = re.match(r'\s*±\s*(' + NUM + ')', rest)
    if tol: item['upper'] = item['lower'] = _f(tol.group(1))
    else:
        tol = re.match(r'\s*\+\s*(' + NUM + r')\s*/?\s*[-−]\s*(' + NUM + ')', rest)
        if tol: item['upper'], item['lower'] = _f(tol.group(1)), _f(tol.group(2))
        else:
            fit = re.match(r'\s*([A-Za-z]{1,2}\d{1,2})\b', rest)
            if fit: item['fit'] = fit.group(1)
    # long notes ("8x Ø20 THRU EQ. SP. ON PCD 180") stay one characteristic with the first size as nominal
    if kind == 'linear' and len(body) > 14 and not tol: item['type'] = 'note'
    return item


# ------------------------------------------------------------------ reading text runs with boxes
def _runs_from_text(page):
    """Text runs (words joined on one line) from the PDF text layer; boxes in points, origin top-left."""
    width, height = page.get_size(); tp = page.get_textpage(); n = tp.count_chars(); runs = []; cur = None; last = None
    import pypdfium2.raw as R
    def flush():
        nonlocal cur
        if cur and cur['t'].strip(): runs.append(cur)
        cur = None
    for i in range(n):
        ch = chr(R.FPDFText_GetUnicode(tp.raw, i) or 32)
        if ch in '\r\n': flush(); last = None; continue
        l, b, r, t = tp.get_charbox(i, loose=False)
        if ch.strip() == '':
            if cur is not None: cur['t'] += ' '
            continue
        h = max(t - b, 1)
        if cur is not None and last is not None:
            ref = max(h, cur['b2'] - cur['b1'], 2)
            if abs((b + t) / 2 - (cur['b1'] + cur['b2']) / 2) > ref * 0.75 or l - last[2] > ref * 1.6 or l < last[0] - ref: flush()
        if cur is None: cur = {'t': '', 'x1': l, 'b1': b, 'x2': r, 'b2': t}
        cur['t'] += ch; cur['x1'] = min(cur['x1'], l); cur['b1'] = min(cur['b1'], b); cur['x2'] = max(cur['x2'], r); cur['b2'] = max(cur['b2'], t)
        last = (l, b, r, t)
    flush(); tp.close()
    return [{'text': ' '.join(r['t'].split()), 'x': r['x1'], 'y': height - r['b2'], 'w': r['x2'] - r['x1'], 'h': r['b2'] - r['b1'], 'score': 1.0}
            for r in runs]


_ocr = None


def _runs_from_ocr(page):
    global _ocr
    import numpy as np
    from rapidocr import RapidOCR
    if _ocr is None:
        _ocr = RapidOCR(params={'Global.log_level': 'error', 'EngineConfig.onnxruntime.intra_op_num_threads': 2,
                                'EngineConfig.onnxruntime.inter_op_num_threads': 1})
    scale = min(3.0, 2600 / max(page.get_size()))
    bitmap = page.render(scale=scale); pil = bitmap.to_pil()
    try: result = _ocr(np.asarray(pil))
    finally: pil.close(); bitmap.close()
    if result.txts is None: return []
    out = []
    for box, text, score in zip(result.boxes, result.txts, result.scores):
        xs = [p[0] / scale for p in box]; ys = [p[1] / scale for p in box]
        out.append({'text': _ocr_fix(text), 'x': min(xs), 'y': min(ys), 'w': max(xs) - min(xs), 'h': max(ys) - min(ys), 'score': float(score)})
    return out


_PART = re.compile(r'\b(?:DWG|DRAWING|PART|P/N|RESİM|PARÇA)\s*(?:N[O0O]|NUMBER|NUMARASI)?\s*[.:#]?\s*([A-Z0-9][A-Z0-9][A-Z0-9\-_./]{1,30})', re.I)
_REV = re.compile(r'\bREV(?:ISION|\.)?\s*[:.]?\s*([A-Z]{1,2}|\d{1,2})\b')
_MAT = re.compile(r'\b(?:MAT(?:ERIAL)?|MALZEME)\s*[.:]?\s*([A-Z0-9][\w.\-/ ]{2,24}?)\s*$', re.I)


def _title_info(text, title):
    """Part number, revision and material from the title block (first hit wins)."""
    t = ' '.join(text.split())
    m = _PART.search(t)
    if m and 'part_number' not in title and re.search(r'\d', m.group(1)): title['part_number'] = m.group(1).rstrip('.')
    m = _REV.search(t)
    if m and 'revision' not in title: title['revision'] = m.group(1)
    m = _MAT.search(t)
    if m and 'material' not in title: title['material'] = m.group(1).strip()


def _zone(x, y, w, h, cols=8, rows=6):
    """Drawing frame zone (ISO 5457 style: columns 1.. from the left, rows A.. from the top)."""
    if w < h: cols, rows = rows, cols
    c = min(cols, int(x / w * cols) + 1); r = min(rows, int(y / h * rows))
    return f"{'ABCDEFGHJK'[r]}{c}"


def extract(a):
    import pypdfium2 as pdfium
    doc = pdfium.PdfDocument(os.path.abspath(a['path'])); items = []; pages = []; general = None; used_ocr = False; title = {}
    try:
        for index in range(min(len(doc), int(a.get('max_pages', 20)))):
            page = doc[index]; w, h = page.get_size()
            runs = _runs_from_text(page); source = 'text'
            if sum(1 for r in runs if re.search(r'\d', r['text'])) < 2 and a.get('ocr', True):
                runs = _runs_from_ocr(page); source = 'ocr'; used_ocr = True
            pages.append({'page': index, 'width': w, 'height': h, 'source': source})
            for r in runs:
                _title_info(r['text'], title)
                c = classify(r['text'])
                if not c: continue
                if c['type'] == 'general':
                    general = general or c; continue
                c.update(page=index, x=round(r['x'], 2), y=round(r['y'], 2), w=round(r['w'], 2), h=round(r['h'], 2), source=source,
                         review=source == 'ocr' or r['score'] < 0.9, score=round(r['score'], 3), zone=_zone(r['x'] + r['w'] / 2, r['y'] + r['h'] / 2, w, h),
                         text=r['text'])
                items.append(c)
            page.close()
    finally:
        doc.close()
    gen_class = (a.get('general_class') or (general or {}).get('class') or '').lower()
    # reading order per page: rows of about a balloon height, left to right
    items.sort(key=lambda i: (i['page'], round(i['y'] / 18), i['x']))
    for n, it in enumerate(items, 1):
        it['id'] = n
        if it['upper'] is None and it['type'] in ('linear', 'diameter', 'radius', 'note') and gen_class:
            d = iso2768(it['nominal'], gen_class)
            if d is not None: it['upper'] = it['lower'] = d; it['tolerance_source'] = f'ISO 2768-{gen_class}'
        elif it['upper'] is not None and it['type'] != 'roughness': it['tolerance_source'] = 'drawing'
        if it['nominal'] is not None and it['upper'] is not None:
            it['min'] = round(it['nominal'] - (it['lower'] or 0), 4); it['max'] = round(it['nominal'] + it['upper'], 4)
    place(items, pages)
    return {'items': items, 'pages': pages, 'general': general, 'title': title, 'general_class': gen_class or None, 'ocr': used_ocr,
            'notice': 'OCR ile okunan değerler tahmindir; her balonu resimde doğrulayın.' if used_ocr else ''}


# ------------------------------------------------------------------ writing the ballooned copy
def _circle(page_raw, cx, cy, r, stroke, fill, width):
    import pypdfium2.raw as R
    k = 0.5523 * r
    o = R.FPDFPageObj_CreateNewPath(cx + r, cy)
    R.FPDFPath_BezierTo(o, cx + r, cy + k, cx + k, cy + r, cx, cy + r)
    R.FPDFPath_BezierTo(o, cx - k, cy + r, cx - r, cy + k, cx - r, cy)
    R.FPDFPath_BezierTo(o, cx - r, cy - k, cx - k, cy - r, cx, cy - r)
    R.FPDFPath_BezierTo(o, cx + k, cy - r, cx + r, cy - k, cx + r, cy)
    R.FPDFPath_Close(o)
    R.FPDFPageObj_SetStrokeColor(o, *stroke, 255); R.FPDFPageObj_SetStrokeWidth(o, width)
    R.FPDFPageObj_SetFillColor(o, *fill, 255)
    R.FPDFPath_SetDrawMode(o, R.FPDF_FILLMODE_WINDING, True)
    R.FPDFPage_InsertObject(page_raw, o)


def _line(page_raw, x1, y1, x2, y2, stroke, width):
    import pypdfium2.raw as R
    o = R.FPDFPageObj_CreateNewPath(x1, y1); R.FPDFPath_LineTo(o, x2, y2)
    R.FPDFPageObj_SetStrokeColor(o, *stroke, 255); R.FPDFPageObj_SetStrokeWidth(o, width)
    R.FPDFPath_SetDrawMode(o, 0, True)
    R.FPDFPage_InsertObject(page_raw, o)


def _place(item, radius, taken, pw, ph):
    """Balloon centre (top-left origin) near the feature, not on other balloons or features, inside the page."""
    x, y, w, h = item['x'], item['y'], item['w'], item['h']
    gap = radius * 1.6
    cands = [(x - gap, y - gap), (x + w + gap, y - gap), (x - gap, y + h + gap), (x + w + gap, y + h + gap),
             (x + w / 2, y - gap * 1.3), (x + w / 2, y + h + gap * 1.3), (x - gap * 1.6, y + h / 2), (x + w + gap * 1.6, y + h / 2)]
    def clash(cx, cy):
        if cx - radius < 2 or cy - radius < 2 or cx + radius > pw - 2 or cy + radius > ph - 2: return 1e9
        s = 0.0
        for (tx, ty, tw, th) in taken:
            dx = max(tx - cx, 0, cx - (tx + tw)); dy = max(ty - cy, 0, cy - (ty + th))
            d = math.hypot(dx, dy)
            if d < radius + 1.5: s += (radius + 1.5 - d) * 10
        return s
    best = min(cands, key=lambda c: (clash(*c), math.hypot(c[0] - (x + w / 2), c[1] - (y + h / 2))))
    return best


def place(items, pages, radius=None):
    """Balloon position for every item (top-left origin, points), so the app preview and the saved PDF agree."""
    size = {p['page']: (p['width'], p['height']) for p in pages}
    by_page = {}
    for it in items: by_page.setdefault(int(it['page']), []).append(it)
    for index, group in by_page.items():
        pw, ph = size[index]; r = float(radius or max(5.0, min(pw, ph) / 70))
        taken = [(i['x'], i['y'], i['w'], i['h']) for i in group]
        for it in group:
            cx, cy = _place(it, r, taken, pw, ph); taken.append((cx - r, cy - r, 2 * r, 2 * r))
            it['balloon'] = [round(cx, 2), round(cy, 2), round(r, 2)]


def render(a):
    """PNG of one page for the balloon editor (own document; the viewer's open PDF is not touched)."""
    import base64, io, pypdfium2 as pdfium
    doc = pdfium.PdfDocument(os.path.abspath(a['path']))
    try:
        index = int(a.get('page', 0))
        if not 0 <= index < len(doc): raise ValueError('Sayfa numarası geçersiz.')
        page = doc[index]; w, h = page.get_size(); scale = float(a.get('scale') or min(3.0, 2000 / max(w, h)))
        bmp = page.render(scale=scale); pil = bmp.to_pil(); buf = io.BytesIO(); pil.save(buf, format='PNG')
        pil.close(); bmp.close(); page.close()
        return {'page': index, 'pages': len(doc), 'width': w, 'height': h, 'scale': scale, 'image': base64.b64encode(buf.getvalue()).decode('ascii')}
    finally:
        doc.close()


def save(a):
    import pypdfium2 as pdfium, pypdfium2.raw as R
    from pdf_studio import _font, _ws
    src = os.path.abspath(a['path']); dest = os.path.abspath(a['dest'])
    if os.path.normcase(src) == os.path.normcase(dest): raise ValueError('Kaynak PDF üzerine yazılmaz; başka bir dosya adı seçin.')
    color = tuple(int(a.get('color', '#D62828').lstrip('#')[i:i + 2], 16) for i in (0, 2, 4))
    doc = pdfium.PdfDocument(src)
    try:
        font = _font(doc); by_page = {}
        for it in a['items']: by_page.setdefault(int(it['page']), []).append(it)
        for index, items in by_page.items():
            page = doc[index]; pw, ph = page.get_size()
            radius = float(a.get('radius') or max(5.0, min(pw, ph) / 70))
            taken = [(i['x'], i['y'], i['w'], i['h']) for i in items]
            for it in items:
                if it.get('balloon'): cx, cy, radius = (float(v) for v in it['balloon'])
                else: cx, cy = _place(it, radius, taken, pw, ph)
                taken.append((cx - radius, cy - radius, 2 * radius, 2 * radius))
                # leader to the nearest point of the feature box
                fx = min(max(cx, it['x']), it['x'] + it['w']); fy = min(max(cy, it['y']), it['y'] + it['h'])
                d = math.hypot(fx - cx, fy - cy)
                if d > radius:
                    sx, sy = cx + (fx - cx) / d * radius, cy + (fy - cy) / d * radius
                    _line(page.raw, sx, ph - sy, fx, ph - fy, color, max(0.5, radius / 9))
                _circle(page.raw, cx, ph - cy, radius, color, (255, 255, 255), max(0.6, radius / 7))
                label = str(it['id']); size = radius * (1.15 if len(label) < 2 else 0.95 if len(label) < 3 else 0.75)
                o = R.FPDFPageObj_CreateTextObj(doc.raw, font, float(size)); R.FPDFText_SetText(o, _ws(label))
                R.FPDFPageObj_SetFillColor(o, *color, 255)
                bl, bb, br, bt = ctypes.c_float(), ctypes.c_float(), ctypes.c_float(), ctypes.c_float()
                R.FPDFPageObj_GetBounds(o, bl, bb, br, bt)
                tw, th = br.value - bl.value, bt.value - bb.value
                R.FPDFPageObj_Transform(o, 1, 0, 0, 1, cx - tw / 2 - bl.value, ph - cy - th / 2 - bb.value)
                R.FPDFPage_InsertObject(page.raw, o)
                it['balloon'] = [round(cx, 2), round(cy, 2), round(radius, 2)]
            if not R.FPDFPage_GenerateContent(page.raw): raise ValueError('Sayfa içeriği yazılamadı.')
            page.close()
        tmp = dest + '.tmp'
        doc.save(tmp)
    finally:
        doc.close()
    os.replace(tmp, dest)
    return {'path': dest, 'bytes': os.path.getsize(dest), 'items': a['items']}


def dispatch(op, a):
    if op == 'balloon_extract': return extract(a)
    if op == 'balloon_save': return save(a)
    if op == 'balloon_render': return render(a)
    return None
