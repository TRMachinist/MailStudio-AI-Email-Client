"""Native assembly/object-set/CSYS/stock acceptance, no user mail or source writes."""
from pathlib import Path
import sys,os,subprocess,tempfile,json,math,hashlib
from OCP.TDocStd import TDocStd_Document
from OCP.TCollection import TCollection_ExtendedString
from OCP.TDataStd import TDataStd_Name
from OCP.XCAFDoc import XCAFDoc_DocumentTool,XCAFDoc_ColorType
from OCP.Quantity import Quantity_Color,Quantity_TypeOfColor
from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox
from OCP.gp import gp_Trsf,gp_Vec,gp_Ax1,gp_Pnt,gp_Dir
from OCP.TopLoc import TopLoc_Location
from OCP.STEPCAFControl import STEPCAFControl_Writer
from OCP.Interface import Interface_Static
from OCP.StepData import StepData_ConfParameters
from OCP.IFSelect import IFSelect_RetDone

root=Path(__file__).resolve().parents[1];data=Path(tempfile.mkdtemp(prefix='MailStudio-assembly-'))
Interface_Static.SetCVal_s('write.step.schema','AP242DIS');Interface_Static.SetCVal_s('write.step.unit','MM')
def save(doc,path):
    writer=STEPCAFControl_Writer();params=StepData_ConfParameters();params.InitFromStatic();assert writer.Transfer(doc,params);assert writer.Write(str(path))==IFSelect_RetDone
doc=TDocStd_Document(TCollection_ExtendedString('assembly'));tool=XCAFDoc_DocumentTool.ShapeTool_s(doc.Main());top=tool.NewShape();part=tool.AddShape(BRepPrimAPI_MakeBox(20,30,40).Shape(),False)
TDataStd_Name.Set_s(top,TCollection_ExtendedString('Test Montaj'));TDataStd_Name.Set_s(part,TCollection_ExtendedString('Ortak tanım',True))
first=tool.AddComponent(top,part,TopLoc_Location());tr=gp_Trsf();tr.SetTranslation(gp_Vec(100,0,0));second=tool.AddComponent(top,part,TopLoc_Location(tr));TDataStd_Name.Set_s(first,TCollection_ExtendedString('Ön Plaka',True));TDataStd_Name.Set_s(second,TCollection_ExtendedString('Arka Plaka'))
colors=XCAFDoc_DocumentTool.ColorTool_s(doc.Main());colors.SetColor(first,Quantity_Color(.8,.1,.1,Quantity_TypeOfColor.Quantity_TOC_RGB),XCAFDoc_ColorType.XCAFDoc_ColorSurf);colors.SetColor(second,Quantity_Color(.1,.2,.8,Quantity_TypeOfColor.Quantity_TOC_RGB),XCAFDoc_ColorType.XCAFDoc_ColorSurf);tool.UpdateAssemblies();assembly=data/'named-assembly.stp';save(doc,assembly)
nested_doc=TDocStd_Document(TCollection_ExtendedString('nested'));nt=XCAFDoc_DocumentTool.ShapeTool_s(nested_doc.Main());nr=nt.NewShape();sub=nt.NewShape();np=nt.AddShape(BRepPrimAPI_MakeBox(20,30,40).Shape(),False);nt.AddComponent(sub,np,TopLoc_Location());nt.AddComponent(sub,np,TopLoc_Location(tr));rotation=gp_Trsf();rotation.SetRotation(gp_Ax1(gp_Pnt(0,0,0),gp_Dir(0,0,1)),math.pi/2);rotation.SetTranslationPart(gp_Vec(10,50,0));nt.AddComponent(nr,sub,TopLoc_Location(rotation));translation=gp_Trsf();translation.SetTranslation(gp_Vec(300,0,0));nt.AddComponent(nr,sub,TopLoc_Location(translation));nt.UpdateAssemblies();nested=data/'nested-assembly.stp';save(nested_doc,nested)
target=sys.argv[1:] or [sys.executable,str(root/'src/MailStudio.Technical/worker.py')];env=os.environ.copy();env['PATH']=str(Path(env.get('SystemRoot',r'C:\Windows'))/'System32');env.pop('PYTHONHOME',None);env.pop('PYTHONPATH',None)
worker=subprocess.Popen(target,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,encoding='utf8',env=env,creationflags=subprocess.CREATE_NO_WINDOW);sequence=0;checks=[]
def call(operation,args):
    global sequence
    sequence+=1;worker.stdin.write(json.dumps({'id':sequence,'operation':operation,'args':args})+'\n');worker.stdin.flush();response=json.loads(worker.stdout.readline())
    if 'error' in response:raise RuntimeError(response['error'])
    return response['result']
def check(condition,message):
    assert condition,message;checks.append(message);print('PASS',message,flush=True)
