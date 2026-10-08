"""Run the mandatory facade gates and record the exact tested source hashes.

No releases, network writes, installation or AutoCAD host are involved.
Mono/mcs must already be available for the native regression runner.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]


def source_hashes():
    """Hash product code and the actual test harness before and after execution.

    Include new, non-ignored source files too: an uncommitted regression still
    participates in the run and must not disappear from its evidence.
    """
    names = subprocess.check_output(
        ['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard'],
        cwd=ROOT).decode('utf-8').split('\0')
    suffixes = {'.py', '.cs', '.csproj', '.json', '.lsp', '.yml', '.yaml',
                '.ps1', '.scr'}
    return {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest()
            for name in sorted(set(names))
            if name and Path(name).suffix.lower() in suffixes
            and (ROOT / name).is_file()}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--out', type=Path)
    args = ap.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix='atr_checks_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, PYTHONUTF8='1', PYTHONIOENCODING='utf-8',
               PYTHONDONTWRITEBYTECODE='1')
    base_commit = subprocess.check_output(
        ['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    hashes_before = source_hashes()
    jobs = []
    for module, scripts in (
        ('Facades', ['test_facade_zones.py', 'test_facades_engine.py']),
        ('AClad', ['test_cladding_plan.py', 'test_clad_engine.py',
                   'test_tile_pattern.py', 'audit_clad.py']),
        ('AFrame', ['test_frame_plan.py', 'test_frame_calc.py',
                    'test_frame_beam.py', 'test_frame_cutting.py',
                    'test_frame_window_ends.py',
                    'test_frame_engine.py', 'test_frame_rules.py',
                    'test_frame_topology.py', 'test_frame_topology_index.py',
                    'test_frame_connections.py', 'test_frame_solution_selection.py',
                    'audit_frame.py']),
    ):
        jobs.extend((module + '_' + Path(s).stem, module + '/engine', [s])
                    for s in scripts)
    jobs.extend([
        ('bundle_versions', '.', ['tools/facades/test_bundle_versions.py']),
        ('facade_startup', '.', ['tools/facades/test_startup.py']),
        ('zone_migration', '.', ['Facades/tests/test_zone_migration.py']),
        ('frame_typical_facades', '.', ['AFrame/engine/test_frame_typical_facades.py']),
        ('frame_local_issues', '.', ['AFrame/tests/test_local_issues.py',
                                    '--out', str(out / 'frame_local_issues')]),
        ('dynamic_block_size', '.', ['AClad/tests/test_dynamic_block_size.py',
                                    '--out', str(out / 'dynamic_block_size')]),
        ('node_library', '.', ['AFrame/tests/test_node_library.py',
                              '--out', str(out / 'node_library')]),
        ('node_library_author', '.', ['tools/node_library/test_author.py']),
        ('native_outline_oracle', '.', ['tools/native_autocad/test_outline_oracle.py',
                                       '--out', str(out / 'native_outline_oracle')]),
        ('stamp_inspector', '.', ['tools/stamp_library/test_inspect.py']),
        ('xmod', '.', ['tools/xmod_check.py', '--no-fixture', '--allow=X6']),
        ('frame_quick', '.', ['AFrame/tools/frame_synth.py', '--quick']),
        ('zones_quick', '.', ['Facades/tools/zones_synth.py', '--quick']),
        ('tile_quick', '.', ['AClad/tools/attile_synth.py', '--quick']),
        ('roles_quick', '.', ['tools/roles_synth.py', '--quick']),
        ('engine_contract', '.', ['tools/engine_contract.py']),
        ('facade_regressions', '.', ['tools/fixes_3009/run_facade_regressions.py',
                                    '--out', str(out / 'regressions')]),
        ('atr_topology', '.', ['tools/atr_review_3009/vector1_topology.py']),
        ('atr_vector5', '.', ['tools/atr_review_3009/vector5_contract_probe.py',
                             '--out', str(out / 'vector5')]),
        ('atr_architecture', '.', ['tools/atr_review_3009/architecture_probe.py',
                                  '--out', str(out / 'architecture')]),
        ('atr_mass', '.', ['tools/atr_review_3009/vector5_mass_review.py',
                          '--evidence', str(out / 'architecture/evidence.json'),
                          '--out', str(out / 'mass_review.json')]),
        ('static_model_gap', '.', ['tools/static_review_3009/reproduce_model_gap.py',
                                 '--out', str(out / 'static_model_gap.json')]),
        ('quantities_core', '.', ['tools/quantities_3009/test_quantities_core.py',
                                 '--out', str(out / 'quantities_core')]),
        ('quantities_producer', '.', ['tools/quantities_3009/test_cladding_producer.py',
                                     '--out', str(out / 'quantities_producer')]),
        ('quantities_table', '.', ['tools/quantities_3009/test_cladding_table.py',
                                  '--out', str(out / 'quantities_table')]),
        ('quantities_store', '.', ['tools/quantities_3009/test_quantity_store.py',
                                  '--out', str(out / 'quantities_store')]),
        ('frame_quantities_core', '.', ['tools/frame_quantities_3009/test_frame_core.py',
                                       '--out', str(out / 'frame_quantities_core')]),
        ('frame_quantities_core_perf', '.', ['tools/frame_quantities_3009/test_frame_core_perf.py', '--quick',
                                            '--out', str(out / 'frame_quantities_core_perf')]),
        ('frame_quantities_producer', '.', ['tools/frame_quantities_3009/test_frame_producer.py',
                                           '--out', str(out / 'frame_quantities_producer')]),
        ('frame_quantities_table', '.', ['tools/frame_quantities_3009/test_frame_table.py',
                                        '--out', str(out / 'frame_quantities_table')]),
        ('frame_engine_perf', '.', ['tools/frame_quantities_3009/test_frame_engine_perf.py', '--targets', '1000,3000',
                                   '--out', str(out / 'frame_engine_perf')]),
        ('quantities_store_perf', '.', ['tools/quantities_3009/test_quantity_store_perf.py', '--quick',
                                       '--out', str(out / 'quantities_store_perf')]),
        ('quantities_store_encoding', '.', ['tools/frame_quantities_3009/test_store_encoding.py',
                                           '--out', str(out / 'quantities_store_encoding')]),
        ('manual_quantities_core', '.', ['tools/manual_quantities_0110/test_manual_core.py',
                                        '--out', str(out / 'manual_quantities_core')]),
        ('manual_quantities_core_perf', '.', ['tools/manual_quantities_0110/test_manual_core_perf.py', '--quick',
                                             '--out', str(out / 'manual_quantities_core_perf')]),
        ('manual_quantities_cad', '.', ['tools/manual_quantities_0110/test_manual_cad.py',
                                       '--out', str(out / 'manual_quantities_cad')]),
        ('manual_quantities_cad_perf', '.', ['tools/manual_quantities_0110/test_manual_cad.py', '--performance',
                                            '--out', str(out / 'manual_quantities_cad_perf')]),
        ('manual_quantities_output', '.', ['tools/manual_quantities_0110/test_manual_output.py',
                                          '--out', str(out / 'manual_quantities_output')]),
        ('catalog_tutorial_examples', '.', ['docs/facades_beginner_3009/replay_examples.py',
                                           '--out', str(out / 'catalog_tutorial_examples.json')]),
        ('solution_settings_contract', '.', ['tools/solution_catalog_0110/test_settings_contract.py',
                                             '--out', str(out / 'solution_settings_contract')]),
        ('pilot_interface_contract', '.', ['tools/pilot_completion_0110/audit_interface_mapping.py',
                                          '--out', str(out / 'pilot_interface_contract')]),
        ('limited_pilot_acceptance', '.', ['tools/limited_pilot_0110/run_acceptance.py',
                                          '--out', str(out / 'limited_pilot_acceptance')]),
        ('frame_axis_cancel', '.', ['tools/limited_pilot_0110/test_axis_cancel.py',
                                   '--out', str(out / 'frame_axis_cancel')]),
        ('table_command_lifecycle', '.', ['tools/table_lifecycle_0110/run_lifecycle.py',
                                         '--out', str(out / 'table_command_lifecycle')]),
        ('pilot_node_table_lifecycle', '.', ['tools/node_lifecycle_0110/run_lifecycle.py',
                                           '--out', str(out / 'pilot_node_table_lifecycle')]),
        ('project_parameters_core', '.', ['tools/project_params_0110/test_core.py',
                                         '--out', str(out / 'project_parameters_core')]),
        ('project_parameters_store', '.', ['tools/project_params_0110/test_store_contract.py',
                                          '--out', str(out / 'project_parameters_store')]),
        ('project_parameters_integration', '.', ['tools/project_params_0110/test_integration.py',
                                                '--out', str(out / 'project_parameters_integration')]),
        ('node_geometry_core', '.', ['tools/node_geometry_0110/test_core.py',
                                    '--out', str(out / 'node_geometry_core')]),
        ('mounting_assessment_core', '.', ['tools/mounting_assessment_0110/test_core.py',
                                          '--out', str(out / 'mounting_assessment_core')]),
        ('node_geometry_cad', '.', ['tools/node_geometry_0110/test_node_cad.py',
                                   '--out', str(out / 'node_geometry_cad')]),
        ('catalog_connection_store', '.', ['tools/catalog_connections_0110/test_connection_store.py',
                                          '--out', str(out / 'catalog_connection_store')]),
        ('catalog_clamps_manual_boundary', '.', ['tools/catalog_connections_0110/test_clamps_manual_boundary.py',
                                               '--out', str(out / 'catalog_clamps_manual_boundary')]),
        ('catalog_connection_table', '.', ['tools/catalog_connections_0110/test_connection_table.py',
                                          '--out', str(out / 'catalog_connection_table'),
                                          '--report', str(out / 'catalog_connection_store/independent_zones_report.json')]),
        ('orphan_connection_legacy', '.', ['tools/orphan_connections_0110/reproduce_gap.py',
                                         '--out', str(out / 'orphan_connection_legacy'), '--expect', 'fixed']),
        ('catalog_topology_performance', '.', ['tools/catalog_connections_0110/reproduce_topology_performance.py',
                                             '--out', str(out / 'catalog_topology_performance')]),
    ])
    results = []
    for name, cwd, arguments in jobs:
        start = time.monotonic()
        run = subprocess.run([sys.executable] + arguments, cwd=ROOT / cwd,
                             env=env, text=True, capture_output=True)
        (out / (name + '.log')).write_text(run.stdout + run.stderr, encoding='utf-8')
        results.append({'name': name, 'cwd': cwd, 'arguments': arguments,
                        'returncode': run.returncode,
                        'seconds': round(time.monotonic() - start, 3)})
        print(name + ': ' + ('PASS' if run.returncode == 0 else 'FAIL'), flush=True)
        if run.returncode:
            print((run.stdout + run.stderr)[-4000:], flush=True)
    hashes_after = source_hashes()
    changed = {path: {'before': hashes_before.get(path),
                      'after': hashes_after.get(path)}
               for path in sorted(hashes_before.keys() | hashes_after.keys())
               if hashes_before.get(path) != hashes_after.get(path)}
    manifest = {'base_commit': base_commit,
                'results': results, 'source_sha256': hashes_after,
                'source_sha256_before': hashes_before,
                'source_stable': not changed, 'source_changes': changed,
                'live_autocad_checked': False, 'release_published': False,
                'known_exclusion': 'X6 belongs to unchanged ATSPEC'}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False,
                                                indent=2) + '\n', encoding='utf-8')
    if changed:
        print('SOURCE CHANGED DURING RUN: FAIL (' + str(len(changed)) +
              ' files); see source_changes in manifest.json', flush=True)
    return int(bool(changed) or any(r['returncode'] for r in results))


if __name__ == '__main__':
    raise SystemExit(main())
