import argparse, copy, hashlib, io, json, subprocess, sys, tarfile, time
from pathlib import Path

def rect(x0,y0,x1,y1): return [[x0,y0],[x1,y0],[x1,y1],[x0,y1]]
def base(height=5000):
 return dict(op='frame',system=dict(name='Вектор-1',mid_rail_over=None),sub_type='vertical',parts='frame',cladding='porcelain',corners_x=[],contours=[dict(id='wall',pts=rect(0,0,3000,height))],joints_x=[600,1500,2400])
def calc(r):
 r=copy.deepcopy(r);r['calc']=dict(wind_region='II',terrain='B',height=30,q_clad=25,offset=230,na_max=3000,gamma_clad=1.1);return r

def cases():
 r={}
 x=base(5000);x['joints_x']=[996,1500,2004];x['contours']+=[dict(id='window',pts=rect(1000,2000,2000,3500))]
 r['window_ends_vertical_manual']=x
 r['window_ends_vertical_calc']=calc(x)
 o=copy.deepcopy(x);o['sub_type']='ortho';r['window_ends_ortho_manual']=o
 x=base(8000);x['contours'] += [dict(id='w1',pts=rect(1000,1000,2000,2500)),dict(id='w2',pts=rect(1000,4000,2000,5500))]
 r['two_aligned_windows']=x
 for h in (599,600,601,3300,6100):
  x=base(h);x['joints_x']=[1500]
  r['height_%s_manual'%h]=x;r['height_%s_calc'%h]=calc(x)
 cols=(1500,4500,7500,10500,13500)
 regular=[(x,y,x+1500,y+1800) for x in cols for y in (1200,4200,7200)]
 typical={'ordinary':(10200,regular),'top_300':(9300,regular),'ribbon_600':(10200,[(1200,y,16800,y+1800) for y in (1200,3600,6000)]),'sill_500':(10200,[(x,y,x+1500,y+1800) for x in cols for y in (500,3500,6500)]),'french_300':(10200,[(x,y,x+1500,y+1800) for x in cols for y in (300,3300,6300)])}
 for name,(h,w) in typical.items():
  x=base(h);x['contours']=[dict(id='wall',pts=rect(0,0,18000,h))]+[dict(id='w'+str(i),pts=rect(*win)) for i,win in enumerate(w)];x['joints_x']=list(range(608,18000,608));x['corners_x']=[0,18000]
  r['typical_'+name+'_manual']=x;r['typical_'+name+'_calc']=calc(x)
 x=base(9000);x['contours']=[dict(id='wall',pts=rect(0,0,12000,9000))];x['joints_x']=list(range(600,12000,600));x['corners_x']=[0,12000]
 r['blank_wall_calc']=calc(x)
 x=calc(base(3000));x['rail_profile']='ГП-60-40';r['gp6040_calc']=x
 x=calc(base(3000));x['calc']['scheme']='ortho';r['mismatch_vertical_ortho_calc']=x
 x=dict(op='frame',system=dict(name='Межэтажная'),sub_type='interfloor',parts='frame',cladding='porcelain',contours=[dict(id='wall',pts=rect(0,0,3000,3000))],joints_x=[300,900,1500,2100,2700],floors_y=[0,1500,3000],corners_x=[0],calc=dict(wind_region='II',terrain='B',height=8,q_clad=25,offset=170,na_max=3000))
 r['interfloor_calc']=x
 x=copy.deepcopy(x);x.pop('calc');r['interfloor_manual']=x
 # Our controlled 15-window input (1500x1500 mm), not an original German DWG.
 x=base(12000);x['contours']=[dict(id='wall',pts=rect(0,0,18000,12000))]+[dict(id='w%s_%s'%(a,b),pts=rect(a,b,a+1500,b+1500)) for a in cols for b in (1200,4200,7200)];x['joints_x']=list(range(608,18000,608));x['corners_x']=[0,18000]
 r['review_15_windows_manual']=x;r['review_15_windows_calc']=calc(x)
 return r

if len(sys.argv)>1 and sys.argv[1]=='--child':
 snap=Path(sys.argv[2]); output=Path(sys.argv[3]);sys.path.insert(0,str(snap/'AFrame/engine'))
 requests_bytes=Path(sys.argv[4]).read_bytes()
 if hashlib.sha256(requests_bytes).hexdigest()!=sys.argv[5]:
  raise RuntimeError('Canonical requests changed before child execution')
 requests=json.loads(requests_bytes)
 from frame_engine import op_frame
 records={}
 for name,request in requests.items():
  t=time.perf_counter()
  try: result=op_frame(copy.deepcopy(request))
  except Exception as e: result={'ok':False,'exception':repr(e)}
  records[name]={'request':request,'elapsed_seconds':time.perf_counter()-t,'result':result}
 output.write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8');sys.exit(0)

