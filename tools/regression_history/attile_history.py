"""Replay actual historical ATTILE adapter methods; API doubles, not AutoCAD.
All reference commits are pinned, including audit-2026-10-09 = a5cf506.
The CAD double harness is also read from a5cf506, not the current working tree.
Source snapshots and full results go to the required new --out. No checkout/reset.
"""
import argparse, hashlib, json, math, pathlib, re, subprocess, sys
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--repo',required=True,type=pathlib.Path,help='Repository with complete audited Git history')
parser.add_argument('--out',required=True,type=pathlib.Path,help='New output directory; existing output is refused')
parser.add_argument('--cases',type=pathlib.Path,default=pathlib.Path(__file__).with_name('attile_cases.json'))
args=parser.parse_args()
ROOT=args.repo.resolve(); OUT=args.out.resolve()
if OUT.exists(): parser.error('--out must not exist; historical probes are always rebuilt and rerun')
OUT.mkdir(parents=True)
fixture_bytes=args.cases.read_bytes()
script_bytes=pathlib.Path(__file__).read_bytes()
fixture=json.loads(fixture_bytes.decode('utf-8'))
assert fixture['schema_version']==1 and fixture['native_autocad'] is False
cases=fixture['cases']
assert len(cases)==12 and len({x['id'] for x in cases})==12
behaviors={'ordinary','snapshot','native_increment_1mm','integer_value','readonly_exact','setter_rejects','coupled'}
for case in cases:
 assert case['behavior'] in behaviors
 assert all(math.isfinite(case[k]) for k in ('width','height','geometry_width_error'))
 assert case['width']>0 and case['height']>0
sys.path.insert(0,str(ROOT/'tools/quantities_3009'))
from probe_runtime import compile_probe, command
POINTS={
 'build-103':'611bdf32a82f357cf63d763a9b7d631e1b6d74df',
 'build-104':'ec5160983f61ce5668369d2513313391db4d6c5d',
 'build-105':'c4009838875aa0f225971153bf4b54fe288e8e79',
 'build-106':'9fa9ae7dbb69b17e0303d6fab28bb62774273f9b',
 'build-107':'b83896ee904bb64d0321e2357723cdb6e6196dfa',
 'build-108':'d164be90e04227e33c7159454220e262484b2c50',
 'build-109':'997476ac119192a09b8767ba1d6ca3131ba09a06',
 'build-110':'a6d0a85efd068d30a20b0bd83dfbf69fc872389e',
 'audit-2026-10-09':'a5cf5066a4e673efef0b115ebe13738b7d305b4b',
 'hard_fail_parent':'6d53e3042ce02102094f87943c54bf3115b0eb88',
 'hard_fail_first':'8360c238f342d82c1dd83c63789729506c34df18',
 'final_read_parent':'bc78d21b33f50d26ad825bb938247ecc03dce83b',
 'final_read_fix':'aff2c5fecdbb43c4c13a86fee5517776a90bb3ce',
 'cache_fix_parent':'12af31ae269c229f5a48b2d9578b0a704b101380',
 'cache_fix':'de6791397ddb125597de4aeae3f425b26c21ee90',
}
def git(*args):return subprocess.check_output(['git',*args],cwd=ROOT,text=True)
def show(sha,path):return git('show',sha+':'+path)
def extract(source,name):
 m=re.search(r'(?m)^\s*(?:private|internal|public) (?:static )?[^\n]+ '+name+r'\(',source)
 if not m:raise ValueError('Missing actual method: '+name)
 start=m.start();brace=source.index('{',start);depth=1;end=brace+1
 while depth:
  depth+=(source[end]=='{')-(source[end]=='}');end+=1
 return source[start:end]
