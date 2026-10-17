"""2D CAD regression: generated DXF (line, arc, circle, polyline, text, dimension, hatch, block) → record groups; optional DWG."""
import os, sys, json, subprocess, tempfile, time
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'src', 'MailStudio.Technical'))
import ezdxf
from ezdxf.enums import TextEntityAlignment
import cad2d

work = tempfile.mkdtemp(prefix='cad2d-')
dxf = os.path.join(work, 'flans.dxf')
d = ezdxf.new('R2018', setup=True); d.header['$INSUNITS'] = 4
d.layers.add('KONTUR', color=7); d.layers.add('OLCU', color=1); d.layers.add('TARAMA', color=3); d.layers.add('GIZLI', color=5).off()
m = d.modelspace()
m.add_lwpolyline([(0, 0), (120, 0), (120, 80), (0, 80)], close=True, dxfattribs={'layer': 'KONTUR'})
m.add_circle((60, 40), 20, dxfattribs={'layer': 'KONTUR'})
m.add_arc((60, 40), 30, 0, 90, dxfattribs={'layer': 'KONTUR'})
m.add_line((0, 100), (120, 100), dxfattribs={'layer': 'GIZLI'})
m.add_text('FLANŞ Ø120 h7', height=5, dxfattribs={'layer': 'KONTUR'}).set_placement((5, 85), align=TextEntityAlignment.LEFT)
m.add_mtext('Malzeme: Al 7075-T6\\PAdet: 25', dxfattribs={'layer': 'KONTUR', 'char_height': 3.5, 'insert': (70, 95)})
dim = m.add_linear_dim(base=(0, -15), p1=(0, 0), p2=(120, 0), dxfattribs={'layer': 'OLCU'}); dim.render()
h = m.add_hatch(color=3, dxfattribs={'layer': 'TARAMA'}); h.paths.add_polyline_path([(130, 0), (170, 0), (170, 40), (130, 40)], is_closed=True)
h.paths.add_polyline_path([(140, 10), (160, 10), (160, 30), (140, 30)], is_closed=True)
blk = d.blocks.new('DELIK'); blk.add_circle((0, 0), 3); blk.add_attdef('NO', (4, 4), dxfattribs={'height': 2.5})
ins = m.add_blockref('DELIK', (20, 20)); ins.add_auto_attribs({'NO': 'D1'})
d.saveas(dxf)

info = cad2d.open_file({'path': dxf})
assert info['units'] == 'mm' and 'Model' in info['layouts'], info
names = {l['name']: l for l in info['layers']}
assert names['GIZLI']['on'] is False and names['OLCU']['color'] == '#FF0000', names
t0 = time.time(); r = cad2d.render({'layout': 'Model'}); dt = time.time() - t0
layers = {g['layer'] for g in r['groups']}
assert {'KONTUR', 'OLCU', 'TARAMA'} <= layers, layers
fills = [f for g in r['groups'] if g['layer'] == 'TARAMA' for f in g['fills']]
assert any(len(f) == 2 for f in fills), 'tarama deliği iki halka olarak gelmeli'
x0, y0, x1, y1 = r['bbox']; assert x0 <= 0 and x1 >= 170 and y0 <= -15, r['bbox']
txt = [t['text'] for t in r['texts']]
assert 'FLANŞ Ø120 h7' in txt and any('Al 7075' in t for t in txt) and 'D1' in txt and any(t.startswith('120') for t in txt), txt
print(json.dumps({'groups': len(r['groups']), 'records': r['count'], 'texts': len(txt), 'seconds': round(dt, 2)}, ensure_ascii=False))
conv = cad2d.converters(); print('dönüştürücüler', conv)
if conv['libredwg'] or conv['oda']:
    dwg = os.path.join(work, 'flans.dwg')
    tool = os.path.join(os.path.dirname(conv['libredwg']), 'dxf2dwg.exe') if conv['libredwg'] else None
    if tool and os.path.exists(tool):
        subprocess.run([tool, '-y', '-o', dwg, dxf], capture_output=True, cwd=os.path.dirname(tool))
    if os.path.exists(dwg):
        info2 = cad2d.open_file({'path': dwg}); r2 = cad2d.render({'layout': 'Model'})
        assert info2['converted'] and r2['count'] > 5, (info2, r2['count'])
        print('DWG', info2['converted'], r2['count'], 'kayıt')
print('CAD 2B regresyonu gecti')
