"""Replay classic-menu regressions across immutable releases 103–110.

Uses actual historical C# sources with existing CAD doubles, never AutoCAD.
No checkout, network, or build cache; only requested output is written. Requires Mono/mcs
or Windows .NET SDK and the pinned Git objects locally available. Exit zero
means the expected historical transition was reproduced, not all old tests
passed: releases 103–108 deliberately expose three old failed assertions.
"""
import sys,json,subprocess,importlib.util,argparse,hashlib
from pathlib import Path
sys.dont_write_bytecode = True
parser=argparse.ArgumentParser(description='Replay unchanged historical classic-menu code with current CAD doubles; not native AutoCAD.')
parser.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[2])
parser.add_argument('--out',type=Path,required=True)
args=parser.parse_args()
ROOT=args.repo.resolve();OUT=args.out.resolve();OUT.mkdir(parents=True,exist_ok=False)
spec=importlib.util.spec_from_file_location('startup',ROOT/'tools/facades/test_startup.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
head=module.CLASSIC.split('public class Probe {',1)[0]
reporters='\n'.join('namespace '+name+'Plugin { internal static class Plugin { public static void ReportStartupFailure(string stage, Exception error) { } } }' for _,name in module.MODULES)
main=r'''
public class Probe {
 static int Failures; static void Expect(bool value,string name) { Console.WriteLine((value?"PASS: ":"FAIL: ")+name); if(!value)Failures++; }
 static void Init() { AFacadesPlugin.FacadesClassic.Init(); ACladPlugin.FacadesClassic.Init(); AFramePlugin.FacadesClassic.Init(); }
 static void Main(string[] args) {
  var app=(FakeApplication)Autodesk.AutoCAD.ApplicationServices.Application.AcadApplication;
  if(args[0]=="partial") {
   FakeMenu.FailAfterAdds=1; AFacadesPlugin.FacadesClassic.Init();
   var menu=app.MenuGroups.Group.Menus.Item(0);
   Autodesk.AutoCAD.ApplicationServices.Application.Quit();
   Expect(menu.Count==0 && !menu.OnMenuBar,"partial first initialization installs cleanup before creating menu");
  } else {
   Init(); var menu=app.MenuGroups.Group.Menus.Item(0);int count=menu.Count;
   Expect(count>=6 && menu.OnMenuBar,"ordinary startup creates shared menu");
   Init();Expect(menu.Count==count,"ordinary startup is idempotent");
   if(args[0]=="normal") {
    Autodesk.AutoCAD.ApplicationServices.Application.Quit();Expect(menu.Count==0 && !menu.OnMenuBar,"ordinary exit removes transient menu");
    Autodesk.AutoCAD.ApplicationServices.Application.Abort();Expect(menu.Count==count && menu.OnMenuBar,"cancelled exit restores transient menu");
   } else if(args[0]=="reorder") {
    var other=new FakeGroup();app.MenuGroups.All.Insert(0,other);var foreign=other.Menus.Add("Пользователь");foreign.AddMenuItem(0,"Линия","_LINE ");foreign.InsertInMenuBar(0);
    Autodesk.AutoCAD.ApplicationServices.Application.Quit();Expect(menu.Count==0 && !menu.OnMenuBar,"exit after MenuGroups reorder removes transient menu");
    Expect(foreign.Count==1 && foreign.OnMenuBar,"unrelated menu survives cleanup");
    Init();Expect(menu.Count==count && menu.OnMenuBar && other.Menus.Count==1,"restart after reorder reuses shared menu without duplicate");
   }
  }
  Environment.Exit(Failures==0?0:1);
 }
}
'''
PINNED = {
 103: '611bdf32a82f357cf63d763a9b7d631e1b6d74df',
 104: 'ec5160983f61ce5668369d2513313391db4d6c5d',
 105: 'c4009838875aa0f225971153bf4b54fe288e8e79',
 106: '9fa9ae7dbb69b17e0303d6fab28bb62774273f9b',
 107: 'b83896ee904bb64d0321e2357723cdb6e6196dfa',
 108: 'd164be90e04227e33c7159454220e262484b2c50',
 109: '997476ac119192a09b8767ba1d6ca3131ba09a06',
 110: 'a6d0a85efd068d30a20b0bd83dfbf69fc872389e',
}
if not module.available():
 raise SystemExit('Mono/mcs or Windows dotnet required; no probes executed')
rows=[]
for n, ref in PINNED.items():
 directory=OUT/'menu-probes'/f'build-{n}';directory.mkdir(parents=True,exist_ok=True);sources=[]
 for folder,name in module.MODULES:
  p=directory/(name+'Classic.cs');p.write_bytes(subprocess.check_output(['git','-C',str(ROOT),'show',ref+':'+folder+'/src/'+name+'Plugin/FacadesClassic.cs']));sources.append(p)
 p=directory/'Probe.cs';p.write_text(head.replace('FAKE_PLUGINS',reporters)+main);exe=directory/'Probe.exe'
 result=module.compile_probe([p,*sources],['Microsoft.CSharp'],exe)
 if result.returncode:raise RuntimeError(result.stdout+result.stderr)
 for case in ('normal','partial','reorder'):
  run=subprocess.run(module.command(exe)+[case],capture_output=True,text=True)
  (directory/(case+'.log')).write_text(run.stdout+run.stderr)
  expected = {'normal':(0,4,0), 'partial':((1,0,1) if n<=108 else (0,1,0)),
              'reorder':((1,3,2) if n<=108 else (0,5,0))}[case]
  actual=(run.returncode,run.stdout.count('PASS:'),run.stdout.count('FAIL:'))
  rows.append({'build':n,'sha':ref,'scenario':case,'exit_code':actual[0],'pass':actual[1],
               'fail':actual[2],'expected':list(expected),'transition_confirmed':actual==expected})
  print(rows[-1],flush=True)
report={'native_autocad_executed':False,'harness_sha256':hashlib.sha256(
 (ROOT/'tools/facades/test_startup.py').read_bytes()).hexdigest(), 'rows':rows,
 'all_expected_transitions_confirmed':all(r['transition_confirmed'] for r in rows)}
(OUT/'menu-history.json').write_text(json.dumps(report,indent=2)+'\n')
if not report['all_expected_transitions_confirmed']:
 raise SystemExit('FAIL: historical behavior differs from the pinned expectations; inspect logs')
print('PASS: 24 expected historical scenarios confirmed; 18 deliberately exposed old failed assertions; native AutoCAD NOT_RUN')
