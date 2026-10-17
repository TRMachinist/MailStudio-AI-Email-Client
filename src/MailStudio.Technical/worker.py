"""Local stdin/stdout CAD/PDF worker. CAD measurements use retained OCCT B-Rep, never display triangles."""
import sys,os,json,math,base64,io,traceback
sys.stdin.reconfigure(encoding='utf-8')
protocol=os.fdopen(os.dup(sys.stdout.fileno()),'w',encoding='utf-8',buffering=1)
os.dup2(sys.stderr.fileno(),sys.stdout.fileno())  # native OCCT progress must not corrupt JSON protocol
shape=None;faces=[];edges=[];vertices=[];radial_cache={};xde=None;cad=None;source_path=None;stock_shape=None;pdf=None;ocr_engine=None

def point(p):return [p.X(),p.Y(),p.Z()]
def vector(v):
    if len(v)!=3 or not all(math.isfinite(float(x)) for x in v):raise ValueError('Üç sonlu vektör bileşeni gerekli.')
    n=math.sqrt(sum(float(x)**2 for x in v))
    if n<1e-12:raise ValueError('Yön vektörü sıfır olamaz.')
    return [float(x)/n for x in v]
def bounds(s):
    from OCP.Bnd import Bnd_Box
    from OCP.BRepBndLib import BRepBndLib
    b=Bnd_Box();BRepBndLib.AddOptimal_s(s,b,False,False)
    if b.IsVoid():raise ValueError('Model sınırı hesaplanamadı.')
    return list(b.Get())
def entry(label):
    from OCP.TCollection import TCollection_AsciiString
    from OCP.TDF import TDF_Tool
    a=TCollection_AsciiString();TDF_Tool.Entry_s(label,a);return a.ToCString()
def pmi_presentation(presentation):
    """Retain the file's own graphical annotation geometry, including stroked glyphs."""
    from OCP.TopExp import TopExp_Explorer
    from OCP.TopAbs import TopAbs_EDGE,TopAbs_FACE,TopAbs_REVERSED
    from OCP.TopoDS import TopoDS
    from OCP.BRepAdaptor import BRepAdaptor_Curve
    from OCP.GCPnts import GCPnts_QuasiUniformDeflection
    from OCP.BRep import BRep_Tool
    from OCP.BRepMesh import BRepMesh_IncrementalMesh
    from OCP.TopLoc import TopLoc_Location
    if presentation.IsNull():return None
    result={'lines':[],'faces':[],'warnings':[]};size=bounds(presentation);tolerance=max(.001,math.dist(size[:3],size[3:])/4000)
    edges=TopExp_Explorer(presentation,TopAbs_EDGE);count=0
    while edges.More():
        try:
            curve=BRepAdaptor_Curve(TopoDS.Edge_s(edges.Current()));samples=GCPnts_QuasiUniformDeflection(curve,tolerance)
            if samples.IsDone() and samples.NbPoints()>1:
                count+=samples.NbPoints()
                if count>100000:raise ValueError('PMI sunumu nokta sınırını aştı.')
                result['lines'].append([point(samples.Value(i)) for i in range(1,samples.NbPoints()+1)])
        except RuntimeError as ex:result['warnings'].append(str(ex))
        edges.Next()
    BRepMesh_IncrementalMesh(presentation,tolerance,False,.35,True).Perform();explorer=TopExp_Explorer(presentation,TopAbs_FACE)
    while explorer.More():
        face=TopoDS.Face_s(explorer.Current());loc=TopLoc_Location();tri=BRep_Tool.Triangulation_s(face,loc)
        if tri is not None:
            positions=[];indices=[]
            for i in range(1,tri.NbNodes()+1):positions.extend(point(tri.Node(i).Transformed(loc.Transformation())))
            for i in range(1,tri.NbTriangles()+1):
                a,b,c=tri.Triangle(i).Get()
                if face.Orientation()==TopAbs_REVERSED:b,c=c,b
                indices.extend((a-1,b-1,c-1))
            count+=len(indices)
            if count>300000:raise ValueError('PMI sunumu üçgen sınırını aştı.')
            result['faces'].append({'positions':positions,'indices':indices})
        explorer.Next()
    return result