try:
    model=call('step_open',{'path':str(assembly)});check(model['body_count']==2 and model['component_count']==2 and len(model['components'])==3,'named assembly hierarchy and independent occurrences are preserved')
    check({n['name'] for n in model['components']}=={'Test Montaj','Ön Plaka','Arka Plaka'},'STEP component names retain Turkish characters')
    check(model['bodies'][0]['color'][0]>.7 and model['bodies'][1]['color'][2]>.7,'occurrence-specific part colors survive import')
    check({f['body_id'] for f in model['faces']}=={1,2} and {e['body_id'] for e in model['edges']}=={1,2},'every face and edge belongs to an occurrence-specific body')
    check(abs(model['bodies'][1]['bounds'][0]-100)<1e-6,'assembly child translation stays in world coordinates')
    a={'kind':'body','id':1};b={'kind':'body','id':2};r=call('analyze',{'first':[a],'second':[b],'direction':[1,0,0],'x_direction':[0,1,0]})
    check(abs(r['minimum_distance_mm']-80)<1e-6 and abs(r['projected_gap_mm']-80)<1e-6,'body object sets provide true minimum and projected interval distance')
    check(abs(r['first_properties']['volume_mm3']-24000)<1e-5 and abs(r['first_properties']['area_mm2']-5200)<1e-5,'body volume and area are native CAD properties')
    check(all(abs(x-y)<1e-6 for x,y in zip(r['first_properties']['centroid'],[10,15,20])),'selected body centroid is reported in world coordinates')
    overlap=call('analyze',{'first':[a],'second':[b],'direction':[0,0,1],'x_direction':[1,0,0]});check(overlap['minimum_distance_mm']>79 and abs(overlap['projected_gap_mm'])<1e-6,'projected overlap is distinguished from nonzero 3D separation')
    root_node=next(n for n in model['components'] if n['is_assembly']);group=call('analyze',{'first':[{'kind':'component','id':root_node['id']},a],'second':[]});check(abs(group['first_properties']['volume_mm3']-48000)<1e-5,'component plus contained body is not double-counted in a selection group')
    all_box=call('envelope',{'stock_type':'box','orientation':'world'});single_box=call('envelope',{'stock_type':'box','body_ids':[2],'orientation':'world'});check(abs(all_box['box_size'][0]-120)<1e-5 and abs(single_box['box_size'][0]-20)<1e-5,'bounding box scope distinguishes assembly and selected component')
    check(all(abs(x-y)<1e-5 for x,y in zip(single_box['box_center'],[110,15,20])),'selected component stock is positioned on its occurrence')
    rotated=call('envelope',{'stock_type':'box','body_ids':[1],'direction':[0,0,1],'x_direction':[0,1,0],'origin':[11,22,33]});check(all(abs(x-y)<1e-5 for x,y in zip(rotated['box_size'],[30,20,40])),'complete X/Z/origin CSYS controls bounding dimensions')
    auto=call('envelope',{'stock_type':'box','body_ids':[1],'orientation':'auto','uniform_offset':1});check(all(abs(x-y)<1e-4 for x,y in zip(sorted(auto['box_size']),[22,32,42])),'native best-fit OBB orientation produces tight padded block')
    candidates=[next(v for v in model['vertices'] if v['body_id']==1 and math.dist(v['point'],p)<1e-6) for p in ([0,0,0],[20,0,0],[20,30,0])];angle=call('analyze',{'mode':'angle3','first':[{'kind':'vertex','id':v['id']} for v in candidates]});check(abs(angle['angle_degrees']-90)<1e-6,'three native CAD points measure a right angle with second point as vertex')
    whole_cyl=call('envelope',{'stock_type':'cylinder'});small_cyl=call('envelope',{'stock_type':'cylinder','body_ids':[1]});check(whole_cyl['cylinder']['radius']>60 and small_cyl['cylinder']['radius']<20,'radial stock cache is isolated by selected body set')
    source_hash=hashlib.sha256(assembly.read_bytes()).hexdigest();call('envelope',{'stock_type':'box','body_ids':[1],'uniform_offset':1});export=data/'stock.stp';call('export_stock',{'path':str(export)});check(export.stat().st_size>1000 and hashlib.sha256(assembly.read_bytes()).hexdigest()==source_hash,'native stock export leaves source assembly unchanged')
    exported=call('step_open',{'path':str(export)});mass=call('inspect',{'kind':'body','id':1});check(abs(mass['volume_mm3']-22*32*42)<1e-4,'exported stock reopens as a real CAD solid of the expected volume')
    call('envelope',{'stock_type':'box'});blocked=False
    try:call('export_stock',{'path':str(export)})
    except RuntimeError as ex:blocked='Kaynak' in str(ex)
    check(blocked,'stock export refuses to overwrite the currently loaded STEP source')
    call('step_open',{'path':str(assembly)});atomic=data/'atomic-stock.stp';call('envelope',{'stock_type':'cylinder'});call('export_stock',{'path':str(atomic),'stock_args':{'stock_type':'box','body_ids':[2],'uniform_offset':3}});call('step_open',{'path':str(atomic)});atomic_properties=call('inspect',{'kind':'body','id':1});check(abs(atomic_properties['volume_mm3']-26*36*46)<1e-4,'atomic export uses current stock arguments instead of a stale previous cylinder')
    nested_model=call('step_open',{'path':str(nested)});check(nested_model['body_count']==4 and len(nested_model['components'])==7,'nested assemblies preserve repeated subassembly occurrences')
    check(all(abs(x-y)<1e-5 for x,y in zip(nested_model['bounds'],[-20,0,0,420,170,40])),'nested parent rotation and translation compose correctly for all instances')
    print(json.dumps({'passed':len(checks),'checks':checks,'data':str(data)},ensure_ascii=False),flush=True)
finally:worker.kill();worker.wait(timeout=10)