def digest(v):return hashlib.sha256(v.encode()).hexdigest()
main=r'''
    static class HistoryCheck
    {
        static Dictionary<string, object> Case(string scenario, bool sample, double width, double height, string behavior, double geometryError)
        {
            var b = new BlockReference();
            b.Properties.Add(new PropertyState { Name="ширина", Value=1240.0 });
            b.Properties.Add(new PropertyState { Name="высота", Value=1377.0 });
            b.Properties.Add(new PropertyState { Name="видимость", Value="definition-state" });
            var p = new TilePatternCommand.Proto { W=width, H=height,
                Te=sample ? new TilePatternCommand.SampleElem() : null };
            if (sample) { p.Te.Color="source-color"; p.Te.Props["видимость"]="source-state"; }
            if (behavior=="snapshot") b.SnapshotReaders=true;
            if (behavior=="native_increment_1mm") { b.Properties[1].Value=65.0; b.Properties[1].Increment=1; }
            if (behavior=="integer_value") { b.Properties[1].Value=(short)65; b.Properties[1].TypeCode=70; }
            if (behavior=="readonly_exact") {
                b.Properties[0].Value=p.W; b.Properties[1].Value=p.H;
                b.Properties[0].ReadOnly=b.Properties[1].ReadOnly=true;
            }
            if (behavior=="setter_rejects") b.Properties[1].Reject=true;
            if (behavior=="coupled") b.Coupled=true;
            b.GeometryWidthError=geometryError;
            BlockReference.Next=b;
            bool returned=false, ok=false; string error=null;
            try { ok=TilePatternCommand.Run(p); returned=true; }
            catch (Exception ex) { error=ex.GetType().Name+": "+ex.Message; }
            b=BlockReference.Next;
            double w=Convert.ToDouble(b.Properties[0].Value,CultureInfo.InvariantCulture);
            double h=Convert.ToDouble(b.Properties[1].Value,CultureInfo.InvariantCulture);
            var exts=CladCommand.CellExtents(new Transaction(),b);
            bool exactProps=Math.Abs(w-p.W)<=1e-6 && Math.Abs(h-p.H)<=1e-6;
            bool exactGeometry=Math.Abs(exts.MaxPoint.X-exts.MinPoint.X-p.W)<=1e-6 &&
                Math.Abs(exts.MaxPoint.Y-exts.MinPoint.Y-p.H)<=1e-6;
            return new Dictionary<string,object> {
                {"scenario",scenario},{"mode",sample?"sample":"definition"},
                {"requested",new[]{p.W,p.H}},{"returned_without_throw",returned},{"adapter_ok",ok},
                {"actual",new[]{w,h}},{"properties_exact",exactProps},{"geometry_exact",exactGeometry},
                {"height_clr",b.Properties[1].Value.GetType().FullName},{"height_setter_calls",b.Properties[1].Writes},
                {"attributes_fill_calls",b.Attributes},{"appearance_preserved",sample?(Equals(b.Color,"source-color") && Equals(b.Properties[2].Value,"source-state")):true},
                {"error",error}
            };
        }
        static int Main()
        {
            var rows=new List<Dictionary<string,object>>();
            __SCENARIO_CALLS__
            var result=new Dictionary<string,object>{{"cases",rows},{"prototype_keys",new[]{
                TilePatternCommand.PrototypeSizeKey(245.001,35.85),TilePatternCommand.PrototypeSizeKey(245.004,35.85)}},
                {"native_autocad",false}};
            Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result));
            return 0;
        }
    }
}
'''
base=show(POINTS['audit-2026-10-09'],'AClad/tests/test_dynamic_block_size.cs')
base_hash=digest(base)
main=main.replace('__SCENARIO_CALLS__','\n'.join('foreach(bool sample in new[]{false,true}) rows.Add(Case('+','.join([json.dumps(c['id']), 'sample', repr(float(c['width'])),repr(float(c['height'])),json.dumps(c['behavior']),repr(float(c['geometry_width_error']))])+'));' for c in cases))
base=base[:base.index('    static class DynamicBlockSizeCheck')]+main
base=base.replace('public bool ReadOnly, Reject, Throw, ThrowMetadata;','public bool ReadOnly, Reject, Throw, ThrowMetadata; public int Writes;')
base=base.replace('block.Writes++;','block.Writes++; state.Writes++;')
base=base.replace('public string Layer;','public string Layer; public object Color="definition-color"; public bool IsDynamicBlock { get { return true; } } public double GeometryWidthError;')
base=base.replace('BadGeometry = Next.BadGeometry;','BadGeometry = Next.BadGeometry; GeometryWidthError=Next.GeometryWidthError;')
base=base.replace('(block.BadGeometry ? 1 : 0)', '(block.BadGeometry ? 1 : 0) + block.GeometryWidthError')
base=base.replace('internal class SampleElem { public void ApplyLook(BlockReference b) { b.Looks++; } }',
 '''internal class SampleElem { public object Color; public string W="ширина", H="высота";
 public readonly Dictionary<string,object> Props=new Dictionary<string,object>(); __APPLY_LOOK__ }''')