parser=argparse.ArgumentParser(description='Replay fixed AFrame inputs on build-103 through build-110 and causal commits. This is Python engine execution, not AutoCAD.')
parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[2])
parser.add_argument('--out', type=Path, required=True, help='New evidence directory; must not exist')
args=parser.parse_args()
ROOT=args.repo.resolve(); OUT=args.out.resolve(); OUT.mkdir(parents=True,exist_ok=False)
refs=['build-103','8360c23','19e3dd0','8f580aa','9d405ff','build-104','build-105','4c4c8c0','ca58b23','build-106','e0b1a7f','build-107','build-108','cf98f15','build-109','e2f4c0f','12be8a0','build-110','HEAD']
requests_path=OUT/'requests.json'
requests_bytes=json.dumps(cases(),ensure_ascii=False,indent=2).encode('utf-8')
requests_sha256=hashlib.sha256(requests_bytes).hexdigest()
expected_requests=json.loads(requests_bytes)
requests_path.write_bytes(requests_bytes)
resolved_refs=[(ref, subprocess.check_output(['git','rev-parse',ref+'^{commit}'],cwd=ROOT,text=True).strip()) for ref in refs]
manifest=[];summary={}
for ref,sha in resolved_refs:
 snap=OUT/'snapshots'/sha;snap.mkdir(parents=True,exist_ok=True)
 archive=subprocess.check_output(['git','archive',sha,'AFrame/engine'],cwd=ROOT)
 with tarfile.open(fileobj=io.BytesIO(archive)) as tf: tf.extractall(snap,filter='data')
 out=OUT/(ref.replace('/','_')+'-results.json')
 subprocess.run([sys.executable,__file__,'--child',str(snap),str(out),str(requests_path),requests_sha256],check=True)
 if requests_path.read_bytes()!=requests_bytes:
  raise RuntimeError('Canonical requests changed during '+ref)
 records=json.loads(out.read_text()); compact={}
 if set(records)!=set(expected_requests):
  raise RuntimeError('Result case names differ from canonical requests: '+ref)
 for name,record in records.items():
  if record.get('request')!=expected_requests[name]:
   raise RuntimeError('Result input differs from canonical request: '+ref+'/'+name)
  res=record['result']; sm=res.get('static_model') or (res.get('calc_report') or {}).get('static_model') or {}
  compact[name]={'ok':res.get('ok'),'error_code':res.get('error_code'),'exception':res.get('exception'),'error':res.get('error'),'rails':len(res.get('rails',[])),'brackets':len(res.get('brackets',[])),'local_issues':len(res.get('local_issues',[])),'calculation_status':res.get('calculation_status'),'model_reasons':sorted({i.get('reason','') for i in sm.get('geometric_screening',{}).get('reasons',[])}),'spans_at_x':{str(x):[(rr['y0'],rr['y1']) for rr in res.get('rails',[]) if abs(rr['x']-x)<1e-6] for x in (600,900,996,1400,1500,2004,2100)},'time_seconds':round(record['elapsed_seconds'],5)}
 summary[ref]=compact;manifest.append({'ref':ref,'sha':sha,'engine_archive_sha256':hashlib.sha256(archive).hexdigest(),'result_sha256':hashlib.sha256(out.read_bytes()).hexdigest(),'requests_sha256':requests_sha256,'cases':len(records),'built':sum(v['ok'] is True for v in compact.values()),'exceptions':sum(bool(v['exception']) for v in compact.values())})
 print(ref,manifest[-1]['built'],'/',len(records),'exceptions',manifest[-1]['exceptions'],flush=True)
(OUT/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
(OUT/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')

# Causal transitions, not merely a green aggregate of expected refusals.
checks=[]
def check(name,condition):
 if not condition: raise AssertionError(name)
 checks.append(name)
def result(ref,name): return summary[ref][name]
for ref in ('build-103','8360c23','19e3dd0'):
 check(ref+' ordinary windows built',result(ref,'review_15_windows_calc')['ok'] is True)
for ref in ('8f580aa','build-104','build-105'):
 check(ref+' introduced whole-facade topology refusal', result(ref,'review_15_windows_calc')['error_code']=='E_CALC_TOPOLOGY_UNSUPPORTED')
for ref in ('build-103','build-105','build-107','build-110','HEAD'):
 check(ref+' short manual geometry built',result(ref,'height_599_manual')['ok'] is True)
for ref in ('4c4c8c0','build-106'):
 check(ref+' short manual regression',result(ref,'height_599_manual')['error_code']=='E_UNSUPPORTED_RAIL')
for ref in ('build-103','build-105','build-109','build-110','HEAD'):
 check(ref+' exact stock retained', result(ref,'height_3300_manual')['spans_at_x']['1500']==[(0.0,3000.0),(3010.0,3300.0)])
for ref in ('4c4c8c0','build-106','build-107','build-108'):
 check(ref+' unrequested stock rebalance',result(ref,'height_3300_manual')['spans_at_x']['1500']==[(0.0,1645.0),(1655.0,3300.0)])
check('103 already has wrong window ends',result('build-103','window_ends_vertical_manual')['spans_at_x']['996']==[(0.0,1950.0),(3550.0,5000.0)])
check('HEAD corrected window ends',result('HEAD','window_ends_vertical_manual')['spans_at_x']['996']==[(0.0,2000.0),(3500.0,5000.0)])
check('103 already omits upper jamb',result('build-103','two_aligned_windows')['spans_at_x']['900']==[(950.0,2550.0)])
check('HEAD includes both window jambs',result('HEAD','two_aligned_windows')['spans_at_x']['900']==[(950.0,2550.0),(3950.0,5550.0)])
for name,code in [('gp6040_calc','E_PROFILE_SECTION_UNCONFIRMED'),('mismatch_vertical_ortho_calc','E_CALC_SCHEME_MISMATCH'),('interfloor_calc','E_CALC_MODEL_UNCONFIRMED')]:
 check('103 built '+name,result('build-103',name)['ok'] is True)
 check('HEAD separately restricted '+name,result('HEAD',name)['error_code']==code)
check('all calls completed without exception',not any(m['exceptions'] for m in manifest))
(OUT/'causal-checks.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'scope':'Historical Python engine differential checks; not native AutoCAD or engineering approval.'},ensure_ascii=False,indent=2),encoding='utf-8')
print('Causal assertions:',len(checks),'PASS; native AutoCAD: NOT_RUN')
