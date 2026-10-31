"""Tables (parts lists / BOM, schedules, price lists) from PDF pages, for export to Excel.

Words come from the text layer, or from OCR when the page has none (then every table is flagged review=True).
Rows are lines of words; columns are the vertical gaps that stay free of text through the whole table, or the
table's own vertical ruling lines when it has them. A run of at least three lines with two or more cells is a table."""
import os, re, statistics


def _words_text(page):
    """Words with boxes (points, origin top-left) from the text layer."""
    import pypdfium2.raw as R
    width, height = page.get_size()
    tp = page.get_textpage(); n = tp.count_chars(); words = []; cur = None; last = None
    def flush():
        nonlocal cur
        if cur and cur['t'].strip(): words.append(cur)
        cur = None
    for i in range(n):
        ch = chr(R.FPDFText_GetUnicode(tp.raw, i) or 32)
        if ch.strip() == '': flush(); last = None; continue
        l, b, r, t = tp.get_charbox(i, loose=False)
        h = max(t - b, 1)
        if cur is not None and last is not None:
            # the word's height is the reference: '-', '.' and ',' have tiny boxes
            ref = max(h, cur['tp'] - cur['b'], 2)
            # same line when the boxes overlap vertically (a comma hangs below the baseline but still overlaps)
            if min(t, cur['tp']) - max(b, cur['b']) < -ref * 0.15 or l - last[2] > ref * 0.8 or l < last[0] - ref: flush()   # spaces in the text layer split words; only wide gaps here
        if cur is None: cur = {'t': '', 'l': l, 'b': b, 'r': r, 'tp': t}
        cur['t'] += ch; cur['l'] = min(cur['l'], l); cur['b'] = min(cur['b'], b); cur['r'] = max(cur['r'], r); cur['tp'] = max(cur['tp'], t)
        last = (l, b, r, t)
    flush(); tp.close()
    return [{'text': w['t'], 'x1': w['l'], 'x2': w['r'], 'y1': height - w['tp'], 'y2': height - w['b']} for w in words], False


_ocr = None


def _words_ocr(page):
    global _ocr
    import numpy as np
    from rapidocr import RapidOCR
    if _ocr is None:
        _ocr = RapidOCR(params={'Global.log_level': 'error', 'EngineConfig.onnxruntime.intra_op_num_threads': 2,
                                        'EngineConfig.onnxruntime.inter_op_num_threads': 1})
    scale = min(3.0, 2600 / max(page.get_size()))
    bmp = page.render(scale=scale); pil = bmp.to_pil()
    try: res = _ocr(np.asarray(pil))
    finally: pil.close(); bmp.close()
    out = []
    if res.txts is None: return out, True
    for box, text in zip(res.boxes, res.txts):
        xs = [float(p[0]) / scale for p in box]; ys = [float(p[1]) / scale for p in box]
        # OCR returns phrases; split on wide spaces so cells stay apart
        out.append({'text': str(text), 'x1': min(xs), 'x2': max(xs), 'y1': min(ys), 'y2': max(ys)})
    return out, True


def _vertical_rules(page):
    """x positions (points) of long vertical lines drawn on the page (table grid), with their y extent."""
    import pypdfium2.raw as R
    import ctypes
    width, height = page.get_size(); rules = []
    for obj in page.get_objects(max_depth=3):
        if obj.type != R.FPDF_PAGEOBJ_PATH: continue
        l, b, r, t = obj.get_bounds()
        if r - l < 1.5 and t - b > 8: rules.append(((l + r) / 2, height - t, height - b))
        n = R.FPDFPath_CountSegments(obj.raw)
        if n <= 2 or n > 400: continue
        # rectangles / polylines: look at each straight vertical piece
        pts = []
        for i in range(n):
            seg = R.FPDFPath_GetPathSegment(obj.raw, i)
            x, y = ctypes.c_float(), ctypes.c_float()
            R.FPDFPathSegment_GetPoint(seg, x, y); pts.append((x.value, y.value))
        for (x1, y1), (x2, y2) in zip(pts, pts[1:]):
            if abs(x1 - x2) < 0.8 and abs(y1 - y2) > 8: rules.append((x1, height - max(y1, y2), height - min(y1, y2)))
    return rules