run=extract(base,'Run')
base=base.replace(run,'''internal static bool Run(Proto p) { bool ok;
 MakeDynRef(new Transaction(),new BlockTableRecord(),p,125.5,-42.5,new Dictionary<ObjectId,List<ObjectId>>(),out ok); return ok; }''')
results={}
assertions=[]
def check(condition,message):
 assertions.append({'pass':bool(condition),'message':message})
for label,sha in POINTS.items():
 d=OUT/label;d.mkdir(exist_ok=True)
 clad=show(sha,'AClad/src/ACladPlugin/CladCommand.cs');tile=show(sha,'AClad/src/ACladPlugin/TilePatternCommand.cs')
 tracked=git('ls-tree','-r','--name-only',sha,'AClad/src/ACladPlugin/LayoutSafety.cs').strip()
 safety=show(sha,tracked) if tracked else 'namespace ACladPlugin { internal class LayoutSafety { } }'
 methods=[extract(clad,'TrySetNum'),extract(clad,'SameValue')]
 for name in ('DynSizeMatches','RequirePlacement','DynSizeDescription'):
  if re.search(r'\b'+name+r'\(',clad):methods.append(extract(clad,name))
 key=extract(tile,'PrototypeSizeKey') if 'internal static string PrototypeSizeKey(' in tile else '''internal static string PrototypeSizeKey(double w,double h) { return F2(w)+"|"+F2(h); }'''
 if 'PrototypeSizeKey' not in tile: assert re.search(r'F2\(w\)\s*\+\s*"\|"\s*\+\s*F2\(h\)',tile)
 source=base.replace('__CLAD_METHODS__','\n'.join(methods)).replace('__MAKE_DYN_REF__',extract(tile,'MakeDynRef')).replace('__PROTO_SIZE_KEY__',key).replace('__APPLY_LOOK__',extract(tile,'ApplyLook'))
 (d/'HistoryCheck.cs').write_text(source);(d/'LayoutSafety.cs').write_text(safety)
 (d/'CladCommand.source.cs').write_text(clad);(d/'TilePatternCommand.source.cs').write_text(tile)
 compiled=compile_probe([d/'HistoryCheck.cs',d/'LayoutSafety.cs'],['System.Web.Extensions'],d/'HistoryCheck.exe')
 (d/'compile.log').write_text(compiled.stdout+compiled.stderr)
 if compiled.returncode:raise RuntimeError(label+': '+compiled.stdout+compiled.stderr)
 ran=subprocess.run(command(d/'HistoryCheck.exe'),capture_output=True,text=True)
 (d/'run.log').write_text(ran.stdout+ran.stderr)
 if ran.returncode:raise RuntimeError(label+': '+ran.stdout+ran.stderr)
 result=json.loads(ran.stdout);result.update(commit=sha,label=label,methods_sha256={
   'MakeDynRef':digest(extract(tile,'MakeDynRef')),'TrySetNum':digest(extract(clad,'TrySetNum')),
   'ApplyLook':digest(extract(tile,'ApplyLook')),'LayoutSafety':digest(safety),'PrototypeSizeKey':digest(key)})
 result['prototype_collision']=result['prototype_keys'][0]==result['prototype_keys'][1]
 (d/'results.json').write_text(json.dumps(result,ensure_ascii=False,indent=2));results[label]=result
 print(label, 'cases',len(result['cases']),'returned',sum(x['returned_without_throw'] for x in result['cases']),
       'returned_exact',sum(x['returned_without_throw'] and x['geometry_exact'] and x['properties_exact'] for x in result['cases']),
       'prototype_collision',result['prototype_collision'])
