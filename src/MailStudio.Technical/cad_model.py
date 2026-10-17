"""Read native occurrences, keeping entity IDs tied to their assembly owners."""
import os,math
from OCP.STEPCAFControl import STEPCAFControl_Reader
from OCP.TDocStd import TDocStd_Document
from OCP.TCollection import TCollection_ExtendedString
from OCP.Interface import Interface_Static
from OCP.IFSelect import IFSelect_RetDone
from OCP.BRepMesh import BRepMesh_IncrementalMesh
from OCP.TopExp import TopExp_Explorer,TopExp
from OCP.TopAbs import TopAbs_FACE,TopAbs_REVERSED,TopAbs_EDGE,TopAbs_VERTEX
from OCP.TopoDS import TopoDS
from OCP.BRep import BRep_Tool
from OCP.TopLoc import TopLoc_Location
from OCP.BRepAdaptor import BRepAdaptor_Surface,BRepAdaptor_Curve
from OCP.TopTools import TopTools_IndexedMapOfShape
from OCP.GCPnts import GCPnts_QuasiUniformDeflection
from cad_document import read_assembly,compound
from cad_analysis import bbox,xyz

class CadModel:
    def __init__(self,path):
        reader=STEPCAFControl_Reader();reader.SetColorMode(True);reader.SetNameMode(True);reader.SetGDTMode(True);Interface_Static.SetCVal_s('xstep.cascade.unit','MM')
        if reader.ReadFile(os.path.abspath(path))!=IFSelect_RetDone:raise ValueError('STEP dosyası okunamadı.')
        self.doc=TDocStd_Document(TCollection_ExtendedString('MailStudio'))
        if not reader.Transfer(self.doc):raise ValueError('STEP geometrisi aktarılamadı.')
        self.nodes,self.bodies=read_assembly(self.doc)
        if not self.bodies:raise ValueError('STEP içinde aktarılabilir geometri yok.')
        self.shape=compound([b['shape'] for b in self.bodies]);box=bbox(self.shape);deflection=max(.005,math.dist(box[:3],box[3:])/1800)
        BRepMesh_IncrementalMesh(self.shape,deflection,False,.35,True).Perform()
        self.faces=[];self.edges=[];self.vertices=[];self.face_owners=[];self.edge_owners=[];self.vertex_owners=[];meshes=[];edge_data=[];vertex_data=[];triangles=0;edge_points=0;warnings=[]
        for body in self.bodies:
            body['bounds']=bbox(body['shape']);body['face_ids']=[];body['edge_ids']=[];body['vertex_ids']=[]
            owner={'body_id':body['id'],'component_id':body['component_id']}
            explorer=TopExp_Explorer(body['shape'],TopAbs_FACE)
            while explorer.More():
                face=TopoDS.Face_s(explorer.Current());self.faces.append(face);self.face_owners.append(body['id']);identity=len(self.faces);body['face_ids'].append(identity);loc=TopLoc_Location();tri=BRep_Tool.Triangulation_s(face,loc)
                if tri is not None:
                    positions=[];indices=[]
                    for i in range(1,tri.NbNodes()+1):positions.extend(round(x,7) for x in xyz(tri.Node(i).Transformed(loc.Transformation())))
                    for i in range(1,tri.NbTriangles()+1):
                        a,b,c=tri.Triangle(i).Get()
                        if face.Orientation()==TopAbs_REVERSED:b,c=c,b
                        indices.extend((a-1,b-1,c-1))
                    triangles+=len(indices)//3
                    if triangles>1500000:raise ValueError('Görüntüleme sınırı aşıldı (1,5 milyon üçgen).')
                    surface=BRepAdaptor_Surface(face);kind=str(surface.GetType()).split('_')[-1];meta={'type':kind}
                    if kind in ('Cylinder','Sphere'):meta['radius']=getattr(surface,kind)().Radius();meta['diameter']=2*meta['radius']
                    meshes.append({'id':identity,'positions':positions,'indices':indices,**meta,**owner,'color':body['color']})
                explorer.Next()
            edge_map=TopTools_IndexedMapOfShape();TopExp.MapShapes_s(body['shape'],TopAbs_EDGE,edge_map)
            for i in range(1,edge_map.Extent()+1):
                edge=TopoDS.Edge_s(edge_map.FindKey(i));self.edges.append(edge);self.edge_owners.append(body['id']);identity=len(self.edges);body['edge_ids'].append(identity)
                if BRep_Tool.Degenerated_s(edge):continue
                try:
                    curve=BRepAdaptor_Curve(edge);samples=GCPnts_QuasiUniformDeflection(curve,deflection)
                    if not samples.IsDone():raise ValueError('CAD kenarı örneklenemedi.')
                    points=[xyz(samples.Value(j)) for j in range(1,samples.NbPoints()+1)];edge_points+=len(points)
                    if edge_points>1000000:raise ValueError('CAD kenar görüntüleme sınırı aşıldı.')
                    edge_data.append({'id':identity,'points':points,'type':str(curve.GetType()).split('_')[-1],**owner})
                except RuntimeError as ex:warnings.append({'id':identity,'warning':str(ex)})
            vertex_map=TopTools_IndexedMapOfShape();TopExp.MapShapes_s(body['shape'],TopAbs_VERTEX,vertex_map)
            for i in range(1,vertex_map.Extent()+1):
                vertex=TopoDS.Vertex_s(vertex_map.FindKey(i));self.vertices.append(vertex);self.vertex_owners.append(body['id']);identity=len(self.vertices);body['vertex_ids'].append(identity);vertex_data.append({'id':identity,'point':xyz(BRep_Tool.Pnt_s(vertex)),**owner})
        records=[{k:v for k,v in body.items() if k!='shape'} for body in self.bodies]
        self.data={'unit':'mm','bounds':box,'faces':meshes,'edges':edge_data,'vertices':vertex_data,'edge_warnings':warnings,'face_count':len(self.faces),'triangles':triangles,'components':self.nodes,'bodies':records,'body_count':len(records),'component_count':sum(not n['is_assembly'] for n in self.nodes)}
