"""Native settings, actual metadata/freshness guard and engine/producer boundary.

Only selected CAD objects are doubled; no live AutoCAD behavior is claimed.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/quantities_3009'))
from probe_runtime import available, command, compile_probe
sys.path.insert(0, str(ROOT / 'AFrame/engine'))
import frame_engine
from test_frame_solution_selection import example_selection


def compile_actual_settings(out):
    out = Path(out).resolve(); out.mkdir(parents=True, exist_ok=True)
    src = ROOT / 'AFrame/src/AFramePlugin'
    command_source = (src / 'FrameCommand.cs').read_text(encoding='utf-8')
    read = command_source[command_source.index('        private static string ReadFrameMetadata('):command_source.index('        private static Extents3d? SelRegion(')]
    start = command_source.index('                var currentSolutionScope = new FrameSolutionSelectionScope(true);')
    end = command_source.index('                var bt = (BlockTable)tr.GetObject', start)
    fresh = command_source[start:end]
    result_start = command_source.index('            var solutionRoots = FrameSolutionSelectionScope.EngineRoots(')
    result_end = command_source.index('            var rails = Get(res,', result_start)
    result_gate = command_source[result_start:result_end]
    axis_snapshot = '            var axisInputContours = new List<Dictionary<string, object>>(polyData.Values);'
    assert axis_snapshot in command_source
    axis_start = command_source.index('                double bbx0 = double.MaxValue, bbx1 = double.MinValue;')
    axis_end = command_source.index('                // 26.09: у плитки', axis_start)
    axis_bounds = command_source[axis_start:axis_end]
    guard = out / 'ActualFrameGuard.cs'
    guard.write_text('''using System; using System.Text; using System.Collections.Generic; using System.Web.Script.Serialization;
namespace AFramePlugin { internal static class FrameGuardBridge {
// This older suite exercises the unbound 2B1 branch. The actual project
// operation and command paths are executed by project_params_0110 separately.
private sealed class UnboundProjectOperation {
internal bool IsBound { get { return false; } }
internal void ValidateGroup(IEnumerable<string> roots) {}
internal void ValidateClamps(IEnumerable<string> roots) {}
internal void VerifySavedSettings(int owner, Dictionary<string,object> settings) {}
}
private const string XKeyFrame = "ATFRAME";
private static object Get(Dictionary<string,object> d,string k){object v;return d!=null&&d.TryGetValue(k,out v)?v:null;}
private static string SafeStr(object value){return value == null ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);}
internal static Dictionary<string,object> Read(Transaction tr,JavaScriptSerializer ser,Entity e){return ReadFrameSettings(tr,ser,e);}
internal static void Fresh(Transaction tr,JavaScriptSerializer ser,Dictionary<int,Tuple<string,bool>> solutionOwners,HashSet<int> canonicalSolutionOwners,List<string> solutionRoots,FrameSolutionSelectionContext resolvedSolutionContext){
var projectOperation=new UnboundProjectOperation();
''' + fresh + '''
}
internal static void SolutionGate(FrameSettings fs,Dictionary<string,object> res,FrameSolutionSelectionScope solutionScope,Dictionary<string,List<string>> oldByRoot){
var partToRoot=new Dictionary<string,string>(); bool clampsOnly=fs.ClampsOnly; string declaredSolutionReason=null;
var projectOperation=new UnboundProjectOperation();
''' + result_gate + '''
}
internal static double[] AxisBoundsAfterZoneDedup(Dictionary<string,Dictionary<string,object>> polyData){
''' + axis_snapshot + '''
polyData.Clear();
''' + axis_bounds + '''
return new[]{bbx0,bbx1,bby0,bby1};
}
''' + read + '\n}}', encoding='utf-8')
    producer = (src / 'FrameQuantities.cs').read_text(encoding='utf-8')
    marker = '        internal static QuantityReport BuildReport('
    assert producer.count(marker) == 1
    prefix = '\n'.join(line for line in producer.splitlines() if line.startswith('using ') and 'Autodesk.' not in line)
    categories = re.findall(r'(?m)^\s*private static readonly string\[\] Categories = .*?;', producer)
    assert len(categories) == 1
    extracted = out / 'ActualFrameProducer.cs'
    extracted.write_text(prefix + '\nnamespace AFramePlugin { internal sealed class FrameQuantities {\n' + categories[0] + '\n' + producer[producer.index(marker):], encoding='utf-8')
    sources = [Path(__file__).with_name('SettingsContractProbe.cs'), src / 'FrameSettings.cs', src / 'FrameSolutionSelection.cs', src / 'FrameProjectParameters.cs',
               ROOT / 'Common/FacadeQuantities.cs', guard, extracted]
    executable = out / 'SettingsContractProbe.exe'
    compiled = compile_probe(sources, ['System.Web.Extensions'], executable)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        raise RuntimeError(compiled.stdout + compiled.stderr)
    # Supplemental ordering assertion; behavior is exercised through actual extracted guards.
    assert command_source.index('solutionScope.AddCanonical(') < command_source.index('using (var ff = new FrameForm(')
    assert command_source.index('FrameSolutionSelectionScope.PreferCanonicalWindowSettings(') < command_source.index('using (var ff = new FrameForm(')
    assert result_start < start < end
    assert command_source.index(axis_snapshot) < command_source.index('polyData.Remove(') < axis_start
    assert command_source.count('fs.SaveLast();') == 1
    assert command_source.index('fs.SaveLast();') > command_source.index('tr.Commit();', end)
    return executable


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    if not available():
        print('BLOCKED: native .NET compiler/runtime unavailable'); return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix='solution_settings_'))).resolve(); out.mkdir(parents=True, exist_ok=True)
    executable = compile_actual_settings(out)
    equality_diagnostic = out / 'serializer_equality_diagnostic.json'
    subprocess.run(command(executable) + ['--equality-diagnostic', str(equality_diagnostic)], check=True)
    def run(mode, name, payload):
        infile, outfile = out / (name + '_input.json'), out / (name + '_output.json')
        infile.write_text(json.dumps(payload, ensure_ascii=False, allow_nan=False), encoding='utf-8')
        subprocess.run(command(executable) + [mode, str(infile), str(outfile)], check=True)
        return json.loads(outfile.read_text(encoding='utf-8'))
    checks = 0
    def check(value, message):
        nonlocal checks
        checks += 1
        assert value, message
    def rect(x, y, w, h): return [[x,y],[x+w,y],[x+w,y+h],[x,y+h]]
    raw = {'op':'frame','system':'Вектор-1','sub_type':'vertical','cladding':'porcelain','parts':'frame',
           'joints_x':[500,1500,2500,4500], 'rows_y':[600,1200,1800,2400],
           'contours':[{'id':'A','pts':rect(0,0,3000,3000)},{'id':'H','pts':rect(1000,1000,1000,1000)}]}
    hole = frame_engine.run(copy.deepcopy(raw)); check(hole['ok'], 'real outer/hole fixture failed')
    disjoint_request = copy.deepcopy(raw); disjoint_request['contours'].append({'id':'B','pts':rect(4000,0,2000,3000)})
    disjoint = frame_engine.run(disjoint_request); check(disjoint['ok'], 'real disjoint fixture failed')
    clamps_guard_request = copy.deepcopy(raw)
    clamps_guard_request.update(parts='clamps', solution_selection=example_selection(),
                               rails_fixed=[{'x':500,'y0':0,'y1':3000},{'x':2500,'y0':0,'y1':3000}])
    clamps_guard_response = frame_engine.run(clamps_guard_request)
    check(clamps_guard_response['ok'], 'real clamps outer/hole guard fixture failed')
    native = run('--contracts','contracts',{'selection':example_selection(),'hole_per_zone':hole['per_zone'],
                 'disjoint_per_zone':disjoint['per_zone'],'clamps_guard_response':clamps_guard_response})
    check(native['status'] == 'PASS', 'native contracts failed')
    subprocess.run(command(executable) + ['--catalog',str(out/'catalog_csharp.json')],check=True)
    check(json.loads((out/'catalog_csharp.json').read_text()) == json.loads((ROOT/'Common/catalogs/vector1_2015_type1_historical.json').read_text()), 'C# canonical snapshot mismatch')
    settings = {'steps':'manual','mode':'frame','sub_type':'vertical','cladding':'porcelain','profile':'ГП-60-40','offset':777,'solution_selection':example_selection()}
    packet = run('--settings','settings',settings); check(packet['ok'], str(packet))
    request = copy.deepcopy(raw); request.update(packet['engine_params'])
    response = frame_engine.run(request); check(response['ok'], 'actual C# request refused by real engine: '+str(response))
    consumed = run('--consume','pipeline',{'settings':packet['settings'],'request':request,'response':response})
    check(consumed['ok'], 'actual engine report refused by C# adapter: '+str(consumed))
    report = consumed['report']
    check(report['parameters']['solution_selection'] == example_selection(), 'quantity metadata lost exact choice')
    check(report['engine_summary']['solution_report']['assembly_compatibility'] == 'not_verified', 'assembly boundary lost')
    check('selection' not in report['engine_summary']['solution_report'], 'selection duplicated in summary')
    check(all(e['product_id'] is None for e in report['elements']), 'declaration became physical SKU')
    check(not packet['last'].get('solution_selection'), 'choice leaked into global profile')
    check(report['parameters']['solution_selection']['geometry']['cladding_front_offset_mm'] != settings['offset'], 'offset bases conflated')
    cases = []
    for name in ('missing','selection','source','approval','limitations','status'):
        bad = copy.deepcopy(response)
        if name == 'missing': bad.pop('solution_report')
        elif name == 'selection': bad['solution_report']['selection']['bracket']['L_mm'] += 10
        elif name == 'source': bad['solution_report']['selection']['source_sha256']='a'*64
        elif name == 'approval': bad['solution_report']['assembly_compatibility']='verified'
        elif name == 'limitations': bad['solution_report']['limitations']=['anything']
        elif name == 'status': bad['solution_report']['status']='retained_identity_only'
        check(not run('--consume','bad_echo_'+name,{'settings':packet['settings'],'request':request,'response':bad})['ok'],'bad engine echo accepted: '+name)
        cases.append(name)
    clamps_settings = copy.deepcopy(settings); clamps_settings.update(mode='clamps',steps='calc')
    clamps_packet = run('--settings','clamps_settings',clamps_settings); check(clamps_packet['ok'],'unchanged clamps identity payload refused')
    clamps_request=copy.deepcopy(request);clamps_request.update(clamps_packet['engine_params']);clamps_request['rails_fixed']=[{'x':500,'y0':0,'y1':3000}]
    clamps_response=frame_engine.run(clamps_request);check(clamps_response['ok'],'real clamps engine fixture')
    check(run('--consume','clamps_pipeline',{'settings':clamps_packet['settings'],'request':clamps_request,'response':clamps_response})['ok'],'retained identity echo refused')
    files=[Path(__file__).resolve(),Path(__file__).with_name('SettingsContractProbe.cs')]
    files += [ROOT/'AFrame/src/AFramePlugin'/n for n in ('FrameSettings.cs','FrameSolutionSelection.cs','FrameProjectParameters.cs','FrameCommand.cs','FrameQuantities.cs')]
    files += [ROOT/'Common/FacadeQuantities.cs',ROOT/'Common/catalogs/vector1_2015_type1_historical.json'] + list((ROOT/'AFrame/engine').glob('*.py'))
    manifest={'status':'PASS','checks':checks+native['checks'],'native_checks':native['checks'],'pipeline_checks':checks,
              'live_autocad_checked':False,'performance':native['performance'],
              'equality_diagnostic_fresh_process':json.loads(equality_diagnostic.read_text(encoding='utf-8')),
              'equality_diagnostic_contract_loop':native['equality_diagnostic'],
              'scope':'Actual native settings, selected-owner metadata/freshness guard, real engine and actual quantity producer; CAD objects doubled',
              'source_sha256':{str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}}
    (out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('Solution settings/engine/producer checks:',manifest['checks'],'PASS')
    return 0


if __name__=='__main__':raise SystemExit(main())
