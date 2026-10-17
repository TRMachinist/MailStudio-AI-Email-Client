"""XDE occurrence hierarchy. Definitions may repeat; each occurrence stays distinct."""
from OCP.XCAFDoc import XCAFDoc_DocumentTool,XCAFDoc_ShapeTool,XCAFDoc_ColorTool,XCAFDoc_ColorType
from OCP.TDF import TDF_Label,TDF_LabelSequence,TDF_AttributeIterator,TDF_Tool
from OCP.TCollection import TCollection_AsciiString
from OCP.TDataStd import TDataStd_Name
from OCP.TopLoc import TopLoc_Location
from OCP.TopTools import TopTools_IndexedMapOfShape
from OCP.TopExp import TopExp
from OCP.TopAbs import TopAbs_SOLID,TopAbs_SHELL
from OCP.Quantity import Quantity_Color
from OCP.BRep import BRep_Builder
from OCP.TopoDS import TopoDS_Compound

def compound(shapes):
    builder=BRep_Builder();result=TopoDS_Compound();builder.MakeCompound(result)
    for shape in shapes:builder.Add(result,shape)
    return result

def name_of(label):
    attributes=TDF_AttributeIterator(label)
    while attributes.More():
        a=attributes.Value()
        if isinstance(a,TDataStd_Name):return a.Get().ToExtString()
        attributes.Next()
    return ''

def color_of(tool,labels,shape):
    for kind in (XCAFDoc_ColorType.XCAFDoc_ColorSurf,XCAFDoc_ColorType.XCAFDoc_ColorGen):
        color=Quantity_Color()
        if tool.GetInstanceColor(shape,kind,color):return [color.Red(),color.Green(),color.Blue()]
        for label in labels:
            if XCAFDoc_ColorTool.GetColor_s(label,kind,color):return [color.Red(),color.Green(),color.Blue()]
    return None

def read_assembly(doc):
    tool=XCAFDoc_DocumentTool.ShapeTool_s(doc.Main());colors=XCAFDoc_DocumentTool.ColorTool_s(doc.Main())
    roots=TDF_LabelSequence();tool.GetFreeShapes(roots);nodes=[];bodies=[]
    def visit(occurrence,parent,location,ancestors):
        definition=occurrence;world=location
        if XCAFDoc_ShapeTool.IsReference_s(occurrence):
            definition=TDF_Label()
            if not XCAFDoc_ShapeTool.GetReferredShape_s(occurrence,definition):raise ValueError('Montaj bileşeninin referansı çözülemedi.')
            world=location.Multiplied(XCAFDoc_ShapeTool.GetLocation_s(occurrence))
        label_entry=TCollection_AsciiString();TDF_Tool.Entry_s(definition,label_entry);key=label_entry.ToCString()
        if key in ancestors:raise ValueError('Döngü içeren montaj yapısı aktarılamadı.')
        native=XCAFDoc_ShapeTool.GetShape_s(definition)
        if native.IsNull():raise ValueError('Montaj bileşeni boş geometri içeriyor.')
        own_name=name_of(occurrence);definition_name=name_of(definition)
        if own_name.startswith('=>') or own_name.startswith('⇒'):own_name=''
        node={'id':len(nodes)+1,'parent_id':parent,'name':own_name or definition_name or f'Bileşen {len(nodes)+1}','children':[],'body_ids':[],'is_assembly':XCAFDoc_ShapeTool.IsAssembly_s(definition)}
        nodes.append(node)
        if parent:nodes[parent-1]['children'].append(node['id'])
        if node['is_assembly']:
            children=TDF_LabelSequence();XCAFDoc_ShapeTool.GetComponents_s(definition,children,False)
            assembly_location=world.Multiplied(native.Location())
            for i in range(1,children.Length()+1):visit(children.Value(i),node['id'],assembly_location,ancestors|{key})
        else:
            instance=native.Moved(world);parts=TopTools_IndexedMapOfShape();TopExp.MapShapes_s(instance,TopAbs_SOLID,parts);body_kind='solid'
            if parts.Extent()==0:TopExp.MapShapes_s(instance,TopAbs_SHELL,parts);body_kind='sheet'
            shapes=[parts.FindKey(i) for i in range(1,parts.Extent()+1)] or [instance]
            color=color_of(colors,[occurrence,definition],instance)
            for i,body in enumerate(shapes,1):
                identity=len(bodies)+1;node['body_ids'].append(identity)
                bodies.append({'id':identity,'component_id':node['id'],'name':node['name']+(f' · Gövde {i}' if len(shapes)>1 else ''),'kind':body_kind,'color':color,'shape':body})
        return node
    for i in range(1,roots.Length()+1):visit(roots.Value(i),0,TopLoc_Location(),set())
    # Parent assembly scope consists of occurrence-specific descendant bodies.
    for node in reversed(nodes):
        for child in node['children']:node['body_ids'].extend(nodes[child-1]['body_ids'])
    return nodes,bodies