# Explicit historical assertions: a completion is not automatically correct geometry.
old={'build-103','hard_fail_parent'}
early={'build-104','build-105','build-106','build-107','build-108','hard_fail_first','final_read_parent'}
current={'audit-2026-10-09','cache_fix'}
for label, result in results.items():
 check(len(result['cases'])==len(cases)*2,label+': complete case/branch count')
 for spec in cases:
  for mode in ('definition','sample'):
   rows=[c for c in result['cases'] if c['scenario']==spec['id'] and c['mode']==mode]
   check(len(rows)==1,label+': unique '+spec['id']+'/'+mode)
   row=rows[0]; behavior=spec['behavior']; ge=spec['geometry_width_error']
   expect_return=(label in old or (behavior in {'ordinary','snapshot','readonly_exact'} and ge<=0.5 and not(label in early and behavior in {'snapshot','readonly_exact'})))
   check(row['returned_without_throw']==expect_return,label+': completion '+spec['id']+'/'+mode)
   check(row['appearance_preserved'],label+': ApplyLook '+spec['id']+'/'+mode)
   if behavior in {'ordinary','snapshot','readonly_exact'}:
    check(row['properties_exact'],label+': final property pair '+spec['id']+'/'+mode)
    check(row['geometry_exact']==(ge==0),label+': independent model geometry '+spec['id']+'/'+mode)
   if behavior in {'native_increment_1mm','integer_value'}:
    height=65 if behavior=='integer_value' and label in current else 36
    check(row['actual'][1]==height,label+': actual rounding value '+spec['id']+'/'+mode)
    check(row['height_setter_calls']==(0 if height==65 else 1),label+': setter calls '+spec['id']+'/'+mode)
   if behavior=='readonly_exact' and label in old:
    check(row['adapter_ok'] is False,label+': old completion despite readonly ok=false')
 check(result['prototype_collision']==(label not in current),label+': prototype key collision')
# Pin first-bad/fix boundaries by exact method identity as well as observed cases.
for a,b in [('build-103','hard_fail_parent'),('build-104','hard_fail_first'),('build-108','final_read_parent'),('build-109','final_read_fix'),('audit-2026-10-09','cache_fix')]:
 check(results[a]['methods_sha256']['MakeDynRef']==results[b]['methods_sha256']['MakeDynRef'],a+'/'+b+': MakeDynRef identity')
failures=[a['message'] for a in assertions if not a['pass']]
report={'status':'FAIL' if failures else 'PASS','probes':results,'assertions':assertions,'failures':failures,'native_autocad':False,
 'script_sha256':hashlib.sha256(script_bytes).hexdigest(),
 'fixture_sha256':hashlib.sha256(fixture_bytes).hexdigest(),'double_harness_commit':POINTS['audit-2026-10-09'],'double_harness_sha256':base_hash,
 'limitations':['Property behavior is deliberately injected; source DWG behavior is not inferred.',
 'Actual MakeDynRef/TrySetNum/ApplyLook methods are executed. From/Probe UI/transactions/native geometry are not.',
 'Historical caller behavior was audited statically; no full ATTILE command was executed.',
 'Legacy prototype key uses the exact original F2 subexpression, not a historical method named PrototypeSizeKey.',
 'The 0.15 mm geometry offset case exposes the existing 0.5 mm placement tolerance; its completion is not asserted as exact geometry.']}
(OUT/'results.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
print('Historical assertions:',len(assertions),'failures:',len(failures),'native AutoCAD: NOT RUN')
if failures:
 print('\n'.join(failures));raise SystemExit(1)
