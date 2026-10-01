"""Execute the actual project-parameter persistence adapter with small CAD doubles.

The transaction model is explicit and in-memory. No AutoCAD, COPY remapping,
Undo, Save/reopen, document-locking or Windows runtime acceptance is claimed.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/quantities_3009'))
from probe_runtime import available, command, compile_probe


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def quantity_sources(local):
    production = [ROOT / 'Common' / name for name in (
        'FacadeProjectParameterStore.cs', 'GeometryFingerprint.cs', 'ZoneGeometryGuard.cs',
        'LayoutGeometryGuard.cs', 'FacadeQuantities.cs', 'FacadeQuantityStore.cs',
        'FacadeQuantityStore.Frame.cs', 'ManualQuantities.cs', 'ManualQuantityGeometry.cs',
        'FacadeQuantityStore.Manual.cs')]
    helpers = [ROOT / 'tools/quantities_3009/QuantityStoreCheck.cs',
               ROOT / 'tools/quantities_3009/QuantityCadDoubles.cs',
               ROOT / 'tools/fixes_3009/test_zone_geometry_adapter.py',
               ROOT / 'Facades/engine/facade_zones.py', ROOT / 'Facades/engine/facades_engine.py',
               local / 'native_quantity_probe.cs', local / 'native_legacy_source_metadata.cs']
    return production, helpers


def run_quantity_contract(out, local, production):
    """Compile actual store plus real zone fixtures and a watched-record counter."""
    out.mkdir(parents=True, exist_ok=True)
    original_doubles = (ROOT / 'tools/quantities_3009/QuantityCadDoubles.cs').read_text(encoding='utf-8')
    counters = 'internal static class CadCounters\n{'
    assert original_doubles.count(counters) == 1
    adapted = original_doubles.replace(counters, counters + '\n    public static Autodesk.AutoCAD.DatabaseServices.ObjectId WatchedXrecord;\n    public static int WatchedXrecordReads;')
    adapted = adapted.replace('public static void Reset() {', 'public static void Reset() {\n        WatchedXrecordReads = 0;')
    getter = 'get { CadCounters.XrecordReads++; AfterRead(); return data; }'
    assert adapted.count(getter) == 1
    adapted = adapted.replace(getter, 'get { CadCounters.XrecordReads++; AfterRead(); if (ObjectId == CadCounters.WatchedXrecord) CadCounters.WatchedXrecordReads++; return data; }')
    doubles = out / 'QuantityCadDoublesWatched.cs'; doubles.write_text(adapted, encoding='utf-8')
    helper_text = (ROOT / 'tools/quantities_3009/QuantityStoreCheck.cs').read_text(encoding='utf-8')
    fixture_source = helper_text[:helper_text.index('    private static void Shift(')] + '\n}\n'
    helper = out / 'ExistingZoneFixture.cs'; helper.write_text(fixture_source, encoding='utf-8')
    fixture_builder = ROOT / 'tools/fixes_3009/test_zone_geometry_adapter.py'
    spec = importlib.util.spec_from_file_location('project_store_zone_fixtures', fixture_builder)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    fixtures = out / 'fixtures.json'
    fixtures.write_text(json.dumps(module.make_fixtures(), ensure_ascii=False), encoding='utf-8')
    executable = out / 'ProjectQuantityProbe.exe'
    compiled = compile_probe(production + [doubles, helper, local / 'native_quantity_probe.cs',
                            local / 'native_legacy_source_metadata.cs'], ['System.Web.Extensions'], executable)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return {'status': 'FAIL', 'reason': 'Quantity integration compilation failed.'}
    report = out / 'cases.json'
    if report.exists(): report.unlink()
    run = subprocess.run(command(executable) + [str(fixtures), str(report)], capture_output=True, text=True)
    (out / 'run.log').write_text(run.stdout + run.stderr, encoding='utf-8')
    print(run.stdout + run.stderr, end='')
    result = json.loads(report.read_text(encoding='utf-8')) if report.exists() else {'status': 'FAIL', 'reason': 'Native report missing.'}
    if run.returncode: result['status'] = 'FAIL'
    result['fixture_sha256'] = sha(fixtures)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix='project_store_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    local = Path(__file__).resolve().parent
    sources = [ROOT / 'Common/FacadeProjectParameterStore.cs',
               local / 'native_store_cad.cs', local / 'native_store_probe.cs']
    quantity_production, quantity_helpers = quantity_sources(local)
    tracked = list(dict.fromkeys(sources + quantity_production + quantity_helpers +
                   [Path(__file__).resolve(), ROOT / 'tools/quantities_3009/probe_runtime.py']))
    manifest = {'status': 'BLOCKED', 'live_autocad_checked': False,
                'transaction_rollback': 'explicit_in_memory_model_only',
                'source_sha256': {str(p.relative_to(ROOT)): sha(p) for p in tracked if p.is_file()}}

    def finish(code):
        (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print('Project parameter store:', manifest['status'])
        return code

    missing = [str(p.relative_to(ROOT)) for p in tracked if not p.is_file()]
    if missing or not available():
        manifest['reason'] = {'missing_sources': missing, 'native_runtime_available': available()}
        return finish(2)
    executable = out / 'ProjectStoreProbe.exe'
    compiled = compile_probe(sources, ['System.Web.Extensions'], executable)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        manifest['status'] = 'FAIL'; manifest['reason'] = 'Native compilation failed.'
        print(compiled.stdout + compiled.stderr)
        return finish(1)
    report = out / 'cases.json'
    if report.exists(): report.unlink()
    run = subprocess.run(command(executable) + [str(report)], capture_output=True, text=True)
    (out / 'run.log').write_text(run.stdout + run.stderr, encoding='utf-8')
    print(run.stdout + run.stderr, end='')
    if report.exists(): manifest['result'] = json.loads(report.read_text(encoding='utf-8'))
    if run.returncode == 0:
        manifest['quantity_result'] = run_quantity_contract(out / 'quantities', local, quantity_production)
    manifest['sources_unchanged_during_run'] = all(sha(ROOT / p) == value for p, value in manifest['source_sha256'].items())
    manifest['status'] = 'PASS' if (run.returncode == 0 and manifest.get('result', {}).get('checks', 0) > 0
        and manifest['result']['status'] == 'PASS' and manifest.get('quantity_result', {}).get('status') == 'PASS'
        and manifest['sources_unchanged_during_run']) else 'FAIL'
    manifest['checks'] = manifest.get('result', {}).get('checks', 0) + manifest.get('quantity_result', {}).get('checks', 0)
    return finish(0 if manifest['status'] == 'PASS' else 1)


if __name__ == '__main__': raise SystemExit(main())
