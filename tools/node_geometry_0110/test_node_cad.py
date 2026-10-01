"""Actual ATFNODE lifecycle with explicit CAD/storage/prompt doubles.

The geometry algorithm, drawing builder, renderer, store and command are real.
Zone verification returns explicit fixture states; forms only provide inputs.
This does not prove native AutoCAD COPY, Undo, UCS/DUCS or Save/reopen behavior.
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
    out = (args.out or Path(tempfile.mkdtemp(prefix='node_cad_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    local = Path(__file__).resolve().parent
    src = ROOT / 'AFrame/src/AFramePlugin'
    sources = [local / x for x in ('NodeCadDoubles.cs', 'NodeCommandDoubles.cs', 'NodeCadProbe.cs')]
    sources += [ROOT / 'Common/FacadeProjectParameterStore.cs']
    sources += [src / x for x in ('FrameSolutionSelection.cs', 'FrameProjectParameters.cs', 'FrameNodeGeometry.cs', 'FrameMountingAssessment.cs',
                                  'FrameNodeDrawing.cs', 'FrameNodeRenderer.cs', 'FrameNodeStore.cs', 'FrameNodeCommand.cs')]
    missing = [str(p.relative_to(ROOT)) for p in sources if not p.is_file()]
    if missing or not available():
        print('BLOCKED:', missing or 'native runtime unavailable')
        return 2
    tracked = sources + [Path(__file__).resolve(), ROOT / 'tools/quantities_3009/probe_runtime.py']
    hashes = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in tracked}
    executable = out / 'NodeCadProbe.exe'
    compiled = compile_probe(sources, ['System.Web.Extensions', 'System.Windows.Forms', 'System.Drawing'], executable)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return 1
    fixture = out / 'selection.json'
    fixture.write_text(json.dumps(example_selection(), ensure_ascii=False, allow_nan=False), encoding='utf-8')
    cases = out / 'cases.json'
    if cases.exists(): cases.unlink()
    run = subprocess.run(command(executable) + [str(fixture), str(cases)], capture_output=True, text=True)
    (out / 'run.log').write_text(run.stdout + run.stderr, encoding='utf-8')
    print(run.stdout + run.stderr, end='')
    results = json.loads(cases.read_text(encoding='utf-8')) if cases.exists() else {}
    unchanged = all(hashlib.sha256((ROOT / p).read_bytes()).hexdigest() == value for p, value in hashes.items())
    passed = run.returncode == 0 and results.get('checks', 0) > 0 and results.get('status') == 'PASS' and unchanged
    report = {'status': 'PASS' if passed else 'FAIL', 'checks': results.get('checks', 0), 'result': results,
              'live_autocad_checked': False, 'source_sha256': hashes, 'sources_unchanged_during_run': unchanged}
    (out / 'manifest.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return 0 if passed else 1


if __name__ == '__main__': raise SystemExit(main())
