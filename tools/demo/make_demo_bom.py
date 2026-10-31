"""Demo parts list PDF (text layer + ruling lines) for the "PDF table to Excel" feature.
python tools/demo/make_demo_bom.py  → src/MailStudio/Demo/NW-1042-parts-list.pdf"""
import os, sys
root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(root, 'src', 'MailStudio.Technical'))
import pypdfium2 as pdfium, pypdfium2.raw as R
from pdf_studio import _ws

ROWS = [['Pos', 'Part No', 'Description', 'Material', 'Qty'],
        ['1', 'NW-1042', 'Flange DN100 PN16', 'S235JR', '2'],
        ['2', 'NW-1043', 'Gasket DN100', 'EPDM', '2'],
        ['3', 'ISO 4017 M16x60', 'Hex bolt', '8.8 zinc', '16'],
        ['4', 'ISO 4032 M16', 'Hex nut', '8', '16'],
        ['5', 'NW-2001', 'Bracket rev C', 'Al 7075-T6', '1'],
        ['6', 'NW-2002', 'Spacer (customer supplied)', '', '4'],
        ['7', 'DIN 125 A17', 'Washer', 'St zinc', '32']]


def make(path, ruled=True, rows=ROWS):
    doc = pdfium.PdfDocument.new(); page = doc.new_page(595, 842); H = 842
    def text(t, x, y, size=9):
        o = R.FPDFPageObj_NewTextObj(doc.raw, b'Helvetica', float(size)); R.FPDFText_SetText(o, _ws(t))
        R.FPDFPageObj_Transform(o, 1, 0, 0, 1, x, H - y); R.FPDFPage_InsertObject(page.raw, o)
    def line(x1, y1, x2, y2):
        o = R.FPDFPageObj_CreateNewPath(x1, H - y1); R.FPDFPath_LineTo(o, x2, H - y2)
        R.FPDFPageObj_SetStrokeColor(o, 0, 0, 0, 255); R.FPDFPageObj_SetStrokeWidth(o, 0.5); R.FPDFPath_SetDrawMode(o, 0, True)
        R.FPDFPage_InsertObject(page.raw, o)
    text('PARTS LIST - NW-1042 FLANGE ASSEMBLY (DEMO)', 60, 60, 14)
    text('Northwind Machining (demo). Quantities per assembly.', 60, 80)
    cols = [60, 100, 200, 360, 470]
    y = 120
    for r in rows:
        for x, c in zip(cols, r):
            if c: text(c, x + 3, y)
        y += 18
    if ruled:
        top = 120 - 12; bottom = 120 + 18 * len(rows) - 12
        for x in cols + [560]: line(x, top, x, bottom)
        for i in range(len(rows) + 1): line(60, top + 18 * i, 560, top + 18 * i)
    text('Notes: bolts torqued to 210 Nm. Not for production - demo document.', 60, y + 30)
    R.FPDFPage_GenerateContent(page.raw); doc.save(path); doc.close()


if __name__ == '__main__':
    out = os.path.join(root, 'src', 'MailStudio', 'Demo', 'NW-1042-parts-list.pdf')
    make(out); print(out)