def pmi_items(doc):
    from OCP.XCAFDoc import XCAFDoc_DocumentTool,XCAFDoc_Dimension,XCAFDoc_GeomTolerance,XCAFDoc_Datum
    from OCP.TDF import TDF_LabelSequence,TDF_AttributeIterator
    tool=XCAFDoc_DocumentTool.DimTolTool_s(doc.Main());out=[]
    for kind,method,cls in [('Ölçü',tool.GetDimensionLabels,XCAFDoc_Dimension),('Geometrik tolerans',tool.GetGeomToleranceLabels,XCAFDoc_GeomTolerance),('Datum',tool.GetDatumLabels,XCAFDoc_Datum)]:
        labels=TDF_LabelSequence();method(labels)
        for i in range(1,labels.Length()+1):
            label=labels.Value(i);item={'kind':kind,'label':entry(label)};iterator=TDF_AttributeIterator(label);a=None
            while iterator.More():
                candidate=iterator.Value()
                if isinstance(candidate,cls):a=candidate;break
                iterator.Next()
            if a is not None:
                obj=a.GetObject()
                for name in ('GetType','GetValue','GetName','GetSemanticName'):
                    try:
                        value=getattr(obj,name)();item[name[3:]]=value.ToCString() if hasattr(value,'ToCString') else value.ToExtString() if hasattr(value,'ToExtString') else value if isinstance(value,(int,float,str)) else str(value)
                    except (AttributeError,TypeError,RuntimeError):pass
                if hasattr(obj,'IsDimWithRange') and obj.IsDimWithRange():item['LowerBound']=obj.GetLowerBound();item['UpperBound']=obj.GetUpperBound()
                if hasattr(obj,'IsDimWithPlusMinusTolerance') and obj.IsDimWithPlusMinusTolerance():item['LowerTolerance']=obj.GetLowerTolValue();item['UpperTolerance']=obj.GetUpperTolValue()
                try:
                    presentation=pmi_presentation(obj.GetPresentation())
                    if presentation is not None:item['presentation']=presentation
                except Exception as ex:item['presentation_warning']=str(ex)
                for flag,getter,key in [('HasPoint','GetPoint','point'),('HasPoint2','GetPoint2','point2'),('HasTextPoint','GetPointTextAttach','text_point'),('HasPointText','GetPointTextAttach','text_point')]:
                    if hasattr(obj,flag) and getattr(obj,flag)():item[key]=point(getattr(obj,getter)())
            try:
                first=TDF_LabelSequence();second=TDF_LabelSequence();tool.GetRefShapeLabel_s(label,first,second)
                item['geometry_labels']=[entry(seq.Value(j)) for seq in (first,second) for j in range(1,seq.Length()+1) if not seq.Value(j).IsNull()]
            except (TypeError,RuntimeError):item['geometry_labels']=[]
            out.append(item)
    return out

def step_open(path):
    global shape,faces,edges,vertices,radial_cache,xde,cad,source_path,stock_shape
    from cad_model import CadModel
    current=CadModel(path)
    try:pmi=pmi_items(current.doc)
    except Exception as ex:pmi=[{'kind':'PMI aktarım uyarısı','detail':str(ex)}]
    shape=current.shape;faces=current.faces;edges=current.edges;vertices=current.vertices;xde=current.doc;cad=current;radial_cache={};source_path=os.path.abspath(path);stock_shape=None
    graphical=sum(1 for item in pmi if item.get('presentation') and (item['presentation']['lines'] or item['presentation']['faces']))
    try:
        from cad_analysis import properties
        part_volume=properties(shape)['volume_mm3']
    except Exception:part_volume=None
    return {**current.data,'part_volume_mm3':part_volume,'pmi':pmi,'graphical_pmi_count':graphical,'pmi_notice':f'{graphical} kayıt grafik sunum içeriyor. Grafik sunumu olmayan semantik kayıtlar listede kalır.'}

def face_of(index):
    if shape is None:raise ValueError('Önce STEP açın.')
    if not 1<=int(index)<=len(faces):raise ValueError('Geçersiz yüz seçimi.')
    return faces[int(index)-1]

def entity_of(spec):
    if shape is None:raise ValueError('Önce STEP açın.')
    if spec['kind']=='center':return entity_of(dict(spec,kind=spec['source_kind']))
    if spec['kind']=='body':
        index=int(spec['id'])
        if not 1<=index<=len(cad.bodies):raise ValueError('Geçersiz gövde.')
        return cad.bodies[index-1]['shape']
    if spec['kind']=='component':
        index=int(spec['id'])
        if not 1<=index<=len(cad.nodes):raise ValueError('Geçersiz montaj bileşeni.')
        return selected_bodies(cad.nodes[index-1]['body_ids'])[0]
    kind=spec['kind'];items=faces if kind in ('face','facepoint') else edges if kind in ('edge','edgepoint') else vertices if kind=='vertex' else None
    index=int(spec['id'])
    if items is None or not 1<=index<=len(items):raise ValueError('Geçersiz CAD seçimi.')
    return items[index-1]

