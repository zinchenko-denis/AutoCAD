#!/usr/bin/env python3
"""Replay real historical guards/fingerprint against one fixed test/input revision.

No checkout/reset, no DWG input, no native AutoCAD. Historical failures are
reported as failures; exit 0 means the known differential was reproduced.
Requires git, Python, mcs and mono. --out must not exist (no stale evidence).
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--source-sha', default='HEAD', help='Fixed harness/dependency revision')
    args = parser.parse_args()
    repo, out = args.repo.resolve(), args.out.resolve()
    if out.exists():
        parser.error('--out must not exist')
    if not all(shutil.which(x) for x in ('git', 'mcs', 'mono')):
        parser.error('git, mcs and mono are required; no checks executed')
    def git(*cmd):
        return subprocess.check_output(['git', '-C', str(repo), *cmd])
    baseline_sha = git('rev-parse', args.source_sha + '^{commit}').decode().strip()
    old_shas = {r: git('rev-parse', r + '^{commit}').decode().strip() for r in
                ('2f838e8', 'e28aeb7', 'build-104', 'build-105', 'build-106', 'build-107', 'build-108', 'build-109', 'build-110')}
    common = ['GeometryFingerprint.cs', 'ZoneGeometryGuard.cs', 'LayoutGeometryGuard.cs',
              'FacadeQuantities.cs', 'FacadeQuantityStore.cs', 'FacadeQuantityStore.Frame.cs',
              'FacadeProjectParameterStore.cs', 'ManualQuantities.cs', 'ManualQuantityGeometry.cs',
              'FacadeQuantityStore.Manual.cs']
    helpers = ['tools/fixes_3009/test_zone_geometry_adapter.py',
               'tools/fixes_3009/test_zone_geometry_adapter.cs',
               'tools/fixes_3009/test_zone_geometry_cad_doubles.cs',
               'tools/quantities_3009/test_quantity_store.py',
               'tools/quantities_3009/QuantityStoreCheck.cs', 'tools/quantities_3009/QuantityCadDoubles.cs',
               'tools/quantities_3009/fixtures/cladding_v1_6222041_xrecords.json',
               'Facades/engine/facades_engine.py', 'Facades/engine/facade_zones.py']
    files = ['Common/' + p for p in common] + helpers
    out.mkdir(parents=True)
    baseline = out / 'fixed-input'
    def put(root, rev, name):
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(git('show', rev + ':' + name))
    for name in files:
        put(baseline, baseline_sha, name)
    def method(source):
        start = source.index('        private static string Fingerprint(')
        end = source.index('{', start) + 1
        depth = 1
        while depth:
            depth += (source[end] == '{') - (source[end] == '}')
            end += 1
        return source[start:end]
    report = {'scope': 'Historical production C# with fixed current harness/CAD doubles; AutoCAD NOT_RUN.',
              'baseline_sha': baseline_sha, 'revisions': old_shas, 'cases': []}
    historical_fingerprints = {rev: hashlib.sha256(method(git('show', old_shas[rev] + ':Common/FacadeQuantityStore.cs').decode()).encode()).hexdigest()
                               for rev in ('e28aeb7', 'build-104', 'build-105', 'build-106', 'build-107')}
    report['unchanged_historical_fingerprint_methods'] = historical_fingerprints
    expected = len(set(historical_fingerprints.values())) == 1
    expected_zone_failures = {'fresh_fractional_coordinates_hatch_mark_sidecar', 'fresh_fractional_merged_coordinates_hatch_mark_sidecar',
                              'fresh_fractional_curved_coordinates_hatch_mark_sidecar', 'fractional_snapshot_repeat_read_in_new_transaction_and_culture',
                              'fractional_source_and_hatch_real_edit_still_rejected', 'fractional_snapshot_hash_corruption_still_rejected'}
    expected_hatch_failures = {'ATTILE_block_rectangle_hatch_edges_roundtrip_and_edit', 'ATTILE_block_full_circle_hatch_roundtrip_and_radius',
                               'ATTILE_block_arc_reference_vector_and_orientation_checked', 'ATTILE_block_elliptical_hatch_roundtrip_and_axis',
                               'ATTILE_block_spline_hatch_exact_control_points_knots_weights', 'ATTILE_many_tiles_share_one_hatch_definition_capture'}
    variants = [('zone', r) for r in ['2f838e8', 'build-104', 'build-105', 'build-106', 'build-107', 'build-108', 'build-109', 'build-110', 'baseline']]
    variants += [('hatch-store', r) for r in ['build-105', 'build-107', 'build-108', 'baseline']]
    variants += [('hatch-method', 'build-104')]
    inventories, fixture_hashes = {}, {}
    for kind, rev in variants:
        label = kind + '-' + rev
        root = out / label / 'src'
        shutil.copytree(baseline, root)
        sha = baseline_sha if rev == 'baseline' else old_shas[rev]
        item = {'label': label, 'historical_sha': sha}
        if kind == 'zone':
            for name in common[:3]:
                put(root, sha, 'Common/' + name)
            script = 'tools/fixes_3009/test_zone_geometry_adapter.py'
            item['scope'] = 'All three historical guards; fixed engine, harness and doubles.'
        else:
            script = 'tools/quantities_3009/test_quantity_store.py'
            path = root / 'Common/FacadeQuantityStore.cs'
            historical = git('show', sha + ':Common/FacadeQuantityStore.cs').decode()
            if kind == 'hatch-store':
                path.write_text(historical)
                item['scope'] = 'Whole historical store; other dependencies fixed. Not a historical bundle run.'
            else:
                current = path.read_text()
                exact = method(historical)
                path.write_text(current.replace(method(current), exact))
                assert method(path.read_text()) == exact
                item['historical_method_sha256'] = hashlib.sha256(exact.encode()).hexdigest()
                item['scope'] = 'Verbatim historical Fingerprint only in fixed complete dependency graph.'
        logs = out / label / 'evidence'
        run = subprocess.run([sys.executable, str(root / script), '--out', str(logs)], capture_output=True, text=True)
        (out / label / 'stdout.log').write_text(run.stdout + run.stderr)
        manifest_path = logs / 'manifest.json'
        manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {}
        result = manifest.get('result', {})
        cases = result.get('cases', [])
        names = [c.get('name') for c in cases]
        failures = [c.get('name') for c in cases if c.get('status') == 'FAIL']
        passed = sum(c.get('status') == 'PASS' for c in cases)
        group = 'zone' if kind == 'zone' else 'hatch'
        required_total = 36 if group == 'zone' else 70
        valid_cases = (len(cases) == required_total and passed + len(failures) == required_total and
                       all(isinstance(n, str) and n for n in names) and len(set(names)) == required_total)
        counts_match = (result.get('total') == len(cases) and result.get('passed') == passed and
                        result.get('failed') == len(failures))
        inventory = sorted(names) if valid_cases else []
        inventory_match = valid_cases and inventory == inventories.setdefault(group, inventory)
        fixture_path = logs / 'fixtures.json'
        fixture_hash = hashlib.sha256(fixture_path.read_bytes()).hexdigest() if fixture_path.is_file() else None
        fixtures_match = (fixture_hash is not None and fixture_hash == manifest.get('fixtures_sha256') and
                          fixture_hash == fixture_hashes.setdefault(group, fixture_hash))
        item.update(returncode=run.returncode, total=len(cases), passed=passed, failed=len(failures),
                    failures=failures, manifest=str(manifest_path.relative_to(out)),
                    counts_match_report=counts_match, case_inventory_matches=inventory_match,
                    fixtures_sha256=fixture_hash, fixtures_match=fixtures_match)
        # Known old defect: 6 failures; repaired baseline and 108+ must succeed.
        repaired = rev in ('baseline', 'build-108', 'build-109', 'build-110')
        outcome_match = (run.returncode == 0 and not failures) if repaired else (
            run.returncode == 1 and len(failures) == 6 and
            set(failures) == (expected_zone_failures if group == 'zone' else expected_hatch_failures))
        match = valid_cases and counts_match and inventory_match and fixtures_match and outcome_match
        item['expected_differential_observed'] = match
        expected &= match
        report['cases'].append(item)
        print(label, f"{item['passed']}/{item['total']}", 'observed expected differential' if match else 'UNEXPECTED')
    report['case_inventories'] = inventories
    report['fixture_hashes'] = fixture_hashes
    report['expected_differential_observed'] = expected
    (out / 'summary.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
    print('Historical failures above remain FAIL. Full AutoCAD scenario: NOT_RUN.')
    return 0 if expected else 1


if __name__ == '__main__':
    raise SystemExit(main())
