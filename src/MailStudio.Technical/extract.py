"""Compact, text-only summaries of attachments for skills and AI agents.

No meshes, no images: only the facts a language model can reason about (texts, dimensions, sizes, PMI).
Every summary says how it was obtained, so OCR guesses are never mistaken for exact values."""
import os

MAX_TEXT = 60000


def _clip(text, limit):
    text = text or ''
    return text if len(text) <= limit else text[:limit] + '\n[...truncated]'


# ---------------------------------------------------------------- PDF
_ocr = None


def _ocr_page(page):
    global _ocr
    import numpy as np
    from rapidocr import RapidOCR
    if _ocr is None:
        _ocr = RapidOCR(params={'Global.log_level': 'error', 'EngineConfig.onnxruntime.intra_op_num_threads': 2,
                                'EngineConfig.onnxruntime.inter_op_num_threads': 1})
    bitmap = page.render(scale=min(3.0, 2600 / max(page.get_size())))
    pil = bitmap.to_pil()
    try:
        result = _ocr(np.asarray(pil))
        return '\n'.join(result.txts) if result.txts is not None else ''
    finally:
        pil.close(); bitmap.close()


def pdf_summary(path, ocr=True, max_pages=30, limit=MAX_TEXT):
    import pypdfium2 as pdfium
    doc = pdfium.PdfDocument(os.path.abspath(path))
    pages, used = [], 0
    try:
        for i in range(min(len(doc), max_pages)):
            page = doc[i]
            try:
                tp = page.get_textpage(); text = tp.get_text_range(); tp.close()
                method = 'text layer'
                if ocr and len(text.strip()) < 25 and i < 4:
                    text = _ocr_page(page); method = 'OCR (verify numbers on the drawing)'
                w, h = page.get_size()
            finally:
                page.close()
            text = _clip(text, max(500, limit - used)); used += len(text)
            pages.append({'page': i + 1, 'size_mm': [round(w / 72 * 25.4), round(h / 72 * 25.4)], 'method': method, 'text': text})
            if used >= limit: break
        return {'kind': 'pdf', 'pages': len(doc), 'content': pages}
    finally:
        doc.close()


# ---------------------------------------------------------------- STEP
def step_summary(path, pmi_reader=None):
    from OCP.STEPCAFControl import STEPCAFControl_Reader
    from OCP.TDocStd import TDocStd_Document
    from OCP.TCollection import TCollection_ExtendedString
    from OCP.Interface import Interface_Static
    from OCP.IFSelect import IFSelect_RetDone
    from OCP.GProp import GProp_GProps
    from OCP.BRepGProp import BRepGProp
    from OCP.TopExp import TopExp_Explorer
    from OCP.TopAbs import TopAbs_FACE
    from OCP.TopoDS import TopoDS
    from OCP.BRepAdaptor import BRepAdaptor_Surface
    from cad_document import read_assembly, compound
    from cad_analysis import bbox
    reader = STEPCAFControl_Reader(); reader.SetColorMode(True); reader.SetNameMode(True); reader.SetGDTMode(True)
    Interface_Static.SetCVal_s('xstep.cascade.unit', 'MM')
    if reader.ReadFile(os.path.abspath(path)) != IFSelect_RetDone: raise ValueError('STEP file could not be read.')
    doc = TDocStd_Document(TCollection_ExtendedString('MailStudio'))
    if not reader.Transfer(doc): raise ValueError('STEP geometry could not be transferred.')
    nodes, bodies = read_assembly(doc)
    if not bodies: raise ValueError('The STEP file has no transferable geometry.')

    def props(shape):
        v = GProp_GProps(); BRepGProp.VolumeProperties_s(shape, v)
        s = GProp_GProps(); BRepGProp.SurfaceProperties_s(shape, s)
        c = v.CentreOfMass()
        return round(v.Mass(), 3), round(s.Mass(), 3), [round(c.X(), 3), round(c.Y(), 3), round(c.Z(), 3)]

    def size(b): return [round(b[3] - b[0], 3), round(b[4] - b[1], 3), round(b[5] - b[2], 3)]

    out_bodies = []
    for b in bodies[:200]:
        box = bbox(b['shape']); vol, area, com = props(b['shape'])
        kinds = {}
        ex = TopExp_Explorer(b['shape'], TopAbs_FACE)
        while ex.More():
            k = str(BRepAdaptor_Surface(TopoDS.Face_s(ex.Current())).GetType()).split('_')[-1].split('.')[-1]
            kinds[k] = kinds.get(k, 0) + 1; ex.Next()
        out_bodies.append({'name': b['name'], 'size_xyz_mm': size(box), 'volume_mm3': vol, 'surface_mm2': area,
                           'center_of_mass_mm': com, 'face_types': kinds, 'cylindrical_faces': kinds.get('Cylinder', 0)})
    whole = compound([b['shape'] for b in bodies]); box = bbox(whole); vol, area, _ = props(whole)
    pmi = []
    if pmi_reader is not None:
        try:
            for item in pmi_reader(doc)[:300]:
                pmi.append({k: v for k, v in item.items() if k not in ('presentation', 'point', 'point2', 'text_point', 'geometry_labels', 'label')})
        except Exception as ex:
            pmi = [{'kind': 'PMI warning', 'detail': str(ex)}]
    return {'kind': 'step', 'unit': 'mm', 'components': [n['name'] for n in nodes][:200], 'body_count': len(bodies),
            'overall_size_xyz_mm': size(box), 'total_volume_mm3': vol, 'total_surface_mm2': area,
            'bodies': out_bodies, 'pmi': pmi,
            'note': 'Volumes are exact B-Rep values. Weight in grams = volume_mm3 / 1000 * density_g_cm3.'}


