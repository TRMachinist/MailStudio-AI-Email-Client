"""CAD object-set analysis and coordinate-system based bounding solids."""
import math
from OCP.gp import gp_Ax1,gp_Ax2,gp_Ax3,gp_Dir,gp_Pnt,gp_Trsf
from OCP.BRepBuilderAPI import BRepBuilderAPI_Transform
from OCP.BRepBndLib import BRepBndLib
from OCP.Bnd import Bnd_Box,Bnd_OBB
from OCP.BRepGProp import BRepGProp
from OCP.GProp import GProp_GProps
from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox,BRepPrimAPI_MakeCylinder

def xyz(p):return [p.X(),p.Y(),p.Z()]
def norm(v):
    if len(v)!=3 or not all(math.isfinite(float(x)) for x in v):raise ValueError('Üç sonlu yön bileşeni gerekli.')
    size=math.sqrt(sum(float(x)**2 for x in v))
    if size<1e-12:raise ValueError('Yön vektörü sıfır olamaz.')
    return [float(x)/size for x in v]
def dot(a,b):return sum(x*y for x,y in zip(a,b))
def bbox(shape):
    b=Bnd_Box();BRepBndLib.AddOptimal_s(shape,b,False,False)
    if b.IsVoid():raise ValueError('Seçimde sınırlanabilir geometri yok.')
    return list(b.Get())

def frame(args,target=None):
    mode=args.get('orientation','custom');origin=[float(x) for x in args.get('origin',[0,0,0])]
    if len(origin)!=3 or not all(math.isfinite(x) for x in origin):raise ValueError('Koordinat sistemi orijini geçersiz.')
    if mode=='auto':
        if target is None:raise ValueError('Otomatik yön için geometri seçin.')
        obb=Bnd_OBB();BRepBndLib.AddOBB_s(target,obb,False,True,False)
        if obb.IsVoid():raise ValueError('Yönlü kutu hesaplanamadı.')
        ax=gp_Ax3(gp_Pnt(*xyz(obb.Center())),gp_Dir(*xyz(obb.ZDirection())),gp_Dir(*xyz(obb.XDirection())))
    elif mode=='world':ax=gp_Ax3()
    else:
        z=norm(args.get('direction',[0,0,1]));x=args.get('x_direction')
        if x is None:ax=gp_Ax3(gp_Pnt(*origin),gp_Dir(*z))
        else:
            x=norm(x);projection=dot(x,z);x=norm([x[i]-projection*z[i] for i in range(3)])
            ax=gp_Ax3(gp_Pnt(*origin),gp_Dir(*z),gp_Dir(*x))
    transform=gp_Trsf();transform.SetTransformation(ax)
    return ax,transform,{'origin':xyz(ax.Location()),'x':xyz(ax.XDirection()),'y':xyz(ax.YDirection()),'z':xyz(ax.Direction())}

def properties(shape):
    area=GProp_GProps();volume=GProp_GProps();length=GProp_GProps()
    BRepGProp.SurfaceProperties_s(shape,area,False,False)
    BRepGProp.VolumeProperties_s(shape,volume,True,False,False)
    BRepGProp.LinearProperties_s(shape,length,True,False)
    v=abs(volume.Mass());a=abs(area.Mass());l=abs(length.Mass());main=volume if v>1e-9 else area if a>1e-9 else length
    return {'volume_mm3':v,'area_mm2':a,'length_mm':l,'centroid':xyz(main.CentreOfMass()),'volume_method':'Seçilen kapalı gövdelerin toplamı; kesişimler Boolean birleştirilmez.'}

