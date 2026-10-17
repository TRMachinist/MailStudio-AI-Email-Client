"""Ordered CAD measurement sessions. Geometry is native OCCT; no render-mesh measurements."""
import math
from cad_analysis import frame,dot,xyz,properties,norm
from OCP.gp import gp_Pnt,gp_Lin,gp_Dir
from OCP.BRepExtrema import BRepExtrema_DistShapeShape
from OCP.BRepBuilderAPI import BRepBuilderAPI_MakeVertex

def evaluate(args,entity,project,inspect,group):
    items=args.get('items',[])
    if len(items)>100:raise ValueError('Bir ölçümde en fazla 100 nesne kullanın.')
    vectors=[norm(i['direction']) for i in items if i.get('role')=='vector']
    if len(vectors)>1:raise ValueError('Bir ölçümde tek yön vektörü kullanın.')
    objects=[i for i in items if i.get('role')!='vector' and i.get('members')]
    basis=dict(args);basis['direction']=vectors[0] if vectors else args.get('direction',[0,0,1])
    # A projection reference does not rotate the chosen reporting CSYS.
    _,_,csys=frame({'direction':args.get('csys_z',[0,0,1]),'x_direction':args.get('csys_x',[1,0,0]),'origin':args.get('origin',[0,0,0])})
    direction=norm(basis['direction']);results=[];infos=[];shapes=[]
    def row(key,label,value,unit='',category='properties',**extra):return dict(key=key,label=label,value=value,unit=unit,category=category,**extra)
    for index,item in enumerate(objects):
        specs=item['members'];info=inspect(specs[0]) if len(specs)==1 else {'point':None,'kind':'set'};native=group(specs);infos.append(info);shapes.append(native)
        values=[];anchor=info.get('point');props=None
        if len(specs)>1 or info.get('kind') in ('body','component'):
            props=properties(native);anchor=props['centroid']
            values.extend([row('volume','Hacim',props['volume_mm3'],'mm³','volume'),row('area','Yüzey alanı',props['area_mm2'],'mm²','area')])
            for k,v in zip('XYZ',props['centroid']):values.append(row('centroid_'+k,'Ağırlık merkezi '+k,v,'mm','coordinates'))
        else:
            for key,label,unit,category in [('length_mm','Uzunluk','mm','length'),('radius_mm','Yarıçap','mm','radial'),('diameter_mm','Çap','mm','radial'),('area_mm2','Alan','mm²','area')]:
                if key in info and info.get('kind') not in ('vertex','facepoint','edgepoint','center'):values.append(row(key,label,info[key],unit,category))
            p=info['point'];local=[dot([p[j]-csys['origin'][j] for j in range(3)],csys[k]) for k in ('x','y','z')]
            for k,v in zip('XYZ',local):values.append(row('coordinate_'+k,'Koordinat '+k,v,'mm','coordinates'))
        if anchor is None:anchor=[0,0,0]
        graphic=None
        if 'radius_mm' in info and 'center' in info:
            center=info['center'];axis=info.get('axis',[0,0,1]);p=info['point'];radial=[p[j]-center[j] for j in range(3)];along=dot(radial,axis);radial=[radial[j]-along*axis[j] for j in range(3)]
            if sum(v*v for v in radial)<1e-12:
                candidate=[1,0,0] if abs(axis[0])<.9 else [0,1,0];radial=[candidate[j]-dot(candidate,axis)*axis[j] for j in range(3)]
            radial=norm(radial);a=[center[j]-radial[j]*info['radius_mm'] for j in range(3)];b=[center[j]+radial[j]*info['radius_mm'] for j in range(3)];graphic={'a':a,'b':b,'category':'radial','key':'diameter_mm','kind':'diameter'};anchor=center
        elif 'length_mm' in info and 'start' in info:graphic={'a':info['start'],'b':info['end'],'category':'length','key':'length_mm','kind':'length'}
        results.append({'title':item.get('name',f'Nesne {index+1}'),'indices':[index],'anchor':anchor,'rows':values,'graphic':graphic})
    method=args.get('method','free');count=len(objects)
    if method not in ('free','individual','pairs','chain','reference'):raise ValueError('Ölçüm yöntemi geçersiz.')
    if method=='individual':pairs=[]
    elif method=='pairs':pairs=[(i,i+1) for i in range(0,count-1,2)]
    elif method=='chain':pairs=[(i,i+1) for i in range(count-1)]
    elif method=='reference':pairs=[(0,i) for i in range(1,count)]
    else:pairs=[(0,1)] if count==2 else []
    hint='Nesneleri seçin; kullanılabilen ölçüler otomatik gösterilir.'
    if method=='free' and count>2:hint='Birden çok nesne için Tek tek, Çiftler, Zincir veya Referanstan yöntemini seçin.'
    if method=='pairs' and count%2:hint='Son nesnenin çifti eksik; bir nesne daha seçin.'
    for i,j in pairs:
        first,second=shapes[i],shapes[j];fi,si=infos[i],infos[j];dist=BRepExtrema_DistShapeShape(first,second);dist.Perform()
        if not dist.IsDone() or dist.NbSolution()<1:raise ValueError('Seçilen nesneler arasında CAD mesafesi hesaplanamadı.')
        p=xyz(dist.PointOnShape1(1));q=xyz(dist.PointOnShape2(1));delta=[q[k]-p[k] for k in range(3)]
        rows=[row('minimum','Minimum mesafe',dist.Value(),'mm','distance',a=p,b=q)]
        pp=[fi.get('point'),si.get('point')]
        if all(pp):rows.append(row('picked','Seçim noktaları mesafesi',math.dist(*pp),'mm','distance',a=pp[0],b=pp[1]))
        if 'center' in fi and 'center' in si:rows.append(row('centers','Merkezler arası',math.dist(fi['center'],si['center']),'mm','distance',a=fi['center'],b=si['center']))
        if all('axis' in n and 'axis_point' in n for n in (fi,si)):
            la=gp_Lin(gp_Pnt(*fi['axis_point']),gp_Dir(*fi['axis']));lb=gp_Lin(gp_Pnt(*si['axis_point']),gp_Dir(*si['axis']));rows.append(row('axes','Eksenler arası',la.Distance(lb),'mm','distance'))
        # The projected point distance uses the picked CAD points, or explicitly selected analytic centers.
        if vectors and all(pp):
            signed=dot([pp[1][k]-pp[0][k] for k in range(3)],direction);end=[pp[0][k]+signed*direction[k] for k in range(3)]
            rows.append(row('projected','Vektörde mesafe (noktalar)',signed,'mm','distance',a=pp[0],b=end))
        # Parallel support planes have a defined separation along a supplied vector.
        if vectors and fi.get('type')==si.get('type')=='Plane' and 'normal' in fi and 'normal' in si:
            n1,n2=fi['normal'],si['normal'];denom=dot(n2,direction)
            if abs(abs(dot(n1,n2))-1)<1e-8 and abs(denom)>1e-10:
                t=dot(n2,[si['plane_origin'][k]-fi['point'][k] for k in range(3)])/denom;end=[fi['point'][k]+t*direction[k] for k in range(3)]
                rows.append(row('plane_projected','Vektörde destek düzlemi mesafesi',abs(t),'mm','distance',a=fi['point'],b=end,note='Sonsuz paralel destek düzlemleri; yüz sınırlarını kullanmaz.'))
        for k,v in zip('XYZ',[dot(delta,csys[k]) for k in ('x','y','z')]):rows.append(row('delta_'+k,'Δ'+k,v,'mm','coordinates'))
        directions=[n.get('axis',n.get('direction',n.get('normal'))) if n.get('kind') in ('face','edge') else None for n in (fi,si)]
        if all(directions):
            u,v=map(norm,directions);angle=math.degrees(math.acos(max(-1,min(1,dot(u,v)))));rows.extend([row('angle','Açı',angle,'°','angle'),row('acute','Dar açı',min(angle,180-angle),'°','angle'),row('supplement','Bütünler açı',180-angle,'°','angle')])
        results.append({'angle_directions':directions if all(directions) else None,'title':f'Nesne {i+1} ↔ {j+1}','indices':[i,j],'anchor':[(p[k]+q[k])/2 for k in range(3)],'rows':rows,'graphic':{'a':p,'b':q,'key':'minimum','category':'distance','kind':'distance'}})
    if method=='free' and count==3 and all(n.get('kind') in ('vertex','facepoint','edgepoint','center') for n in infos):
        p=[n['point'] for n in infos];u=norm([p[1][k]-p[0][k] for k in range(3)]);v=norm([p[2][k]-p[0][k] for k in range(3)]);angle=math.degrees(math.acos(max(-1,min(1,dot(u,v)))))
        results.append({'title':'Üç nokta açısı · ilk nesne tepe','indices':[0,1,2],'anchor':p[0],'rows':[row('angle3','Açı',angle,'°','angle')],'graphic':{'a':p[1],'b':p[2],'vertex':p[0],'key':'angle3','category':'angle','kind':'angle'}})
    return {'results':results,'hint':hint,'method':method,'csys':csys,'direction':direction,'native_geometry':True}
