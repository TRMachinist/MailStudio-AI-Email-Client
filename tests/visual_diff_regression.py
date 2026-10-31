"""Visual revision overlay on the demo files: python tests/visual_diff_regression.py"""
from pathlib import Path
import sys
root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / 'src' / 'MailStudio.Technical'))
import visual_diff as v
demo = root / 'src' / 'MailStudio' / 'Demo'
ok = 0
def check(cond, what):
    global ok
    if not cond: raise SystemExit('FAIL ' + what)
    ok += 1; print('PASS', what)
def run(o, n): return v.dispatch('visual_diff', {'old': str(demo / o), 'new': str(demo / n)})

r = run('flange-DN100-revB.pdf', 'flange-DN100-revC.pdf')
check(not r['identical'] and r['removed_px'] > 0 and r['added_px'] > 0 and len(r['regions']) >= 8, f"PDF rev B→C: {len(r['regions'])} regions")
check(run('flange-DN100-revC.pdf', 'flange-DN100-revC.pdf')['identical'], 'PDF identical page has no regions')
r = run('flange-DN100-revB.dxf', 'flange-DN100-revC.dxf')
holes = [g for g in r['regions'] if g['kind'] == 'changed' and 80 <= g['w'] <= 160 and 80 <= g['h'] <= 160]
check(len(holes) == 8, f'DXF: the 8 enlarged bolt holes are separate changed regions ({len(holes)})')
check(run('flange-DN100-revC.dxf', 'flange-DN100-revC.dxf')['identical'], 'DXF identical drawing has no regions')
r = run('bracket-revB.stp', 'bracket-revC.stp')
check(r['added_faces'] == 1 and r['removed_faces'] == 1 and r['changes'][0]['type'] == 'Cylinder', 'STEP: only the enlarged bore is new / removed')
check(run('bracket-revC.stp', 'bracket-revC.stp')['identical'], 'STEP identical model has no changed faces')
try: v.dispatch('visual_diff', {'old': 'a.xlsx', 'new': 'b.xlsx'}); check(False, 'unsupported type rejected')
except ValueError: check(True, 'unsupported type rejected')
print(f'RESULT {ok} passed')
