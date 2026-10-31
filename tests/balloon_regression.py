"""Balloon extraction on the demo drawing: python tests/balloon_regression.py"""
from pathlib import Path
import sys, os, tempfile
root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / 'src' / 'MailStudio.Technical'))
import balloon as b
ok = 0
def check(cond, what):
    global ok
    if not cond: raise SystemExit('FAIL ' + what)
    ok += 1; print('PASS', what)
c = b.classify
check(c('Ø20 ±0.1')['upper'] == 0.1 and c('Ø20 ±0.1')['type'] == 'diameter', 'diameter with ± tolerance')
check(c('25 +0.1/-0.05')['lower'] == 0.05, 'asymmetric tolerance')
check(c('M10x1.5-6H')['type'] == 'thread' and c('M10x1.5-6H')['pitch'] == 1.5, 'thread with pitch')
check(c('Ra 3.2')['type'] == 'roughness', 'surface roughness')
check(c('45° ±0.5°')['type'] == 'angle', 'angle')
check(c('Ø40 H7')['fit'] == 'H7', 'fit')
check(c('SCALE 1:2') is None and c('NW-1042') is None and c('SHEET 1/1') is None, 'title block and part number ignored')
check(c('TOLERANCES ISO 2768-mK')['type'] == 'general', 'general tolerance note')
check(b._ocr_fix('0110.') == 'Ø110' and b._ocr_fix('8× 020 THRU') == '8x Ø20 THRU' and b._ocr_fix('0.5') == '0.5' and 'ISO 2768' in b._ocr_fix('ISO 2768-mK'), 'OCR Ø repair')
check(b.iso2768(110, 'm') == 0.3 and b.iso2768(2, 'f') == 0.05 and b.iso2768(0.2, 'm') is None, 'ISO 2768-1 table')
r = b.extract({'path': str(root / 'src/MailStudio/Demo/flange-DN100-revC.pdf')})
import json as _json
check(len(_json.dumps(r, allow_nan=False)) > 100, 'result is plain JSON (what the worker sends)')
req = [i['requirement'] for i in r['items']]
check(r['title'].get('part_number') == 'NW-1042', 'part number from the title block')
check(r['ocr'] and all(i['review'] for i in r['items']), 'scanned demo drawing read with OCR and flagged for review')
check({'Ø110', 'Ø220', '220', '60', 'Ra 1.6'} <= set(req) and any(t.startswith('8x Ø20') for t in req), f'demo characteristics found {req}')
check([i['id'] for i in r['items']] == list(range(1, len(r['items']) + 1)) and all(i.get('balloon') for i in r['items']), 'numbered and placed')
r2 = b.extract({'path': str(root / 'src/MailStudio/Demo/flange-DN100-revC.pdf'), 'general_class': 'm'})
d = next(i for i in r2['items'] if i['requirement'] == 'Ø110')
check(d['min'] == 109.7 and d['max'] == 110.3, 'ISO 2768-m applied when the class is given')
dest = os.path.join(tempfile.mkdtemp(), 'b.pdf')
s = b.save({'path': str(root / 'src/MailStudio/Demo/flange-DN100-revC.pdf'), 'dest': dest, 'items': r['items']})
check(open(dest, 'rb').read(5) == b'%PDF-' and s['bytes'] > 5000, 'ballooned PDF written')
try: b.save({'path': dest, 'dest': dest, 'items': []}); check(False, 'source not overwritten')
except ValueError: check(True, 'source not overwritten')
# a drawing with a real text layer: no OCR, values exact, general tolerance note applied
import pypdfium2 as pdfium, pypdfium2.raw as R
from pdf_studio import _ws
doc = pdfium.PdfDocument.new(); page = doc.new_page(595, 420)
for txt, x, y in [('Ø20 ±0.1', 100, 300), ('25 +0.1/-0.05', 300, 300), ('M8x1.25-6H', 100, 200), ('120', 300, 200), ('TOLERANCES ISO 2768-mK', 100, 60), ('SCALE 1:1', 400, 40)]:
    o = R.FPDFPageObj_NewTextObj(doc.raw, b'Helvetica', 10.0); R.FPDFText_SetText(o, _ws(txt)); R.FPDFPageObj_Transform(o, 1, 0, 0, 1, x, y); R.FPDFPage_InsertObject(page.raw, o)
R.FPDFPage_GenerateContent(page.raw); text_pdf = os.path.join(tempfile.mkdtemp(), 't.pdf'); doc.save(text_pdf); doc.close()
r = b.extract({'path': text_pdf})
by = {i['requirement']: i for i in r['items']}
check(not r['ocr'] and not any(i['review'] for i in r['items']) and set(by) == {'Ø20 ±0.1', '25 +0.1/-0.05', 'M8x1.25-6H', '120'}, f'text layer read exactly {sorted(by)}')
check(by['Ø20 ±0.1']['min'] == 19.9 and by['25 +0.1/-0.05']['max'] == 25.1, 'drawing tolerances kept')
check(r['general_class'] == 'm' and by['120']['min'] == 119.7 and by['120']['tolerance_source'] == 'ISO 2768-m', 'general tolerance note applied to the untoleranced size')
print(f'RESULT {ok} passed')
