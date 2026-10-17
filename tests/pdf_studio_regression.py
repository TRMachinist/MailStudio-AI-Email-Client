"""PDF studio regression: open, word boxes, objects, search and save on a generated PDF."""
import os, sys, tempfile, json
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'src', 'MailStudio.Technical'))
import pypdfium2 as pdfium, pypdfium2.raw as R
import pdf_studio as S

work = tempfile.mkdtemp(prefix='pdfs-')
src = os.path.join(work, 'kaynak.pdf')
d = pdfium.PdfDocument.new()
for k in range(2):
    page = d.new_page(595, 842)
    font = S._font(d)
    S._text_obj(d, page, font, 'Teklif No 4711 Flanş Ø120 çelik', 72, 760, 14, (0, 0, 0))
    S._text_obj(d, page, font, 'İkinci satır ğüşıöç', 72, 730, 12, (0, 0, 0))
    S._white_rect(page, 300, 300, 400, 380, (200, 30, 30))
    R.FPDFPage_GenerateContent(page.raw)
    if k == 1: page.set_rotation(90)
    page.close()
d.save(src); d.close(); S._keep.clear(); S._font_cache.clear()

info = S.open_doc(src)
assert info['pages'] == 2, info
assert info['sizes'][1]['rotation'] == 90 and abs(info['sizes'][1]['w'] - 595) < 1, info['sizes']
r = S.render({'page': 0, 'scale': 1.0}); assert r['px'][0] == 595, r['px']
w = S.words({'page': 0})['words']
texts = [x['text'] for x in w]
assert 'Flanş' in texts and 'ğüşıöç' in texts, texts
tek = next(x for x in w if x['text'] == 'Teklif')
assert 60 < tek['x'] < 80 and 60 < tek['y'] < 90, tek   # 842-760-~14 ≈ 70 from the top
w1 = S.words({'page': 1})['words']; t1 = next(x for x in w1 if x['text'] == 'Teklif')
assert abs(t1['x'] - tek['x']) < 0.5 and abs(t1['y'] - tek['y']) < 0.5, (t1, tek)  # a rotated page lives in the same view space
o = S.objects({'page': 0})['objects']
assert any(x['type'] == 'path' and abs(x['x'] - 300) < 1 and abs(x['y'] - (842 - 380)) < 1 for x in o), o
hits = S.search({'query': 'flanş'})['hits']; assert len(hits) == 2 and hits[0]['rects'], hits
rect_idx = next(x['index'] for x in o if x['type'] == 'path')
line2 = next(x for x in w if x['text'] == 'İkinci')
dest = os.path.join(work, 'cikti.pdf')
res = S.save({'source': src, 'dest': dest, 'rotations': {'0': 180},
              'items': [
                  {'page': 0, 'kind': 'highlight', 'rects': [{'x': tek['x'], 'y': tek['y'], 'w': tek['w'], 'h': tek['h']}], 'color': '#FFD400'},
                  {'page': 0, 'kind': 'ink', 'strokes': [[[100, 400], [150, 420], [200, 400]]], 'color': '#C42B1C', 'width': 2},
                  {'page': 0, 'kind': 'rect', 'rect': {'x': 50, 'y': 500, 'w': 100, 'h': 50}, 'color': '#0F6CBD'},
                  {'page': 0, 'kind': 'note', 'x': 500, 'y': 60, 'text': 'Kontrol et: Ø120'},
                  {'page': 0, 'kind': 'text', 'rect': {'x': 72, 'y': 600, 'w': 200, 'h': 20}, 'text': 'Eklenen not ğş', 'size': 11, 'color': '#000000'},
                  {'page': 0, 'kind': 'object_move', 'index': rect_idx, 'dx': 10, 'dy': 20},
                  {'page': 1, 'kind': 'correct', 'rect': {'x': line2['x'] - 1, 'y': line2['y'] - 1, 'w': 200, 'h': line2['h'] + 2}, 'text': 'Düzeltilmiş satır', 'size': 12},
              ]})
print(json.dumps(res['stats'], ensure_ascii=False))
assert res['stats']['annotations'] == 4 and res['stats']['moved'] == 1 and res['stats']['removed_text_objects'] >= 1, res
chk = pdfium.PdfDocument(dest)
p0 = chk[0]; assert p0.get_rotation() == 180
assert R.FPDFPage_GetAnnotCount(p0.raw) == 4
txt0 = p0.get_textpage().get_text_bounded(); assert all(x in txt0 for x in ('Eklenen', 'not', 'ğş')), ascii(txt0)
p1 = chk[1]; assert p1.get_rotation() == 90
txt1 = p1.get_textpage().get_text_bounded(); assert 'Düzeltilmiş' in txt1 and 'satır' in txt1 and 'İkinci' not in txt1 and 'Teklif' in txt1, ascii(txt1)
S.open_doc(dest); o2 = S.objects({'page': 0})['objects']
assert any(x['type'] == 'path' and abs(x['x'] - 310) < 1 and abs(x['y'] - (842 - 380 + 20)) < 1 for x in o2), o2
try:
    S.save({'source': src, 'dest': src, 'items': []}); raise AssertionError('kaynak üzerine yazıldı')
except ValueError: pass
print('PDF studyosu regresyonu gecti')