# ---------------------------------------------------------------- DXF / DWG
def dxf_summary(path):
    import ezdxf
    from ezdxf import recover
    load = os.path.abspath(path); converted = None
    if load.lower().endswith('.dwg'):
        import cad2d
        load, converted = cad2d.dwg_to_dxf(load)
    enc = None
    try:
        raw = open(load, 'rb').read()
        if any(b > 127 for b in raw): raw.decode('utf-8'); enc = 'utf-8'
    except UnicodeDecodeError:
        enc = None
    try: doc = ezdxf.readfile(load, encoding=enc) if enc else ezdxf.readfile(load)
    except Exception: doc, _ = recover.readfile(load)
    texts, dims, counts = [], [], {}
    for layout in doc.layouts:
        for e in layout:
            t = e.dxftype(); counts[t] = counts.get(t, 0) + 1
            try:
                if t == 'TEXT': texts.append({'layer': e.dxf.layer, 'text': e.dxf.text})
                elif t == 'MTEXT': texts.append({'layer': e.dxf.layer, 'text': e.plain_text()})
                elif t == 'DIMENSION':
                    try: m = round(float(e.get_measurement()), 4)
                    except Exception: m = None
                    override = e.dxf.get('text', '')
                    dims.append({'layer': e.dxf.layer, 'measurement': m, 'text': override if override not in ('', '<>') else None})
                elif t == 'INSERT':
                    for a in e.attribs: texts.append({'layer': a.dxf.layer, 'block': e.dxf.name, 'tag': a.dxf.tag, 'text': a.dxf.text})
            except Exception:
                pass
    texts = [x for x in texts if (x.get('text') or '').strip()][:2000]
    try: units = {0: 'unitless', 1: 'inch', 4: 'mm', 5: 'cm', 6: 'm'}.get(doc.header.get('$INSUNITS', 0), 'other')
    except Exception: units = 'unknown'
    return {'kind': 'dxf', 'version': doc.dxfversion, 'converted_from_dwg_by': converted, 'units': units,
            'layers': [l.dxf.name for l in doc.layers][:300], 'entity_counts': counts,
            'texts': texts, 'dimensions': dims[:2000]}


def dispatch(op, a, pmi_reader=None):
    if op == 'extract_pdf': return pdf_summary(a['path'], a.get('ocr', True), int(a.get('max_pages', 30)))
    if op == 'extract_step': return step_summary(a['path'], pmi_reader)
    if op == 'extract_dxf': return dxf_summary(a['path'])
    return None
