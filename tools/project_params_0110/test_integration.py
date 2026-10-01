"""Execute actual parameter-edit commands and their DTO/persistence boundary.

Host/editor/forms and geometry verification are doubled. In-memory rollback is
not AutoCAD Undo, COPY, locking, Save/reopen or native modal acceptance.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/quantities_3009'))
from probe_runtime import available, command, compile_probe
sys.path.insert(0, str(ROOT / 'AFrame/engine'))
from test_frame_solution_selection import example_selection


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix='project_commands_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    local = Path(__file__).resolve().parent
    if not available():
        print('BLOCKED: native .NET compiler/runtime unavailable')
        return 2
    # Extend only the CAD-double API surface needed by the actual public commands.
    # No product statement or control flow is copied, replaced or reimplemented.
    base = (local / 'native_store_cad.cs').read_text(encoding='utf-8')
    enum = 'public enum DxfCode { Text = 1, SoftPointerId = 330, HardPointerId = 340, Int16 = 70 }'
    manager = '        public Database() { nod = Add(new DBDictionary()).ObjectId; }'
    assert base.count(enum) == 1 and base.count(manager) == 1
    adapted = out / 'CommandCadDoubles.cs'
    adapted.write_text(base.replace(enum, enum[:-2] + ', Operator = -4, Start = 0 }').replace(
        manager, manager + '\n        public TransactionManager TransactionManager { get { return new TransactionManager(this); } }'), encoding='utf-8')
    src = ROOT / 'AFrame/src/AFramePlugin'
    sources = [adapted, local / 'command_doubles.cs', local / 'CommandIntegrationProbe.cs',
               ROOT / 'Common/FacadeProjectParameterStore.cs', src / 'FrameProjectParameters.cs',
               src / 'FrameSolutionSelection.cs', src / 'FrameSettings.cs', src / 'FrameProjectCommand.cs']
    tracked = [p for p in sources if p != adapted] + [local / 'native_store_cad.cs', Path(__file__).resolve(), src / 'FrameCommand.cs']
    # Supplemental wiring assertion; the helper's behavior is executed below.
    frame_command = (src / 'FrameCommand.cs').read_text(encoding='utf-8')
    clamp_call = 'projectOperation.ValidateClamps(solutionRoots);'
    assert frame_command.count(clamp_call) == 1
    clamp_line = next(line.strip() for line in frame_command.splitlines() if clamp_call in line)
    assert clamp_line == clamp_call, 'All clamp operations must validate project provenance, including detached zones'
    guard_start = frame_command.index('                var currentSolutionScope = new FrameSolutionSelectionScope(')
    guard_end = frame_command.index('                var bt = (BlockTable)tr.GetObject', guard_start)
    fresh_guard = out / 'ActualProjectPrewrite.cs'
    fresh_guard.write_text("""using System; using System.Collections.Generic; using System.Web.Script.Serialization; using Autodesk.AutoCAD.DatabaseServices;
namespace AFramePlugin { internal static class ActualProjectPrewrite {
internal static readonly Dictionary<ObjectId,Dictionary<string,object>> Metadata = new Dictionary<ObjectId,Dictionary<string,object>>();
private static Dictionary<string,object> ReadFrameSettings(Transaction tr,JavaScriptSerializer serializer,Entity entity){return Metadata[entity.ObjectId];}
internal static void Fresh(FrameProjectOperation projectOperation, Transaction tr, Dictionary<ObjectId,Tuple<string,bool>> solutionOwners,
HashSet<ObjectId> canonicalSolutionOwners,List<string> solutionRoots,FrameSolutionSelectionContext resolvedSolutionContext) {
var ser = new JavaScriptSerializer();
""" + frame_command[guard_start:guard_end] + '\n}}}', encoding='utf-8')
    sources.append(fresh_guard)
    hashes = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in tracked}
    executable = out / 'CommandIntegrationProbe.exe'
    compiled = compile_probe(sources, ['System.Web.Extensions', 'System.Windows.Forms'], executable)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return 1
    fixture = out / 'selection.json'
    fixture.write_text(json.dumps(example_selection(), ensure_ascii=False), encoding='utf-8')
    report = out / 'cases.json'
    if report.exists(): report.unlink()
    run = subprocess.run(command(executable) + [str(fixture), str(report)], capture_output=True, text=True)
    (out / 'run.log').write_text(run.stdout + run.stderr, encoding='utf-8')
    print(run.stdout + run.stderr, end='')
    results = json.loads(report.read_text(encoding='utf-8')) if report.exists() else {}
    unchanged = all(hashlib.sha256((ROOT / p).read_bytes()).hexdigest() == value for p, value in hashes.items())
    passed = run.returncode == 0 and results.get('checks', 0) > 0 and results.get('status') == 'PASS' and unchanged
    manifest = {'status': 'PASS' if passed else 'FAIL', 'checks': results.get('checks', 0),
                'live_autocad_checked': False, 'result': results, 'sources_unchanged_during_run': unchanged,
                'source_sha256': hashes}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return 0 if passed else 1


if __name__ == '__main__': raise SystemExit(main())
