"""Real frame_engine -> actual CAD-free producer -> actual common aggregation.

Producer methods and the read-only profile guard are extracted verbatim.
Synthetic CAD links/properties permit controlled tests, not a real DWG or host.
"""
import argparse
import copy
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile

from engine_oracles import ARRAYS, ROOT, cases, frame_engine, run_cases
sys.path.insert(0, str(ROOT / 'tools/quantities_3009'))
from probe_runtime import available, command, compile_probe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    if not available():
        print('BLOCKED: native .NET test compiler/runtime unavailable; no producer execution claimed')
        return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix='frame_producer_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    producer = ROOT / 'AFrame/src/AFramePlugin/FrameQuantities.cs'
    source = producer.read_text(encoding='utf-8')
    marker = '        internal static QuantityReport BuildReport('
    assert source.count(marker) == 1, 'Producer structure changed; extraction needs review'
    prefix = '\n'.join(line for line in source.splitlines() if line.startswith('using ') and 'Autodesk.' not in line)
    categories = re.findall(r'(?m)^\s*private static readonly string\[\] Categories = .*?;', source)
    assert len(categories) == 1, 'Producer categories declaration changed; extraction needs review'
    profile_field = re.findall(r'(?m)^\s*private readonly Dictionary<string, string\[\]> profileStates = .*?;', source)
    assert len(profile_field) == 1, 'Profile cache declaration changed; extraction needs review'
    start = source.index('        private bool RailProfileMatches(')
    brace = source.index('{', start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    profile_method = source[start:end]
    extracted = out / 'FrameQuantitiesPure.cs'
    extracted.write_text(prefix + '\nnamespace AFramePlugin { internal sealed class FrameQuantities {\n' +
                         categories[0] + '\n' + profile_field[0] + '\n' + profile_method + '\n' +
                         source[source.index(marker):], encoding='utf-8')
    executable = out / 'FrameProducerProbe.exe'
    core = ROOT / 'Common/FacadeQuantities.cs'
    driver = Path(__file__).with_name('FrameProducerProbe.cs')
    compiled = compile_probe([driver, core, extracted], ['System.Web.Extensions'], executable)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return 1
    checks = 0

    def check(value, message):
        nonlocal checks
        checks += 1
        assert value, message

    def run(name, request, response):
        payload = {'request': request, 'response': response, 'mapping': {'P1': 'Зона-А', 'P2': 'Зона-Б'}}
        infile, outfile = out / (name + '_input.json'), out / (name + '_output.json')
        infile.write_text(json.dumps(payload, ensure_ascii=False), encoding='utf-8')
        subprocess.run(command(executable) + [str(infile), str(outfile)], check=True)
        return json.loads(outfile.read_text(encoding='utf-8'))

    records = []
    fixtures = run_cases()
    for name, fixture in fixtures.items():
        request, response, expected = fixture['request'], fixture['response'], fixture['oracle']
        result = run(name, request, response)
        check(result['ok'], name + ': producer refused a supported successful frame: ' + str(result.get('error')))
        report = result['report']
        check(report['kind'] == 'frame', name + ': wrong quantity kind')
        elements = report['elements']
        check(len(elements) == expected['physical_count'], name + ': counts differ from real emitted arrays')
        check(len({item['element_id'] for item in elements}) == len(elements), name + ': duplicate physical identities')
        check(Counter(item['role'] for item in elements) == Counter(expected['counts']), name + ': physical categories merged or lost')
        for actual, wanted in zip(elements, expected['elements']):
            check(actual['role'] == wanted['role'] and actual['type'] == wanted['type'], name + ': producer array order or kind mismatch')
            check(actual['mark'] == wanted['mark'], name + ': profile mark changed or catalogue item invented')
            check((actual['length_mm'] is None and wanted['length_mm'] is None) or
                  (actual['length_mm'] is not None and wanted['length_mm'] is not None and
                   abs(actual['length_mm'] - wanted['length_mm']) < 1e-8), name + ': actual member length changed')
            check(actual['material'] is None and actual['coating'] is None and actual['product_id'] is None,
                  name + ': engineering/catalogue properties were inferred without sources')
        for key in ('withEstimates', 'withoutCladCutting'):
            aggregate = result[key]
            check(aggregate['ok'], name + ': common aggregation failed: ' + str(aggregate['issues']))
            rows = [row for row in aggregate['rows'] if row['basis'] == 'installed']
            check(sum(row['quantity'] for row in rows) == expected['physical_count'], name + ': estimates leaked into installed count')
            for role, length in expected['length_m'].items():
                actual = sum(row['total_length_m'] or 0 for row in rows if row['role'] == role)
                check(abs(actual - length) < 1e-9, name + ': aggregated metre length rounded or mixed with stock estimate')
            check(all(row['unit'] == 'шт.' for row in rows), name + ': physical count unit was changed to metres or mass')
        check(result['withEstimates']['rows'] == result['withoutCladCutting']['rows'],
              name + ': cladding-only cutting flag unexpectedly changes frame estimates')
        if name == 'vertical_stock':
            rails = [row for row in result['withEstimates']['rows'] if row['basis'] == 'installed' and row['role'] == 'rail']
            check(sum(row['quantity'] for row in rails) == 3 and sum(row['total_length_m'] for row in rails) == 6.08,
                  'Three installed pieces cannot become two estimated stock bars')
        if name == 'independent_zones':
            check(result['selected']['ok'], 'Independent zone selection must retain known installed objects')
            check(all(row['basis'] == 'installed' for row in result['selected']['rows']), 'Whole-run stock estimate cannot become a partial zone estimate')
        if name == 'only_clamps':
            check(set(item['role'] for item in elements) == {'clamp'}, 'Existing source rails were recounted as newly installed')
            check(report['estimates'] == [] and not any(row['basis'] == 'estimate' for row in result['withEstimates']['rows']),
                  'Clamps-only engine zero must not become a stock estimate for retained frame members')
            check(any(issue['code'] == 'Q_FRAME_ESTIMATE_NOT_RECALCULATED' for issue in report['issues']),
                  'Omitted retained-frame estimate needs an explicit reason')
        records.append({'case': name, 'counts': expected['counts'], 'length_m': expected['length_m'], 'physical_count': expected['physical_count']})

    fixture = fixtures['vertical_stock']
    for category in ARRAYS:
        response = copy.deepcopy(fixture['response'])
        response[category] = None
        check(not run('missing_' + category, fixture['request'], response)['ok'], 'Missing category silently became zero: ' + category)
    response = copy.deepcopy(fixture['response'])
    response['rails'][0]['len'] += 100
    check(not run('wrong_length', fixture['request'], response)['ok'], 'Producer accepted length inconsistent with geometry')
    response = copy.deepcopy(fixture['response'])
    response['rails'][0]['zone'] = 'unknown'
    check(not run('unknown_zone', fixture['request'], response)['ok'], 'Unowned physical member accepted')
    response = copy.deepcopy(fixture['response'])
    response['ok'] = False
    check(not run('failed_result', fixture['request'], response)['ok'], 'Failed engine response became a successful passport')
    provenance_file = out / 'retained_provenance.json'
    subprocess.run(command(executable) + ['--provenance', str(provenance_file)], check=True)
    provenance = json.loads(provenance_file.read_text(encoding='utf-8'))
    check(len(provenance['first']) == len(provenance['second']) == 1, 'Repeated clamps update grows provenance chain')
    check(provenance['first'] == provenance['second'], 'Second clamps update loses original source provenance')
    original = provenance['second'][0]
    check(original['run_id'] == 'run-original' and original['element_ids'] == ['R'], 'Retained rail adopts clamps operation identity')
    check(original['parameters']['wind_region'] == 'II' and original['source_revisions']['engine'] == 'original-engine-sha',
          'Original source settings or revisions lost')
    check(original['calc_report']['original_check'] == 123.456 and original['design_scope']['static_model_verification'] == 'not_verified',
          'Original calculation or engineering limits lost')
    check(len(provenance['mixed']) == 2 and {item['run_id'] for item in provenance['mixed']} == {'run-original', 'run-second'},
          'Members of different origins collapse into one source')
    check(all('retained_sources' not in item['parameters'] for item in provenance['mixed']), 'Nested provenance chain persisted')
    check(len(provenance['last']) == 1 and provenance['last'][0]['run_id'] == 'run-second' and provenance['last'][0]['element_ids'] == ['C2'],
          'Deleted members leave stale provenance or retained clamp loses its origin')
    check(provenance['none'] == [], 'No retained members must mean no retained provenance')
    profiles_file = out / 'profile_guard.json'
    subprocess.run(command(executable) + ['--profiles', str(profiles_file)], check=True)
    profiles = json.loads(profiles_file.read_text(encoding='utf-8'))
    for name, passed in profiles.items():
        check(passed, 'Actual read-only dynamic profile guard: ' + name)
    files = [producer, core, driver, Path(__file__).resolve(), Path(__file__).with_name('engine_oracles.py')]
    files += list((ROOT / 'AFrame/engine').glob('*.py')) + [ROOT / 'AFrame/engine/systems.json']
    manifest = {'status': 'PASS', 'checks': checks, 'cases': records,
                'scope': 'Real frame engine, actual C# producer/core and read-only profile guard; CAD links/properties are doubles',
                'live_autocad_checked': False,
                'source_sha256': {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('Frame producer checks:', checks, 'PASS')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