def projected_point(spec):
    from OCP.BRepExtrema import BRepExtrema_DistShapeShape
    from OCP.BRepBuilderAPI import BRepBuilderAPI_MakeVertex
    from OCP.BRep import BRep_Tool
    from OCP.gp import gp_Pnt
    entity=entity_of(spec)
    if spec['kind']=='center':
        info=inspect_entity(dict(spec,kind=spec['source_kind']))
        if 'center' not in info:raise ValueError('Bu nesnede analitik daire/küre merkezi yok.')
        return info['center']
    if spec['kind']=='vertex':return point(BRep_Tool.Pnt_s(entity))
    p=spec.get('point')
    if p is None and spec['kind'] in ('body','component'):
        b=bounds(entity);p=[(b[i]+b[i+3])/2 for i in range(3)]
    if p is None or len(p)!=3 or not all(math.isfinite(float(x)) for x in p):raise ValueError('Seçim noktası geçersiz.')
    projection=BRepExtrema_DistShapeShape(BRepBuilderAPI_MakeVertex(gp_Pnt(*map(float,p))).Vertex(),entity);projection.Perform()
    if not projection.IsDone() or projection.NbSolution()<1:raise ValueError('Seçim CAD geometrisine izdüşürülemedi.')
    return point(projection.PointOnShape2(1))

def inspect_entity(spec):
    from OCP.BRepAdaptor import BRepAdaptor_Surface,BRepAdaptor_Curve
    from OCP.BRep import BRep_Tool
    from OCP.BRepGProp import BRepGProp
    from OCP.GProp import GProp_GProps
    from OCP.GeomAPI import GeomAPI_ProjectPointOnSurf
    from OCP.BRepLProp import BRepLProp_SLProps
    from OCP.TopAbs import TopAbs_REVERSED
    from OCP.gp import gp_Pnt
    if spec['kind']=='center':return {'kind':'center','id':spec['id'],'point':projected_point(spec),'unit':'mm'}
    entity=entity_of(spec);p=projected_point(spec);kind=spec['kind'];result={'kind':kind,'id':spec['id'],'point':p,'unit':'mm'}
    if kind in ('body','component'):
        from cad_analysis import properties
        result.update(properties(entity));result['name']=(cad.bodies if kind=='body' else cad.nodes)[int(spec['id'])-1]['name']
    if kind in ('face','facepoint'):
        s=BRepAdaptor_Surface(entity);name=str(s.GetType()).split('_')[-1];result['type']=name
        props=GProp_GProps();BRepGProp.SurfaceProperties_s(entity,props,False,False);result['area_mm2']=props.Mass()
        try:
            surface=BRep_Tool.Surface_s(entity);projection=GeomAPI_ProjectPointOnSurf(gp_Pnt(*p),surface);u,v=projection.LowerDistanceParameters();normal=BRepLProp_SLProps(s,u,v,1,1e-9)
            if normal.IsNormalDefined():
                n=point(normal.Normal());result['normal']=[-x for x in n] if entity.Orientation()==TopAbs_REVERSED else n
        except RuntimeError:pass
        if name=='Plane':result['plane_origin']=point(s.Plane().Location())
        if name in ('Cylinder','Cone','Torus','Sphere'):

            obj=getattr(s,name)()
            if name!='Sphere':result['axis']=point(obj.Axis().Direction());result['axis_point']=point(obj.Axis().Location())
            if name=='Sphere':result['center']=point(obj.Location())
            if name=='Cylinder':
                location=point(obj.Location());axis=point(obj.Axis().Direction());t=sum((p[i]-location[i])*axis[i] for i in range(3));result['center']=[location[i]+t*axis[i] for i in range(3)]
            if name in ('Cylinder','Sphere'):result['radius_mm']=obj.Radius();result['diameter_mm']=2*obj.Radius()
    elif kind in ('edge','edgepoint'):
        c=BRepAdaptor_Curve(entity);name=str(c.GetType()).split('_')[-1];result['type']=name;props=GProp_GProps();BRepGProp.LinearProperties_s(entity,props);result['length_mm']=props.Mass()
        result['start']=point(c.Value(c.FirstParameter()));result['end']=point(c.Value(c.LastParameter()))
        if name=='Line':result['direction']=point(c.Line().Direction())
        if name=='Circle':result.update({'axis':point(c.Circle().Axis().Direction()),'radius_mm':c.Circle().Radius(),'diameter_mm':2*c.Circle().Radius(),'center':point(c.Circle().Location()),'axis_point':point(c.Circle().Location())})
    return result

