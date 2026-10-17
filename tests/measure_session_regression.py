"""Measure contract tests against the JSON worker, including ordered roles and true method changes."""
from pathlib import Path
import json,sys,subprocess,os,math,tempfile
from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox,BRepPrimAPI_MakeCylinder
from OCP.STEPControl import STEPControl_Writer,STEPControl_AsIs
root=Path(__file__).resolve().parents[1];data=Path(tempfile.mkdtemp(prefix='MailStudio-measure-'))
for name,shape in [('box',BRepPrimAPI_MakeBox(20,30,40).Shape()),('cylinder',BRepPrimAPI_MakeCylinder(5,50).Shape())]:
    writer=STEPControl_Writer();writer.Transfer(shape,STEPControl_AsIs);writer.Write(str(data/(name+'.stp')))
command=sys.argv[1:] or [sys.executable,str(root/'src/MailStudio.Technical/worker.py')];env=os.environ.copy();env['PATH']=str(Path(env.get('SystemRoot',r'C:\Windows'))/'System32');env.pop('PYTHONHOME',None);env.pop('PYTHONPATH',None)
p=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,encoding='utf8',env=env,creationflags=subprocess.CREATE_NO_WINDOW);seq=0;checks=[]
def call(op,args):
    global seq
    seq+=1;p.stdin.write(json.dumps({'id':seq,'operation':op,'args':args})+'\n');p.stdin.flush();result=json.loads(p.stdout.readline())
    if 'error' in result:raise RuntimeError(result['error'])
    return result['result']
def check(cond,msg):
    assert cond,msg;checks.append(msg);print('PASS '+msg,flush=True)
def item(specs,role='object'):return {'role':role,'members':specs}
def session(items,method='free'):return call('measure_session',{'items':items,'method':method})
def pairs(result):return [r for r in result['results'] if len(r['indices'])==2]
def row(result,key):return next(r for r in result['rows'] if r['key']==key)
try:
    box=call('step_open',{'path':str(data/'box.stp')})
    points=[]
    for xyz in ([0,0,0],[20,0,0],[20,30,0],[0,30,0]):points.append({'kind':'vertex','id':next(v['id'] for v in box['vertices'] if math.dist(v['point'],xyz)<1e-6)})
    objects=[item([spec]) for spec in points]
    check([row(r,'minimum')['value'] for r in pairs(session(objects,'pairs'))]==[20,20],'Pairs uses disjoint ordered pairs instead of A/B group extrema')
    check([row(r,'minimum')['value'] for r in pairs(session(objects,'chain'))]==[20,30,20],'Chain measures consecutive objects in list order')
    check(all(abs(x-y)<1e-6 for x,y in zip([row(r,'minimum')['value'] for r in pairs(session(objects,'reference'))],[20,math.sqrt(1300),30])),'Reference measures first listed object to every later object')
    check(not pairs(session(objects,'individual')) and len(session(objects,'individual')['results'])==4,'Individual returns per-object properties without pair distances')
    check(not pairs(session(objects)) and 'yöntemini seçin' in session(objects)['hint'],'ambiguous free selection gives method guidance rather than arbitrary pair permutations')
    angle=session(objects[:3])['results'][-1];check(abs(row(angle,'angle3')['value']-math.degrees(math.atan2(30,20)))<1e-6,'three-point free angle uses the first listed point as vertex')
    reordered=session([objects[1],objects[0],objects[2]],'reference');check(abs(row(pairs(reordered)[1],'minimum')['value']-30)<1e-6,'reordering changes the actual reference and resulting distances')
    vector={'role':'vector','direction':[1,0,0]};r=session([objects[0],objects[2],vector]);check(abs(row(pairs(r)[0],'projected')['value']-20)<1e-6 and abs(row(pairs(r)[0],'minimum')['value']-math.sqrt(1300))<1e-6,'explicit vector role projects selected points separately from Euclidean distance')
    infos=[call('inspect',{'kind':'face','id':f['id'],'point':[10,15,20]}) for f in box['faces']];xfaces=[i for i in infos if abs(i.get('normal',[0])[0])>.9];r=session([item([{'kind':'face','id':f['id'],'point':f['point']}]) for f in xfaces]+[{'role':'vector','direction':[1,1,0]}]);check(abs(row(pairs(r)[0],'plane_projected')['value']-20*math.sqrt(2))<1e-6,'parallel support planes use distance along vector rather than closest-point dot product')
    check('destek düzlemleri' in row(pairs(r)[0],'plane_projected')['note'],'support-plane result explicitly states its infinite-plane domain')
    r=session([item([{'kind':'body','id':1},{'kind':'body','id':1}],'set')]);check(abs(row(r['results'][0],'volume')['value']-24000)<1e-5,'Object Set accumulates native body properties without duplicate counting')
    cyl=call('step_open',{'path':str(data/'cylinder.stp')});edges=[call('inspect',{'kind':'edge','id':e['id'],'point':e['points'][0]}) for e in cyl['edges']];circles=[e for e in edges if e.get('type')=='Circle'];specs=[{'kind':'edge','id':e['id'],'point':e['point']} for e in circles]
    one=session([item([specs[0]])])['results'][0];check(abs(row(one,'radius_mm')['value']-5)<1e-6 and abs(row(one,'diameter_mm')['value']-10)<1e-6 and one['graphic']['kind']=='diameter','single circular edge automatically gives radius diameter length and CAD diameter endpoints')
    two=pairs(session([item([s]) for s in specs]))[0];check(abs(row(two,'centers')['value']-50)<1e-6 and abs(row(two,'axes')['value'])<1e-6,'circle center separation and infinite axis separation are distinct measurements')
    centers=[item([dict(s,kind='center',source_kind='edge')],'point') for s in specs];r=pairs(session(centers))[0];check(abs(row(r,'minimum')['value']-50)<1e-6,'constructed center points are native analytic centers rather than points snapped back to the circle')
    check(all(abs(x-y)<1e-6 for x,y in zip(call('inspect',{'kind':'center','source_kind':'edge','id':specs[0]['id'],'point':[999,999,999]})['point'],circles[0]['center'])),'center construction derives coordinates from CAD rather than trusting supplied display point')
    blocked=False
    try:session([item([specs[0]]),{'role':'vector','direction':[0,0,0]}])
    except RuntimeError:blocked=True
    check(blocked,'invalid zero measurement vector is rejected')
    print(json.dumps({'passed':len(checks),'checks':checks,'data':str(data)},ensure_ascii=False),flush=True)
finally:p.kill();p.wait(timeout=10)
