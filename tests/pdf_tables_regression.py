"""PDF tables: python tests/pdf_tables_regression.py"""
from pathlib import Path
import sys, os, tempfile, json
root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / 'src' / 'MailStudio.Technical')); sys.path.insert(0, str(root / 'tools' / 'demo'))
import pdf_tables as t, make_demo_bom as m
ok = 0
def check(cond, what):
    global ok
    if not cond: raise SystemExit('FAIL ' + what)
    ok += 1; print('PASS', what)
r = t.dispatch('pdf_tables', {'path': str(root / 'src/MailStudio/Demo/NW-1042-parts-list.pdf')})
check(len(json.dumps(r)) > 0 and len(r['tables']) == 1 and not r['ocr'], 'one table on the demo parts list, text layer')
rows = r['tables'][0]['rows']
check(rows == m.ROWS, 'every cell exact, including the empty one and "8.8 zinc"')
tmp = tempfile.mkdtemp()
m.make(os.path.join(tmp, 'n.pdf'), ruled=False)
check(t.dispatch('pdf_tables', {'path': os.path.join(tmp, 'n.pdf')})['tables'][0]['rows'] == m.ROWS, 'same table without ruling lines (whitespace columns)')
r = t.dispatch('pdf_tables', {'path': str(root / 'src/MailStudio/Demo/flange-DN100-revC.pdf')})
check(r['tables'] == [], 'a drawing without a table gives no table')
print(f'RESULT {ok} passed')