def measure(a):
    from OCP.BRepExtrema import BRepExtrema_DistShapeShape
    from OCP.BRepBuilderAPI import BRepBuilderAPI_MakeVertex
    from OCP.gp import gp_Pnt
    selections=a.get('selections')
    if selections is None:selections=[{'kind':'face','id':a['first'],'point':a.get('points',[[0,0,0],[0,0,0]])[0]},{'kind':'face','id':a['second'],'point':a.get('points',[[0,0,0],[0,0,0]])[1]}]
    if len(selections)!=2:raise ValueError('İki CAD seçimi gerekli.')
    retained=[]
    for spec in selections:
        retained.append(BRepBuilderAPI_MakeVertex(gp_Pnt(*projected_point(spec))).Vertex() if spec['kind'] in ('facepoint','edgepoint') else entity_of(spec))
    first,second=retained;dist=BRepExtrema_DistShapeShape(first,second);dist.Perform()
    if not dist.IsDone() or dist.NbSolution()<1:raise ValueError('Yüz mesafesi hesaplanamadı.')
    result={'minimum_distance_mm':dist.Value(),'closest_first':point(dist.PointOnShape1(1)),'closest_second':point(dist.PointOnShape2(1))}
    if 'points' in a or 'selections' in a:
        projected=[projected_point(spec) for spec in selections]
        vector_points=[result['closest_first'],result['closest_second']] if a.get('distance_mode')=='minimum' else projected
        delta=[y-x for x,y in zip(*vector_points)];direction=vector(a.get('direction',[0,0,1]))
        signed=sum(x*y for x,y in zip(delta,direction));result.update({'projected_points':projected,'vector_points':vector_points,'point_distance_mm':math.dist(*projected),'signed_vector_distance_mm':signed,'delta_mm':delta,'direction':direction,'vector_endpoint':[vector_points[0][i]+signed*direction[i] for i in range(3)]})
        info=[inspect_entity(spec) for spec in selections];result['entities']=info;directions=[item.get('normal',item.get('direction',item.get('axis'))) for item in info]
        if all(directions):
            angle=math.degrees(math.acos(max(-1,min(1,sum(x*y for x,y in zip(*directions))))));result['angle_degrees']=angle;result['acute_angle_degrees']=min(angle,180-angle)
    return result
def selected_bodies(ids=None):
    from cad_document import compound
    if cad is None:raise ValueError('Önce STEP açın.')
    ids=list(range(1,len(cad.bodies)+1)) if ids is None else sorted(set(int(i) for i in ids))
    if not ids or any(i<1 or i>len(cad.bodies) for i in ids):raise ValueError('En az bir geçerli gövde seçin.')
    return compound([cad.bodies[i-1]['shape'] for i in ids]),ids

def envelope(a):
    global stock_shape
    from cad_analysis import build_stock
    target,ids=selected_bodies(a.get('body_ids'));result,native=build_stock(target,a,radial_cache,ids);stock_shape=native
    return result

def group_shape(specs):
    from cad_document import compound
    from OCP.BRepBuilderAPI import BRepBuilderAPI_MakeVertex
    from OCP.gp import gp_Pnt
    if not specs:raise ValueError('Nesne grubuna geometri seçin.')
    bodies=set()
    for spec in specs:
        if spec['kind']=='body':bodies.add(int(spec['id']))
        elif spec['kind']=='component':bodies.update(cad.nodes[int(spec['id'])-1]['body_ids'])
    shapes=[cad.bodies[i-1]['shape'] for i in sorted(bodies)];seen=set()
    for spec in specs:
        if spec['kind'] in ('body','component'):continue
        key=(spec['kind'],spec['id'],tuple(spec.get('point',[])) if spec['kind'].endswith('point') else ())
        if key in seen:continue
        seen.add(key)
        if spec['kind']=='center':
            shapes.append(BRepBuilderAPI_MakeVertex(gp_Pnt(*projected_point(spec))).Vertex());continue
        owner=(cad.face_owners if spec['kind'] in ('face','facepoint') else cad.edge_owners if spec['kind'] in ('edge','edgepoint') else cad.vertex_owners)[int(spec['id'])-1]
        if owner in bodies:continue
        shapes.append(BRepBuilderAPI_MakeVertex(gp_Pnt(*projected_point(spec))).Vertex() if spec['kind'].endswith('point') else entity_of(spec))
    return compound(shapes)

def analyze(a):
    from cad_analysis import analyze_sets,angle_three
    first=a.get('first',[]);second=a.get('second',[])
    if a.get('mode')=='angle3':return angle_three([projected_point(spec) for spec in first])
    return analyze_sets(group_shape(first),group_shape(second) if second else None,a,inspect_entity(first[0]) if len(first)==1 else None,inspect_entity(second[0]) if len(second)==1 else None)

