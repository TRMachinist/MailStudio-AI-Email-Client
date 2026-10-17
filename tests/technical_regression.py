from pathlib import Path
import sys,subprocess,json,math,tempfile,os
import shutil
from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox,BRepPrimAPI_MakeCylinder
from OCP.STEPControl import STEPControl_Writer,STEPControl_AsIs
from OCP.STEPCAFControl import STEPCAFControl_Writer
from OCP.Interface import Interface_Static
from OCP.StepData import StepData_ConfParameters
from OCP.IFSelect import IFSelect_RetDone
from OCP.TDocStd import TDocStd_Document
from OCP.TCollection import TCollection_ExtendedString
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_FACE
from OCP.gp import gp_Pnt
from OCP.gp import gp_Ax2,gp_Dir
from OCP.BRepBuilderAPI import BRepBuilderAPI_MakePolygon
from OCP.BRepBuilderAPI import BRepBuilderAPI_Transform
from OCP.gp import gp_Ax1
from OCP.TCollection import TCollection_HAsciiString
from OCP.StdPrs import StdPrs_BRepFont,StdPrs_BRepTextBuilder
from OCP.NCollection import NCollection_Utf8String
from OCP.BRep import BRep_Builder
from OCP.TopoDS import TopoDS_Compound
from OCP.gp import gp_Ax3
from OCP.gp import gp_Trsf,gp_Vec
from OCP.TopLoc import TopLoc_Location
from OCP.XCAFDoc import XCAFDoc_DocumentTool,XCAFDoc_Dimension
from OCP.XCAFDimTolObjects import XCAFDimTolObjects_DimensionObject,XCAFDimTolObjects_DimensionType
from PIL import Image,ImageDraw,ImageFont

root=Path(__file__).resolve().parents[1];data=Path(tempfile.mkdtemp(prefix='MailStudio-technical-'))
step=data/'box-ap242.stp';cylinder=data/'cylinder.stp'
writer=STEPCAFControl_Writer()
assert Interface_Static.SetCVal_s('write.step.schema','AP242DIS')
writer=STEPCAFControl_Writer()
doc=TDocStd_Document(TCollection_ExtendedString('test'));st=XCAFDoc_DocumentTool.ShapeTool_s(doc.Main());box=BRepPrimAPI_MakeBox(20,30,40).Shape();label=st.AddShape(box,False)
ex=TopExp_Explorer(box,TopAbs_FACE);first=st.AddSubShape(label,ex.Current());ex.Next();second=st.AddSubShape(label,ex.Current())
dt=XCAFDoc_DocumentTool.DimTolTool_s(doc.Main());dimension=dt.AddDimension();attr=XCAFDoc_Dimension.Set_s(dimension);obj=XCAFDimTolObjects_DimensionObject();obj.SetType(XCAFDimTolObjects_DimensionType.XCAFDimTolObjects_DimensionType_Location_LinearDistance);obj.SetValue(20);obj.SetPoint(gp_Pnt(0,0,0));obj.SetPoint2(gp_Pnt(20,0,0));attr.SetObject(obj);dt.SetDimension(first,second,dimension)
# A real file-owned graphical annotation wire. Keep its placement outside the part
# so tests also catch dropped transforms and fit-to-part-only clipping.
annotation=BRepBuilderAPI_MakePolygon()
for pnt in ((0,0,40),(0,-10,40),(20,-10,40),(20,0,40)):annotation.Add(gp_Pnt(*pnt))
font=StdPrs_BRepFont(NCollection_Utf8String(r'C:\Windows\Fonts\arial.ttf'),3.0)
glyphs=StdPrs_BRepTextBuilder().Perform(font,NCollection_Utf8String('20 ±0.1'),gp_Ax3(gp_Pnt(4,-8,40),gp_Dir(0,0,1)))
builder=BRep_Builder();presentation=TopoDS_Compound();builder.MakeCompound(presentation);builder.Add(presentation,annotation.Wire());builder.Add(presentation,glyphs)
obj.SetPlane(gp_Ax2(gp_Pnt(0,0,40),gp_Dir(0,0,1)));obj.SetPresentation(presentation,TCollection_HAsciiString('20 mm graphical dimension'));attr.SetObject(obj)
parameters=StepData_ConfParameters();parameters.InitFromStatic();writer.SetDimTolMode(True);assert writer.Transfer(doc,parameters);assert writer.Write(str(step))==IFSelect_RetDone
inch=data/'inch-ap242.stp';assert Interface_Static.SetCVal_s('write.step.unit','INCH');parameters.InitFromStatic();inch_writer=STEPCAFControl_Writer();assert inch_writer.Transfer(doc,parameters);assert inch_writer.Write(str(inch))==IFSelect_RetDone
assert Interface_Static.SetCVal_s('write.step.unit','MM')
assembly_doc=TDocStd_Document(TCollection_ExtendedString('assembly'));ast=XCAFDoc_DocumentTool.ShapeTool_s(assembly_doc.Main());assembly=ast.NewShape();part=ast.AddShape(BRepPrimAPI_MakeBox(20,30,40).Shape(),False);ast.AddComponent(assembly,part,TopLoc_Location());translation=gp_Trsf();translation.SetTranslation(gp_Vec(100,0,0));ast.AddComponent(assembly,part,TopLoc_Location(translation));ast.UpdateAssemblies();aw=STEPCAFControl_Writer();parameters.InitFromStatic();assert aw.Transfer(assembly_doc,parameters);assembly_file=data/'assembly-ap242.stp';assert aw.Write(str(assembly_file))==IFSelect_RetDone
w=STEPControl_Writer();w.Transfer(BRepPrimAPI_MakeCylinder(5,50).Shape(),STEPControl_AsIs);assert w.Write(str(cylinder))==IFSelect_RetDone
tilted=data/'tilted.stp';rotation=gp_Trsf();rotation.SetRotation(gp_Ax1(gp_Pnt(0,0,0),gp_Dir(0,1,0)),math.pi/6);tw=STEPControl_Writer();tw.Transfer(BRepBuilderAPI_Transform(box,rotation,True).Shape(),STEPControl_AsIs);assert tw.Write(str(tilted))==IFSelect_RetDone
unicode_step=data/'İmalat_ölçü.stp';shutil.copyfile(step,unicode_step)