def _lines(words):
    words = sorted(words, key=lambda w: ((w['y1'] + w['y2']) / 2, w['x1']))
    lines = []
    for w in words:
        cy = (w['y1'] + w['y2']) / 2; h = w['y2'] - w['y1']
        if lines and abs(cy - lines[-1]['cy']) <= max(2.0, 0.5 * min(h, lines[-1]['h'])):
            L = lines[-1]; L['words'].append(w); L['cy'] = (L['cy'] * (len(L['words']) - 1) + cy) / len(L['words']); L['h'] = max(L['h'], h)
        else: lines.append({'cy': cy, 'h': h, 'words': [w]})
    for L in lines: L['words'].sort(key=lambda w: w['x1'])
    return lines


def _cells(line, gap):
    """Words of one line joined into cells where the space between them is wider than <gap>."""
    cells = []
    for w in line['words']:
        if cells and w['x1'] - cells[-1]['x2'] <= gap: cells[-1]['text'] += ' ' + w['text']; cells[-1]['x2'] = w['x2']
        else: cells.append({'text': w['text'], 'x1': w['x1'], 'x2': w['x2']})
    return cells


def _columns(rows, rules, x0, x1):
    """Column boundaries: vertical rules inside the table, else gaps that no cell crosses."""
    inner = sorted({round(r[0], 1) for r in rules if x0 - 2 < r[0] < x1 + 2})
    if len(inner) >= 3:
        cuts = [inner[0]] + [x for a, x in zip(inner, inner[1:]) if x - a > 4]
        return cuts[1:-1] if cuts[0] <= x0 + 3 else cuts
    spans = sorted((c['x1'], c['x2']) for r in rows for c in r)
    cuts = []; end = None
    for a, b in spans:
        if end is not None and a > end + 1.5: cuts.append((end + a) / 2)
        end = b if end is None else max(end, b)
    return cuts


def tables(a):
    import pypdfium2 as pdfium
    doc = pdfium.PdfDocument(os.path.abspath(a['path'])); result = []; ocr_used = False
    pages = a.get('pages')
    try:
        for index in (pages if pages is not None else range(min(len(doc), int(a.get('max_pages', 30))))):
            page = doc[int(index)]
            words, ocr = _words_text(page)
            if len(words) < 6 and a.get('ocr', True): words, ocr = _words_ocr(page); ocr_used |= ocr
            rules = [] if ocr else _vertical_rules(page)
            lines = _lines(words)
            if not lines: page.close(); continue
            hmed = statistics.median([L['h'] for L in lines]) or 8
            gap = max(4.0, hmed * 0.9)
            # runs of lines with 2+ cells and not too far apart vertically
            run = []
            def close_run():
                if len(run) >= 3:
                    rows = [_cells(L, gap) for L in run]
                    x0 = min(c['x1'] for r in rows for c in r); x1 = max(c['x2'] for r in rows for c in r)
                    cuts = _columns(rows, [r for r in rules if r[1] <= run[-1]['cy'] and r[2] >= run[0]['cy']], x0, x1)
                    grid = []
                    for r in rows:
                        cells = [''] * (len(cuts) + 1)
                        for c in r:
                            col = sum(1 for x in cuts if (c['x1'] + c['x2']) / 2 > x)
                            cells[col] = (cells[col] + ' ' + c['text']).strip()
                        grid.append(cells)
                    # drop columns that are empty everywhere
                    keep = [i for i in range(len(grid[0])) if any(row[i] for row in grid)]
                    grid = [[row[i] for i in keep] for row in grid]
                    if len(grid[0]) >= 2:
                        result.append({'page': int(index), 'rows': grid, 'columns': len(grid[0]),
                                       'bbox': [round(x0, 1), round(run[0]['cy'] - run[0]['h'], 1), round(x1, 1), round(run[-1]['cy'] + run[-1]['h'], 1)],
                                       'review': ocr, 'ruled': bool(rules)})
                run.clear()
            for L in lines:
                multi = len(_cells(L, gap)) >= 2
                if multi and (not run or L['cy'] - run[-1]['cy'] <= max(run[-1]['h'], L['h']) * 3.2): run.append(L)
                else:
                    close_run()
                    if multi: run.append(L)
            close_run()
            page.close()
    finally:
        doc.close()
    return {'tables': result, 'ocr': ocr_used,
            'notice': 'OCR ile okunan tablolar tahmindir; sayıları kaynak sayfada doğrulayın.' if ocr_used else ''}


def dispatch(op, a):
    if op == 'pdf_tables': return tables(a)
    return None