def radial_bound(local,b,cache,key):
    if key in cache:return cache[key]
    cx=(b[0]+b[3])/2;cy=(b[1]+b[4])/2;rectangle_radius=math.hypot((b[3]-b[0])/2,(b[4]-b[1])/2)
    n=max(32,min(512,4*math.ceil(math.pi/math.acos(rectangle_radius/(rectangle_radius+.01))/4))) if rectangle_radius>1e-10 else 32
    support=0
    for i in range(n//4):
        angle=2*math.pi*i/n
        if i==0:bb=b;x=cx;y=cy
        else:
            tr=gp_Trsf();tr.SetRotation(gp_Ax1(gp_Pnt(0,0,0),gp_Dir(0,0,1)),angle);bb=bbox(BRepBuilderAPI_Transform(local,tr,False).Shape());x=cx*math.cos(angle)-cy*math.sin(angle);y=cx*math.sin(angle)+cy*math.cos(angle)
        support=max(support,abs(bb[0]-x),abs(bb[3]-x),abs(bb[1]-y),abs(bb[4]-y))
    radius=min(rectangle_radius,support/math.cos(math.pi/n));result=(radius,max(0,radius-support));cache[key]=result;return result

def build_stock(target,args,cache,identity):
    ax,tr,csys=frame(args,target);local=BRepBuilderAPI_Transform(target,tr,False).Shape();b=bbox(local);inverse=tr.Inverted()
    off=[float(x) for x in args.get('offset',[0,0,0])]
    if len(off)!=3:raise ValueError('Üç pay değeri gerekli.')
    uniform=float(args.get('uniform_offset',0));box_pay=[float(x)+uniform for x in args.get('box_offsets',[off[0],off[0],off[1],off[1],off[2],off[2]])];cylinder_pay=[float(x)+uniform for x in args.get('cylinder_offsets',[off[0],off[2],off[2]])]
    kind=args.get('stock_type');active=box_pay if kind=='box' else cylinder_pay if kind=='cylinder' else box_pay+cylinder_pay
    if len(box_pay)!=6 or len(cylinder_pay)!=3 or any(not math.isfinite(x) for x in box_pay+cylinder_pay) or any(x<0 for x in active):raise ValueError('Ofset sonrası stok payları sıfır veya pozitif olmalı; stok parçanın içine giremez.')
    box_pay=[max(0,x) for x in box_pay];cylinder_pay=[max(0,x) for x in cylinder_pay]
    low=[b[i]-box_pay[i*2] for i in range(3)];high=[b[i+3]+box_pay[i*2+1] for i in range(3)];size=[high[i]-low[i] for i in range(3)]
    def world(p):return xyz(gp_Pnt(*p).Transformed(inverse))
    corners=[world([x,y,z]) for x in (low[0],high[0]) for y in (low[1],high[1]) for z in (low[2],high[2])]
    center=[(b[i]+b[i+3])/2 for i in range(3)];center[2]+=(cylinder_pay[2]-cylinder_pay[1])/2
    key=(tuple(identity),tuple(csys['x']),tuple(csys['z']))
    base_radius,margin=radial_bound(local,b,cache,key) if kind!='box' else (math.hypot((b[3]-b[0])/2,(b[4]-b[1])/2),0)
    radius=base_radius+cylinder_pay[0];height=b[5]-b[2]+cylinder_pay[1]+cylinder_pay[2]
    if min(size)<=1e-9 or height<=1e-9 or radius<=1e-9:raise ValueError('Stok için sıfırdan büyük üç boyut gerekli.')
    native=BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(center[0],center[1],b[2]-cylinder_pay[1]),gp_Dir(0,0,1)),radius,height).Shape() if kind=='cylinder' else BRepPrimAPI_MakeBox(gp_Pnt(*low),*size).Shape()
    native=BRepBuilderAPI_Transform(native,inverse,False).Shape()
    cylinder={'center':world(center),'radius':radius,'height':height,'volume_mm3':math.pi*radius*radius*height,'radial_bound_margin_mm':margin,'radial_offset':cylinder_pay[0],'lower_offset':cylinder_pay[1],'upper_offset':cylinder_pay[2],'method':'Native CAD destek sınırlarından seçili eksene göre güvenli zarf; global minimum silindir değildir.'}
    result={'unit':'mm','box_size':size,'box_volume_mm3':math.prod(size),'box_center':world([(low[i]+high[i])/2 for i in range(3)]),'local_min':low,'local_max':high,'part_local_bounds':b,'box_offsets':box_pay,'uniform_offset':uniform,'corners':corners,'axis':csys['z'],'csys':csys,'cylinder':cylinder,'body_ids':list(identity),'orientation_method':'OCCT en iyi uyum; global optimum garantisi yok' if args.get('orientation')=='auto' else 'Seçilen koordinat sistemi'}
    return result,native

def analyze_sets(first,second,args,first_info=None,second_info=None):
    from OCP.BRepExtrema import BRepExtrema_DistShapeShape
    ax,tr,csys=frame(args);result={'first_properties':properties(first),'csys':csys}
    if second is None:return result
    dist=BRepExtrema_DistShapeShape(first,second);dist.Perform()
    if not dist.IsDone() or dist.NbSolution()<1:raise ValueError('Seçim grupları arasında mesafe hesaplanamadı.')
    p=xyz(dist.PointOnShape1(1));q=xyz(dist.PointOnShape2(1));delta=[q[i]-p[i] for i in range(3)];direction=csys['z'];a=bbox(BRepBuilderAPI_Transform(first,tr,False).Shape());b=bbox(BRepBuilderAPI_Transform(second,tr,False).Shape())
    if b[2]>a[5]:start,end=a[5],b[2]
    elif b[5]<a[2]:start,end=a[2],b[5]
    else:start=end=max(a[2],b[2])
    base=dot([p[i]-csys['origin'][i] for i in range(3)],direction)
    projection_points=[[p[i]+direction[i]*(v-base) for i in range(3)] for v in (start,end)]
    result.update({'minimum_distance_mm':dist.Value(),'closest_first':p,'closest_second':q,'delta_world_mm':delta,'delta_csys_mm':[dot(delta,csys[k]) for k in ('x','y','z')],'signed_vector_distance_mm':dot(delta,direction),'projected_gap_mm':end-start,'projection_points':projection_points,'first_projection_range':[a[2],a[5]],'second_projection_range':[b[2],b[5]],'second_properties':properties(second)})
    if first_info and second_info:
        directions=[info.get('normal',info.get('direction',info.get('axis'))) for info in (first_info,second_info)]
        if all(directions):
            angle=math.degrees(math.acos(max(-1,min(1,dot(*directions)))));result['angle_degrees']=angle;result['acute_angle_degrees']=min(angle,180-angle)
    return result

def angle_three(points):
    if len(points)!=3:raise ValueError('Üç nokta seçin; ikinci nokta açının köşesidir.')
    first=norm([points[0][i]-points[1][i] for i in range(3)]);second=norm([points[2][i]-points[1][i] for i in range(3)])
    return {'angle_degrees':math.degrees(math.acos(max(-1,min(1,dot(first,second))))),'points':points}
