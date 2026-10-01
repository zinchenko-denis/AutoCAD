"""Bounded final managed P5 path: actual node and table commands share one DB.

The old ownership defect belongs to the CAD adapter, not production AutoCAD.
No product change, native rendering, transaction rollback or engineering admission.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('table_lifecycle', ROOT / 'tools/table_lifecycle_0110/run_lifecycle.py')
table = importlib.util.module_from_spec(spec)
spec.loader.exec_module(table)
pilot = table.pilot


def verify_node(out):
    oracle = json.loads((ROOT / 'docs/node_lifecycle_0110/independent_oracle.json').read_text(encoding='utf-8'))
    result = json.loads((out / 'node_command/result.json').read_text(encoding='utf-8'))
    checks = []
    def need(ok, message):
        if not ok:
            raise AssertionError(message)
        checks.append(message)
    cases = {case['name']: case for case in result['cases']}
    n1 = cases['create_n1']['node']; n2 = cases['create_n2']['node']
    for name in ('n1_initial_fresh', 'n2_fresh_at_issue', 'n2_fresh_before_table_late_mutation'):
        need(cases[name]['fresh'] is True, 'independent fresh time boundary: ' + name)
    for name in ('n1_after_project_change', 'n1_stale_after_n2', 'n1_after_table_project_revision3', 'n2_after_table_project_revision3'):
        need(cases[name]['fresh'] is False, 'independent stale time boundary: ' + name)
    need(n1['owner'] != n2['owner'] and n1['definition'] != n2['definition'], 'N2 owns distinct reference and definition')
    need(n1['zone'] == n2['zone'] and n1['ZoneFingerprint'] == n2['ZoneFingerprint'], 'same canonical source zone and geometry')
    need(n1['references'] == [n1['owner'], n1['definition'], n1['zone']], 'N1 soft references bind actual owner/definition/zone')
    need(n2['references'] == [n2['owner'], n2['definition'], n2['zone']], 'N2 soft references bind actual owner/definition/zone')
    for name, case in cases.items():
        need(case['counters']['modelspace_visits'] == oracle['performance']['node_command_modelspace_visits'], 'zero ModelSpace visits inside actual command: ' + name)
        if name.startswith('n1_') or name.endswith('_n1_stale'):
            need(case['node'] == n1, 'N1 exact owner/definition/raw record/body survives transition: ' + name)
    for node, front in ((n1, 'front_initial_mm'), (n2, 'front_project_changed_mm')):
        selection = node['snapshot']['selection']
        need(selection['geometry']['cladding_front_offset_mm'] == oracle['node_arithmetic'][front], 'saved node keeps its actual project declaration ' + front)
        need(selection['geometry']['insulation_layers_mm'] == oracle['node_arithmetic']['insulation_layers_mm'], 'same actual insulation layers')
        need(node['geometry']['gap_mm'] == oracle['node_arithmetic']['local_clearance_mm'] and node['geometry']['insulation_outer_x_mm'] == oracle['node_arithmetic']['outer_surface_mm'], 'independent insulation sum and local clearance')
        need(all(p['owner'] == node['definition'] for p in node['body']), 'body primitives owned by definition')
    for name in ('create_n1', 'create_n2'):
        need(cases[name]['mounting_status'] == oracle['engineering']['mounting_status'] and cases[name]['automatic_bracket_selection_allowed'] is False, 'software success leaves engineering admission closed: ' + name)
    need(result['final_project_revision'] == 3 and result['final_n2_fresh'] is False, 'later table mutation remains committed and N2 stale')
    for tag in ('initial', 'project_reissued', 'final'):
        quantities = json.loads((out / ('node_command/quantities_' + tag + '.json')).read_text(encoding='utf-8'))
        need({item['handle'] for item in quantities['frame_unaccounted']} == set(quantities['nodes']), 'explicit frame node selection visibly excluded: ' + tag)
        need({item['handle'] for item in quantities['cladding_unaccounted']} == set(quantities['nodes']), 'explicit cladding node selection visibly excluded: ' + tag)
    pilot.save(out / 'node_command/oracle_review.json', {'status': 'PASS', 'checks': len(checks), 'assertions': checks})
    return result, len(checks)


def ownership(build, source):
    exe = build / 'OwnershipProbe.exe'
    compiled = pilot.compile_probe([source, Path(__file__).with_name('OwnershipProbe.cs')], [], exe)
    if compiled.returncode:
        raise RuntimeError(compiled.stdout + compiled.stderr)
    done = subprocess.run(pilot.command(exe), check=True, capture_output=True, text=True, timeout=15)
    return json.loads(done.stdout)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--out', type=Path, required=True)
    ap.add_argument('--ownership-source', type=Path, help='Only replay adapter ownership with an exact saved source; no repository/history dependency')
    ap.add_argument('--expect-ownership', choices=['legacy', 'fixed'], default='fixed')
    args = ap.parse_args(); out = args.out.resolve(); out.mkdir(parents=True, exist_ok=True); build = out / 'build'; build.mkdir(exist_ok=True)
    cad = ROOT / 'tools/quantities_3009/QuantityCadDoubles.cs'
    observed = ownership(build, args.ownership_source or cad)
    expected = args.expect_ownership == 'legacy'
    if observed != {'model_count': 2 if expected else 1, 'definition_count': 1, 'internal_primitive_leaked': expected, 'reference_membership_count': 1, 'lazy_internal_primitive_leaked': expected}:
        raise AssertionError('Adapter ownership expectation not met: ' + str(observed))
    pilot.save(out / 'ownership.json', {'status': 'LEGACY_ADAPTER_GAP_REPRODUCED' if expected else 'PASS', 'source_sha256': pilot.digest(args.ownership_source or cad), 'observed': observed})
    if args.ownership_source:
        print('Ownership adapter:', 'LEGACY_GAP_REPRODUCED' if expected else 'PASS'); return 0
    original, generated = table.helper_extraction(build)
    extras = [ROOT / 'tools/table_lifecycle_0110/TableCommandDoubles.cs', ROOT / 'tools/table_lifecycle_0110/TableLifecycleProbe.cs',
              ROOT / 'Facades/src/AFacadesPlugin/FacadeQuantityTableCommand.cs',
              Path(__file__).with_name('NodeLifecycleDoubles.cs'), Path(__file__).with_name('NodeLifecycleProbe.cs')]
    extras += [ROOT / 'AFrame/src/AFramePlugin' / (name + '.cs') for name in ('FrameNodeCommand', 'FrameNodeRenderer', 'FrameNodeStore')]
    tick = time.perf_counter(); exe, sources, extracted = pilot.compile_native(build, extra_sources=extras, extra_generated=[generated], extra_references=['System.Drawing']); compilation = time.perf_counter() - tick
    tracked = sources + [original, Path(__file__).resolve(), Path(__file__).with_name('OwnershipProbe.cs'), ROOT / 'tools/table_lifecycle_0110/run_lifecycle.py',
                         ROOT / 'tools/limited_pilot_0110/run_acceptance.py', ROOT / 'tools/quantities_3009/probe_runtime.py']
    tracked += [ROOT / 'docs' / name / 'independent_oracle.json' for name in ('limited_pilot_0110', 'table_lifecycle_0110', 'node_lifecycle_0110')]
    for directory in ('AFrame/engine', 'AClad/engine', 'Facades/engine'):
        tracked.extend((ROOT / directory).glob('*.py')); tracked.extend((ROOT / directory).glob('*.json'))
    hashes = {str(p.relative_to(ROOT)): pilot.digest(p) for p in sorted(set(tracked))}
    selection = pilot.example_selection(); selection['geometry'].update(cladding_front_offset_mm=230, insulation_layers_mm=[100, 50])
    pilot.save(out / 'selection.json', selection); pilot.save(out / 'table_expect.json', {'expect': 'fixed'})
    tick = time.perf_counter(); observations = pilot.execute(exe, out / 'selection.json', out); duration = time.perf_counter() - tick
    pilot.verify(out, observations); pilot.verify_trace(out)
    tables, table_python = table.verify_tables(out); node, node_python = verify_node(out)
    native = json.loads((out / 'native_result.json').read_text(encoding='utf-8'))
    pilot.need(all(pilot.digest(ROOT / name) == digest for name, digest in hashes.items()), 'all combined lifecycle inputs remain unchanged during run')
    manifest = {'schema': 'node_table_lifecycle/1', 'status': 'PASS', 'product_defect_found': False,
                'plan_item': 'limited P5 node/table integration; full host P5 NOT_RUN', 'engineering_assessment': 'BLOCKED', 'live_autocad_checked': False,
                'checks': native['checks'] + len(pilot.CHECKS) + tables['checks'] + table_python + node['checks'] + node_python,
                'base_native_checks': native['checks'], 'base_python_checks': len(pilot.CHECKS), 'table_native_checks': tables['checks'], 'table_python_checks': table_python,
                'node_native_checks': node['checks'], 'node_python_checks': node_python,
                'engine_invocations': len(observations), 'node_additional_engine_invocations': 0,
                'base_xlsx_files': 9, 'command_xlsx_files': 10, 'source_sha256': hashes,
                'generated_extraction_sha256': {str(p.relative_to(out)): pilot.digest(p) for p in extracted},
                'artifact_sha256': {str(p.relative_to(out)): pilot.digest(p) for p in sorted(out.rglob('*')) if p.is_file() and build not in p.parents and p.suffix in ('.json', '.xlsx') and p.name != 'manifest.json'},
                'timings': {'compile_seconds': compilation, 'persistent_pipeline_seconds': duration, 'scope': 'managed adapters only; not AutoCAD performance'},
                'limits': ['native_result.node_scope describes only inherited pure segment; actual full Node command execution is node_command/result.json',
                           'one persistent Database contains actual node refs, actual tables and actual engine-produced physical reports',
                           'scripted prompts/form and property-level renderer model; no native visual equivalence',
                           'no-op transactions: only cancellations/refusals before writes, no CAD rollback/Undo claim',
                           'final table late_project deliberately commits revision3; both nodes then stale and exact original N1 preserved',
                           'DWG save/open/COPY/SaveAs/native Table rendering/host performance NOT_RUN']}
    pilot.save(out / 'manifest.json', manifest)
    print('Combined node/table lifecycle:', manifest['checks'], 'PASS; production unchanged; engineering BLOCKED; native host NOT_RUN')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
