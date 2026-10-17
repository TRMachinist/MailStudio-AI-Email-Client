"""Synthetic STEP part for the demo mailbox (L-bracket with holes and fillets). Needs OCP (cadquery-ocp).
Usage: python make_demo_step.py <output.stp>"""
import sys
from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox, BRepPrimAPI_MakeCylinder
from OCP.BRepAlgoAPI import BRepAlgoAPI_Fuse, BRepAlgoAPI_Cut
from OCP.BRepFilletAPI import BRepFilletAPI_MakeFillet
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_EDGE
from OCP.TopoDS import TopoDS
from OCP.BRepAdaptor import BRepAdaptor_Curve
from OCP.gp import gp_Pnt, gp_Ax2, gp_Dir
from OCP.STEPControl import STEPControl_Writer, STEPControl_AsIs
from OCP.Interface import Interface_Static

out = sys.argv[1] if len(sys.argv) > 1 else 'bracket-revC.stp'
base = BRepPrimAPI_MakeBox(gp_Pnt(0, 0, 0), 120, 80, 10).Shape()
wall = BRepPrimAPI_MakeBox(gp_Pnt(0, 0, 10), 10, 80, 60).Shape()
shape = BRepAlgoAPI_Fuse(base, wall).Shape()
rib = BRepPrimAPI_MakeBox(gp_Pnt(10, 35, 10), 30, 10, 30).Shape()
shape = BRepAlgoAPI_Fuse(shape, rib).Shape()
for x, y in [(30, 18), (100, 18), (30, 62), (100, 62)]:
    shape = BRepAlgoAPI_Cut(shape, BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(x, y, -1), gp_Dir(0, 0, 1)), 3.4, 12).Shape()).Shape()
for y, z in [(20, 45), (60, 45)]:
    shape = BRepAlgoAPI_Cut(shape, BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(-1, y, z), gp_Dir(1, 0, 0)), 4.5, 12).Shape()).Shape()
shape = BRepAlgoAPI_Cut(shape, BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(70, 40, -1), gp_Dir(0, 0, 1)), 12.5, 12).Shape()).Shape()
# round the outer vertical edges of the base
fillet = BRepFilletAPI_MakeFillet(shape)
exp = TopExp_Explorer(shape, TopAbs_EDGE)
while exp.More():
    e = TopoDS.Edge_s(exp.Current()); c = BRepAdaptor_Curve(e)
    p1, p2 = c.Value(c.FirstParameter()), c.Value(c.LastParameter())
    if abs(p1.X() - 120) < 1e-6 and abs(p2.X() - 120) < 1e-6 and abs(p1.Z() - p2.Z()) > 9 and (abs(p1.Y()) < 1e-6 or abs(p1.Y() - 80) < 1e-6):
        fillet.Add(8.0, e)
    exp.Next()
shape = fillet.Shape()
Interface_Static.SetCVal_s('write.step.schema', 'AP214IS')
from OCP.TDocStd import TDocStd_Document
from OCP.TCollection import TCollection_ExtendedString
from OCP.XCAFDoc import XCAFDoc_DocumentTool, XCAFDoc_ColorType
from OCP.TDataStd import TDataStd_Name
from OCP.STEPCAFControl import STEPCAFControl_Writer
from OCP.Quantity import Quantity_Color, Quantity_TOC_RGB
doc = TDocStd_Document(TCollection_ExtendedString('MDTV-XCAF'))
st = XCAFDoc_DocumentTool.ShapeTool_s(doc.Main()); ct = XCAFDoc_DocumentTool.ColorTool_s(doc.Main())
label = st.AddShape(shape, False)
TDataStd_Name.Set_s(label, TCollection_ExtendedString('BRACKET REV C'))
ct.SetColor(label, Quantity_Color(0.72, 0.78, 0.86, Quantity_TOC_RGB), XCAFDoc_ColorType.XCAFDoc_ColorSurf)
w = STEPCAFControl_Writer(); w.SetNameMode(True); w.SetColorMode(True); w.Transfer(doc, STEPControl_AsIs); w.Write(out)
print('ok', out)
