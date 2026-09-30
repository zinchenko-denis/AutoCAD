"""Actual Python engine -> actual CAD-free C# producer -> common quantity rows.

The producer's CAD-independent method tail is extracted verbatim; no CAD
substitute can validate a live transaction. Shapely supplies independent areas.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from probe_runtime import available, command, compile_probe

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'AClad/engine'))
import clad_engine


def rect(x, y, w, h):
    return [[x, y], [x + w, y], [x + w, y + h], [x, y + h]]


def zone(zid, points, holes=()):
    return {'zone_id': zid, 'zone': {'schema': 'facade_zone/1', 'id': zid, 'units': 'mm',
            'outer': {'pts': points}, 'openings': [
                {'id': f'H{i}', 'kind': 'window', 'poly': {'pts': hole}} for i, hole in enumerate(holes)]}}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--out', type=Path)
    args = ap.parse_args()
    if not available():
        print('BLOCKED: native .NET test compiler/runtime unavailable; no producer execution claimed')
        return 2
    from shapely.geometry import Polygon
    out = (args.out or Path(tempfile.mkdtemp(prefix='cladding_producer_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    producer = ROOT / 'AClad/src/ACladPlugin/CladdingQuantities.cs'
    source = producer.read_text(encoding='utf-8')
    marker = '        internal static QuantityReport BuildReport('
    assert source.count(marker) == 1, 'Producer structure changed; extraction must be reviewed'
    tail = source[source.index(marker):]
    prefix = '\n'.join(line for line in source.splitlines() if line.startswith('using ') and 'Autodesk.' not in line)
    extracted = out / 'CladdingQuantitiesPure.cs'
    extracted.write_text(prefix + '\nnamespace ACladPlugin { internal sealed class CladdingQuantities {\n' + tail, encoding='utf-8')
    executable = out / 'CladdingProducerProbe.exe'
    sources = [ROOT / 'tools/quantities_3009/CladdingProducerProbe.cs',
               ROOT / 'Common/FacadeQuantities.cs', producer]
    compiled = compile_probe([sources[0], sources[1], extracted], ['System.Web.Extensions'], executable)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return 1
    base = {'op': 'tile_pattern', 'tile': {'w': 600, 'h': 600}, 'gap': {'v': 0, 'h': 0},
            'pattern': {'rows': [['T1', 'T2']], 'row_shifts': [0]}, 'datum': {'mode': 'bbox'}}
    cases = {}
    cases['hole'] = dict(base, zones=[zone('P1', rect(0, 0, 600, 600), [rect(250, 250, 100, 100)])])
    cases['types_cuts'] = dict(base, zones=[zone('P1', rect(0, 0, 1550, 1230))])
    cases['merged'] = dict(base, zones=[zone('P1', rect(0, 0, 450, 600)), zone('P2', rect(450, 0, 450, 600))])
    cases['separate'] = dict(base, zones=[zone('P1', rect(0, 0, 600, 600)), zone('P2', rect(1500, 0, 600, 600))])
    cases['slope'] = {'op': 'cladding', 'tile': {'w': 600, 'h': 600}, 'gap': {'v': 10, 'h': 10},
                      'datum': 0, 'mode': 'edge', 'zones': [zone('P1', [[0, 0], [1200, 0], [1200, 1100], [0, 600]])]}
    cases['empty_member'] = {'op': 'cladding', 'tile': {'w': 600, 'h': 600}, 'gap': {'v': 10, 'h': 10},
                            'datum': 0, 'mode': 'edge', 'zones': [zone('P1', rect(0, 0, 1200, 600)),
                                                                zone('P2', rect(1500, -1000, 600, 500))]}
    cases['whole_empty'] = {'op': 'cladding', 'tile': {'w': 600, 'h': 600}, 'gap': {'v': 10, 'h': 10},
                           'datum': 0, 'mode': 'edge', 'zones': [zone('P1', rect(1500, -1000, 600, 500))]}
    cases['whole_empty_tile'] = dict(base, gap={'v': 10, 'h': 10}, min_piece=30,
                                    zones=[zone('P1', rect(0, 0, 10, 10))])
    checks = 0
    records = []

    def check(value, message):
        nonlocal checks
        checks += 1
        assert value, message

    def run(name, request, response):
        payload = {'key': 'ATCLAD' if request['op'] == 'cladding' else 'ATTILE',
                   'request': request, 'response': response, 'mapping': {'P1': 'Зона-А', 'P2': 'Зона-Б'}}
        infile, outfile = out / (name + '_input.json'), out / (name + '_output.json')
        infile.write_text(json.dumps(payload, ensure_ascii=False), encoding='utf-8')
        subprocess.run(command(executable) + [str(infile), str(outfile)], check=True)
        return json.loads(outfile.read_text(encoding='utf-8'))

    for name, request in cases.items():
        response = clad_engine.run(request)
        check(response['ok'], name + ': source engine failed')
        result = run(name, request, response)
        check(result['ok'], name + ': producer rejected valid response: ' + str(result.get('error')))
        elements = result['report']['elements']
        original = response.get('pieces', response.get('inserts'))
        check(len(elements) == len(original), name + ': physical piece count changed')
        check(len({item['element_id'] for item in elements}) == len(elements), name + ': duplicate physical identity')
        for element, piece in zip(elements, original):
            if 'rings' in piece:
                expected = Polygon(piece['rings'][0], piece['rings'][1:]).area
            elif 'pts' in piece:
                expected = Polygon(piece['pts']).area
            else:
                expected = piece['w'] * piece['h']
            check(abs(element['area_mm2'] - expected) < 1e-6, name + ': area differs from independent polygon')
        check(result['result']['ok'], name + ': common aggregator rejected valid report')
        rows = [row for row in result['result']['rows'] if row['basis'] == 'installed']
        check(sum(row['quantity'] for row in rows) == len(original), name + ': aggregate count differs from physical pieces')
        check(abs(sum(row['area_m2'] for row in rows) * 1e6 - sum(item['area_mm2'] for item in elements)) < 1e-5,
              name + ': aggregate area differs from physical pieces')
        check(all(item['product_id'] is None and item['mark'] is None and item['color'] is None for item in elements),
              name + ': catalogue metadata invented')
        if name == 'hole':
            check(len(elements) == 1 and len(elements[0]['rings']) == 2 and elements[0]['area_mm2'] == 350000,
                  'Outer plus hole must remain ONE element of area 0.35 m²')
        if name == 'merged':
            check(not result['subset']['ok'] and not result['subset']['rows'], 'Partial merged scope must be rejected')
            check(all(item['zone_ids'] == ['Зона-А', 'Зона-Б'] for item in elements), 'Merged ownership lost')
        if name == 'separate':
            check(result['subset']['ok'], 'Independent zone selection wrongly rejected')
        if name == 'slope':
            check(sum(item['piece_kind'] == 'shaped' for item in elements) == 2, 'Sloped pieces lost their kind')
            check(result['report']['cutting'] == [], 'ATCLAD must not invent cutting stock')
        if name == 'empty_member':
            check(result['report']['zone_ids'] == ['Зона-А', 'Зона-Б'] and len(elements) == 2,
                  'Successful zero-piece zone must remain in source scope')
        if name in ('whole_empty', 'whole_empty_tile'):
            check(result['report']['zone_ids'] == ['Зона-А'] and elements == [] and result['result']['rows'] == [],
                  'Known empty layout must preserve source scope and zero installed rows')
            check(result['result']['completeness'] == 'complete', 'Known zero must not be confused with missing composition')
        records.append({'case': name, 'physical_pieces': len(elements), 'area_mm2': sum(item['area_mm2'] for item in elements)})

    request = cases['hole']
    response = clad_engine.run(request)
    bad_area = copy.deepcopy(response)
    bad_area['pieces'][0]['area'] += 100
    check(not run('bad_area', request, bad_area)['ok'], 'Producer accepted mismatched ring area')
    no_cutting = copy.deepcopy(response)
    del no_cutting['per_zone'][0]['by_type']
    check(not run('missing_cutting', request, no_cutting)['ok'], 'Producer silently lost cutting scope')
    unknown_zone = copy.deepcopy(response)
    unknown_zone['pieces'][0]['zone'] = 'missing'
    check(not run('unknown_zone', request, unknown_zone)['ok'], 'Producer accepted unowned physical piece')
    missing_pieces = copy.deepcopy(response)
    missing_pieces['pieces'] = None
    check(not run('missing_pieces', request, missing_pieces)['ok'], 'Missing composition must not be treated as known zero')
    all_sources = sources + list((ROOT / 'AClad/engine').glob('*.py'))
    manifest = {'status': 'PASS', 'checks': checks, 'cases': records, 'live_autocad_checked': False,
                'source_sha256': {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in all_sources}}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'Cladding producer checks: {checks}; PASS')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