# Native text fixture, built without an external PDF-writing dependency.
objects=[b'<< /Type /Catalog /Pages 2 0 R >>',b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>']
stream=b'BT /F1 22 Tf 50 700 Td (Drawing: 25 PCS, diameter 20 mm) Tj ET';objects.append(b'<< /Length '+str(len(stream)).encode()+b' >>\nstream\n'+stream+b'\nendstream')
content=bytearray(b'%PDF-1.4\n');offsets=[]
for i,o in enumerate(objects,1):offsets.append(len(content));content.extend(f'{i} 0 obj\n'.encode()+o+b'\nendobj\n')
xref=len(content);content.extend(f'xref\n0 {len(objects)+1}\n0000000000 65535 f \n'.encode());content.extend(b''.join(f'{n:010d} 00000 n \n'.encode() for n in offsets));content.extend(f'trailer\n<< /Size {len(objects)+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF'.encode());pdf=data/'native.pdf';pdf.write_bytes(content)
image=Image.new('RGB',(1200,700),'white');draw=ImageDraw.Draw(image);font=ImageFont.truetype(r'C:\Windows\Fonts\arial.ttf',54);draw.text((80,120),'PART 12345 - 25 PCS',fill='black',font=font);draw.text((80,240),'DIAMETER 20 mm',fill='black',font=font);scanned=data/'scan.pdf';image.save(scanned,'PDF',resolution=150)

target=sys.argv[1:] or [sys.executable,str(root/'src/MailStudio.Technical/worker.py')]
child_env=os.environ.copy();child_env['PATH']=str(Path(child_env.get('SystemRoot',r'C:\Windows'))/'System32');child_env.pop('PYTHONHOME',None);child_env.pop('PYTHONPATH',None)
worker_process=subprocess.Popen(target,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,encoding='utf-8',env=child_env,creationflags=subprocess.CREATE_NO_WINDOW)
counter=0;passed=[]
def call(op,args={}):
    global counter
    counter+=1;worker_process.stdin.write(json.dumps({'id':counter,'operation':op,'args':args})+'\n');worker_process.stdin.flush();line=worker_process.stdout.readline()
    if not line:raise RuntimeError(f'Worker exited ({worker_process.poll()}) during {op}')
    result=json.loads(line)
    if 'error'in result:raise RuntimeError(result['error'])
    return result['result']
def check(value,label):
    assert value,label;passed.append(label);print('PASS',label,flush=True)
try:
    check(call('ping')['ready'],'worker JSON protocol')
    model=call('step_open',{'path':str(step)});check(model['face_count']==6 and model['unit']=='mm','AP242 B-Rep box import and units');check(len(model['pmi'])>=1 and any(x.get('Value')==20 for x in model['pmi']),'AP242 semantic PMI dimension import')
    graphical=[x['presentation'] for x in model['pmi'] if x.get('presentation')];check(model['graphical_pmi_count']>=1 and sum(len(x['lines']) for x in graphical)>=3,'AP242 file-owned graphical annotation curves survive STEP roundtrip')
    check(any(abs(p[1]+10)<1e-6 and abs(p[2]-40)<1e-6 for x in graphical for line in x['lines'] for p in line),'graphical PMI retains annotation plane coordinates')
    check(sum(len(x['lines']) for x in graphical)>15 or any(x['faces'] for x in graphical),'graphical PMI preserves file-owned text and plus-minus glyph geometry')
    check(len(model['edges'])==12 and len(model['vertices'])==8,'CAD box exports twelve true edges and eight native vertices')
    zfaces=[]
    for f in model['faces']:
        z=f['positions'][2::3]
        if max(z)-min(z)<1e-6:zfaces.append((sum(z)/len(z),f['id']))
    zfaces.sort();distance=call('measure',{'first':zfaces[0][1],'second':zfaces[-1][1],'points':[[3,4,.4],[3,4,39.6]],'direction':[0,0,2]})
    check(abs(distance['minimum_distance_mm']-40)<1e-6 and abs(distance['point_distance_mm']-40)<1e-6 and abs(distance['signed_vector_distance_mm']-40)<1e-6,'exact face distance and projected vector measurement')
    minimum_vector=call('measure',{'selections':[{'kind':'face','id':zfaces[0][1],'point':[2,3,0]},{'kind':'face','id':zfaces[-1][1],'point':[12,13,40]}],'direction':[0,0,1],'distance_mode':'minimum'});check(abs(math.dist(*minimum_vector['vector_points'])-40)<1e-6 and minimum_vector['point_distance_mm']>40,'minimum-distance vector uses native closest points instead of unrelated click positions')
    normal=call('inspect',{'kind':'face','id':zfaces[-1][1],'point':[3,4,39.6]});check(abs(normal['normal'][2]-1)<1e-7 and abs(normal['area_mm2']-600)<1e-6,'face selection returns exact outward normal and CAD area')
    native_edge=next(e for e in model['edges'] if abs(math.dist(e['points'][0],e['points'][-1])-40)<1e-6)
    edge_info=call('inspect',{'kind':'edge','id':native_edge['id'],'point':native_edge['points'][0]});check(abs(edge_info['length_mm']-40)<1e-6 and abs(edge_info['direction'][2])>.9999,'edge selection returns true curve length and direction')
    same_face=call('measure',{'selections':[{'kind':'facepoint','id':zfaces[-1][1],'point':[2,3,39.5]},{'kind':'facepoint','id':zfaces[-1][1],'point':[8,11,40.6]}],'direction':[0,1,0]});check(abs(same_face['point_distance_mm']-10)<1e-6 and abs(same_face['signed_vector_distance_mm']-8)<1e-6,'two arbitrary points on one CAD face are projected and measured with a vector')
    corners=[next(v for v in model['vertices'] if math.dist(v['point'],p)<1e-6) for p in ([0,0,0],[20,30,40])]
    diag=call('measure',{'selections':[{'kind':'vertex','id':v['id']} for v in corners],'direction':[0,0,1]});check(abs(diag['point_distance_mm']-math.sqrt(2900))<1e-6 and diag['delta_mm']==[20,30,40],'native vertex measurement reports exact diagonal and XYZ components')
    edge_vertex=call('measure',{'selections':[{'kind':'edge','id':native_edge['id'],'point':native_edge['points'][0]},{'kind':'vertex','id':corners[0]['id']}],'direction':[1,0,0]});check(edge_vertex['minimum_distance_mm']>=0 and math.isfinite(edge_vertex['point_distance_mm']),'mixed edge and vertex selections use native CAD extrema')
    envelope=call('envelope',{'direction':[0,0,1],'offset':[1,2,3]});check(all(abs(a-b)<1e-5 for a,b in zip(envelope['box_size'],[22,34,46])),'CAD box offsets in mm');check(abs(envelope['cylinder']['height']-46)<1e-5 and envelope['cylinder']['radius']>math.hypot(10,15),'conservative cylinder includes full box and offsets')
    oblique=call('envelope',{'direction':[1,1,0],'offset':[0,0,0]});check(abs(oblique['box_size'][2]-50/math.sqrt(2))<1e-4,'oriented bounds use selected vector')
    stock=call('envelope',{'stock_type':'box','direction':normal['normal'],'box_offsets':[1,2,3,4,5,6],'uniform_offset':1});check(all(abs(x-y)<1e-5 for x,y in zip(stock['box_size'],[25,39,53])),'surface-selected direction and all six stock faces receive global offset')
    shrink=call('envelope',{'stock_type':'box','box_offsets':[2]*6,'uniform_offset':-1});check(all(abs(x-y)<1e-5 for x,y in zip(shrink['box_size'],[22,32,42])),'negative global stock offset shrinks existing allowance without crossing the part')
    asymmetric=call('envelope',{'stock_type':'cylinder','cylinder_offsets':[2,3,5],'uniform_offset':1});check(abs(asymmetric['cylinder']['height']-50)<1e-5 and abs(asymmetric['cylinder']['center'][2]-21)<1e-5,'cylinder applies independent end offsets and global stock offset')
    cyl=call('step_open',{'path':str(cylinder)});check(any(abs(f.get('radius',-1)-5)<1e-6 for f in cyl['faces']),'analytic cylinder radius remains available')
    native_cyl=next(f for f in cyl['faces'] if f['type']=='Cylinder');cyl_info=call('inspect',{'kind':'face','id':native_cyl['id'],'point':[5,0,25]});check(abs(cyl_info['axis'][2]-1)<1e-6 and abs(cyl_info['normal'][0]-1)<1e-6,'cylindrical face exposes selectable native axis and point normal')
    fitted=call('envelope',{'stock_type':'cylinder','direction':cyl_info['axis']});check(5<=fitted['cylinder']['radius']<5.02,'cylinder stock uses CAD radial supports rather than the oversized box diagonal')
    # This fixture declares coordinates 20 x 30 x 40 as inches, so native mm extents must be 25.4 times larger.
    inch_model=call('step_open',{'path':str(inch)});check(all(abs(a-b)<1e-5 for a,b in zip(inch_model['bounds'],[0,0,0,508,762,1016])),'inch STEP source converts to correct millimetre geometry')
    assembly_model=call('step_open',{'path':str(assembly_file)});check(assembly_model['face_count']==12 and abs(assembly_model['bounds'][3]-120)<1e-5,'assembly instance transformations are preserved')
    check(call('step_open',{'path':str(unicode_step)})['face_count']==6,'STEP Unicode filename opens correctly')
    tilted_model=call('step_open',{'path':str(tilted)});tilted_normals=[]
    for f in tilted_model['faces']:
        pts=list(zip(*[iter(f['positions'])]*3));p=[sum(v[i] for v in pts)/len(pts) for i in range(3)];info=call('inspect',{'kind':'face','id':f['id'],'point':p});tilted_normals.append(info['normal'])
    tilted_normal=next(n for n in tilted_normals if abs(n[2]-math.cos(math.pi/6))<1e-6);check(abs(tilted_normal[0]-.5)<1e-6,'tilted CAD face selection returns its transformed world normal')
    tilted_stock=call('envelope',{'stock_type':'box','direction':tilted_normal,'uniform_offset':1});check(abs(tilted_stock['box_size'][2]-42)<1e-5,'stock aligns to the selected tilted part face rather than fixed world axes')
    check(call('pdf_open',{'path':str(pdf)})['pages']==1,'native PDF open');page=call('pdf_page',{'page':0});check('25 PCS' in page['text'] and len(page['image'])>1000,'PDF native text and page rendering')
    import base64
    (data/'native-preview.png').write_bytes(base64.b64decode(page['image']))
    call('pdf_open',{'path':str(scanned)});page=call('pdf_page',{'page':0});check(not page['text'].strip(),'scanned PDF has no synthetic text layer');ocr=call('pdf_ocr',{'page':0});check('25' in ocr['text'] and '20' in ocr['text'] and ocr['review'],'OCR scans a rendered page and labels review required')
    (data/'scan-preview.png').write_bytes(base64.b64decode(page['image']))
    print(json.dumps({'passed':len(passed),'checks':passed,'data':str(data),'pmi':model['pmi'],'ocr':ocr},ensure_ascii=False),flush=True)
finally:worker_process.kill();worker_process.wait(timeout=10)