def export_stock(a):
    from OCP.STEPControl import STEPControl_Writer,STEPControl_AsIs
    from OCP.IFSelect import IFSelect_RetDone
    import tempfile
    stock_result=envelope(a['stock_args']) if 'stock_args' in a else None
    if stock_shape is None:raise ValueError('Önce stok oluşturun.')
    destination=os.path.abspath(a['path'])
    if os.path.normcase(destination)==os.path.normcase(source_path):raise ValueError('Kaynak STEP dosyasının üzerine stok yazılamaz.')
    fd,temporary=tempfile.mkstemp(prefix='MailStudio-stock-',suffix='.stp',dir=os.path.dirname(destination));os.close(fd)
    try:
        writer=STEPControl_Writer();writer.Transfer(stock_shape,STEPControl_AsIs)
        if writer.Write(temporary)!=IFSelect_RetDone:raise ValueError('Stok STEP dosyasına yazılamadı.')
        os.replace(temporary,destination)
    finally:
        if os.path.exists(temporary):os.remove(temporary)
    return {'path':destination,'bytes':os.path.getsize(destination),'stock':stock_result}

def pdf_open(path):
    global pdf
    import pypdfium2 as pdfium
    if pdf is not None:pdf.close()
    pdf=pdfium.PdfDocument(os.path.abspath(path))
    return {'pages':len(pdf)}
def pdf_page(a):
    if pdf is None:raise ValueError('Önce PDF açın.')
    index=int(a.get('page',0))
    if not 0<=index<len(pdf):raise ValueError('Sayfa numarası geçersiz.')
    page=pdf[index];textpage=page.get_textpage();text=textpage.get_text_range();textpage.close()
    scale=min(2.0,1900/max(page.get_size()));bitmap=page.render(scale=scale);pil=bitmap.to_pil();buffer=io.BytesIO();pil.save(buffer,format='PNG');image=base64.b64encode(buffer.getvalue()).decode('ascii');pil.close();bitmap.close();page.close()
    return {'page':index,'pages':len(pdf),'text':text,'image':image,'method':'PDF metin katmanı','review':False}
def pdf_ocr(a):
    global ocr_engine
    import numpy as np
    from rapidocr import RapidOCR
    if pdf is None:raise ValueError('Önce PDF açın.')
    if ocr_engine is None:ocr_engine=RapidOCR(params={'Global.log_level':'error','EngineConfig.onnxruntime.intra_op_num_threads':2,'EngineConfig.onnxruntime.inter_op_num_threads':1})
    page=pdf[int(a.get('page',0))];bitmap=page.render(scale=min(3.0,2600/max(page.get_size())));pil=bitmap.to_pil();result=ocr_engine(np.asarray(pil));pil.close();bitmap.close();page.close()
    texts=list(result.txts) if result.txts is not None else [];scores=[float(x) for x in result.scores] if result.scores is not None else []
    return {'text':'\n'.join(texts),'method':'OCR','scores':scores,'review':True,'notice':'OCR metni tahmindir. Ölçü, tolerans ve sayıları kaynak sayfada doğrulayın.'}
def dispatch(operation,args):
    if operation=='ping':return {'ready':True}
    if operation=='step_open':return step_open(args['path'])
    if operation=='measure':return measure(args)
    if operation=='inspect':return inspect_entity(args)
    if operation=='analyze':return analyze(args)
    if operation=='measure_session':
        from cad_measure_session import evaluate
        return evaluate(args,entity_of,projected_point,inspect_entity,group_shape)
    if operation=='export_stock':return export_stock(args)
    if operation=='envelope':return envelope(args)
    if operation=='pdf_open':return pdf_open(args['path'])
    if operation=='pdf_page':return pdf_page(args)
    if operation=='pdf_ocr':return pdf_ocr(args)
    if operation.startswith('cad2d_'):
        import cad2d
        result=cad2d.dispatch(operation,args)
        if result is not None:return result
    if operation.startswith('pdfs_'):
        import pdf_studio
        result=pdf_studio.dispatch(operation,args)
        if result is not None:return result
    raise ValueError('Bilinmeyen teknik işlem.')
for line in sys.stdin:
    request=None
    try:
        request=json.loads(line);answer={'id':request['id'],'result':dispatch(request['operation'],request.get('args',{}))}
    except Exception as ex:
        traceback.print_exc(file=sys.stderr);answer={'id':request.get('id') if request else None,'error':str(ex)}
    protocol.write(json.dumps(answer,ensure_ascii=False,allow_nan=False,separators=(',',':'))+'\n');protocol.flush()
