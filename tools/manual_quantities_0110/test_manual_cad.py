"""Execute actual manual geometry/mapper/Store with instrumented CAD doubles.

Synthetic library structures contain no private geometry. Facade-zone fixtures
come from the actual zone engine. No native transaction/COPY/Undo/Save guarantee
is simulated. Missing compiler/runtime or source files return BLOCKED.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path)
    parser.add_argument('--exe', type=Path)
    parser.add_argument('--performance', action='store_true', help='Actual CAD operation bounds for 10/100/1000 manual and generated objects')
    parser.add_argument('--large', action='store_true', help='Also measure 10000 manual objects; time/memory are observational')
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix='manual_cad_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    local = Path(__file__).resolve().parent
    sources = [ROOT / 'Common' / (name + '.cs') for name in (
        'GeometryFingerprint', 'ZoneGeometryGuard', 'LayoutGeometryGuard', 'FacadeQuantities',
        'FacadeQuantityStore', 'FacadeQuantityStore.Frame', 'FacadeProjectParameterStore', 'ManualQuantities',
        'ManualQuantityGeometry', 'FacadeQuantityStore.Manual')]
    sources += [ROOT / 'tools/quantities_3009' / name for name in ('QuantityStoreCheck.cs', 'QuantityCadDoubles.cs')]
    sources += [local / 'ManualCadCheck.cs']
    fixture_builder = ROOT / 'tools/fixes_3009/test_zone_geometry_adapter.py'
    tracked = sources + [Path(__file__).resolve(), local / 'ManualCadCheck.csproj', fixture_builder,
                         ROOT / 'Facades/engine/facades_engine.py', ROOT / 'Facades/engine/facade_zones.py']
    report = {'status': 'BLOCKED', 'scope': __doc__, 'source_sha256': {
        str(p.relative_to(ROOT)): sha(p) for p in tracked if p.is_file()}}

    def finish(code):
        (out / 'manifest.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print('Manual CAD:', report['status'], 'report:', out / 'manifest.json')
        return code

    if not all(p.is_file() for p in sources):
        report['reason'] = 'Required production/probe files unavailable; no execution claimed.'
        return finish(2)
    spec = importlib.util.spec_from_file_location('manual_zone_fixtures', fixture_builder)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    fixture_file = out / 'fixtures.json'
    fixture_file.write_text(json.dumps(module.make_fixtures(), ensure_ascii=False, indent=2), encoding='utf-8')
    report['fixtures_sha256'] = sha(fixture_file)
    if args.exe:
        executable = args.exe.resolve()
        if not executable.is_file():
            report['reason'] = 'Missing precompiled executable.'
            return finish(2)
        report['precompiled_executable_sha256'] = sha(executable)
    else:
        if not shutil.which('mcs') or not shutil.which('mono'):
            report['reason'] = 'Missing Mono/mcs; Windows CI uses the explicit net48 project and --exe.'
            return finish(2)
        executable = out / 'ManualCadCheck.exe'
        compiled = subprocess.run(['mcs', '-nologo', '-r:System.Web.Extensions.dll', '-main:ManualCadCheck',
                                   '-out:' + str(executable)] + [str(p) for p in sources], capture_output=True, text=True)
        (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
        report['compile_returncode'] = compiled.returncode
        if compiled.returncode:
            report['status'] = 'FAIL'
            print(compiled.stdout + compiled.stderr)
            return finish(1)
    command = [str(executable)] if os.name == 'nt' else ['mono', str(executable)]
    if args.performance:
        results = []
        for count in ([10, 100, 1000, 10000] if args.large else [10, 100, 1000]):
            for mode in ('manual', 'generated', 'mixed'):
                name = f'{mode}_{count}'
                case_file = out / (name + '.json')
                case_file.unlink(missing_ok=True)
                run = subprocess.run(command + ['--fixtures', str(fixture_file), '--report', str(case_file),
                    '--performance', str(count), mode], capture_output=True, text=True)
                (out / (name + '.log')).write_text(run.stdout + run.stderr, encoding='utf-8')
                result = json.loads(case_file.read_text(encoding='utf-8')) if case_file.is_file() else {'status': 'FAIL'}
                results.append({'name': name, 'returncode': run.returncode, 'result': result})
                print(run.stdout + run.stderr, end='', flush=True)
        failures = sum(c['returncode'] != 0 or c['result'].get('status') != 'PASS' for c in results)
        report['result'] = {'total': len(results), 'failed': failures, 'cases': results}
        report['run_returncode'] = int(failures > 0)
    else:
        case_file = out / 'cases.json'
        case_file.unlink(missing_ok=True)
        run = subprocess.run(command + ['--fixtures', str(fixture_file), '--report', str(case_file)], capture_output=True, text=True)
        (out / 'run.log').write_text(run.stdout + run.stderr, encoding='utf-8')
        print(run.stdout + run.stderr, end='')
        report['run_returncode'] = run.returncode
        if case_file.is_file():
            report['result'] = json.loads(case_file.read_text(encoding='utf-8'))
    report['sources_unchanged_during_run'] = all(sha(ROOT / path) == value for path, value in report['source_sha256'].items())
    report['status'] = 'PASS' if report['run_returncode'] == 0 and report['sources_unchanged_during_run'] and report.get('result', {}).get('total', 0) > 0 else 'FAIL'
    return finish(0 if report['status'] == 'PASS' else 1)


if __name__ == '__main__':
    sys.exit(main())
