"""Actual frame engine -> actual pure producer -> typed storage/core contract.

CAD identity links are doubles. This verifies no live AutoCAD timing or host behavior.
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
sys.path.insert(0, str(ROOT / 'tools/frame_quantities_3009'))
from engine_oracles import cases, frame_engine


def compile_actual_producer(out):
    out = Path(out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    producer = ROOT / 'AFrame/src/AFramePlugin/FrameQuantities.cs'
    source = producer.read_text(encoding='utf-8')
    marker = '        internal static QuantityReport BuildReport('
    assert source.count(marker) == 1, 'Producer extraction needs review'
    prefix = '\n'.join(line for line in source.splitlines() if line.startswith('using ') and 'Autodesk.' not in line)
    categories = re.findall(r'(?m)^\s*private static readonly string\[\] Categories = .*?;', source)
    assert len(categories) == 1
    extracted = out / 'FrameQuantitiesPure.cs'
    extracted.write_text(prefix + '\nnamespace AFramePlugin { internal sealed class FrameQuantities {\n' +
                         categories[0] + '\n' + source[source.index(marker):], encoding='utf-8')
    executable = out / 'ConnectionStoreProbe.exe'
    driver = Path(__file__).with_name('ConnectionStoreProbe.cs')
    core = ROOT / 'Common/FacadeQuantities.cs'
    compiled = compile_probe([driver, core, extracted], ['System.Web.Extensions'], executable)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        raise RuntimeError("Actual producer probe compilation failed")
    return executable, [producer, core, driver]


def compile_and_run_fixture(out):
    """Self-contained UI integration fixture from the actual engine and producer."""
    out = Path(out).resolve()
    executable, _ = compile_actual_producer(out)
    request = cases()["vertical_opening"]
    request["system"] = "Вектор-1"
    response = frame_engine.run(copy.deepcopy(request))
    payload = {"request": request, "response": response, "mapping": {"P1": "Зона-А", "P2": "Зона-Б"}}
    infile, outfile = out / "fixture_input.json", out / "fixture_output.json"
    infile.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    subprocess.run(command(executable) + ["--produce", str(infile), str(outfile)], check=True)
    result = json.loads(outfile.read_text(encoding="utf-8"))
    if not result["ok"]:
        raise AssertionError("Actual producer fixture failed: " + str(result))
    report = out / "fixture_report.json"
    report.write_text(json.dumps(result["report"], ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    if not available():
        print('BLOCKED: native .NET compiler/runtime unavailable')
        return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix='connection_store_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    executable, compiled_sources = compile_actual_producer(out)
    subprocess.run(command(executable) + ['--contracts', str(out / 'core_cases.json')], check=True)
    checks = 0
    def check(value, message):
        nonlocal checks
        checks += 1
        assert value, message
    def produce(name, request, response):
        payload = {'request': request, 'response': response, 'mapping': {'P1': 'Зона-А', 'P2': 'Зона-Б'}}
        infile, outfile = out / (name + '_input.json'), out / (name + '_output.json')
        infile.write_text(json.dumps(payload, ensure_ascii=False), encoding='utf-8')
        subprocess.run(command(executable) + ['--produce', str(infile), str(outfile)], check=True)
        return json.loads(outfile.read_text(encoding='utf-8'))
    fixtures = {}
    for name in ('vertical_stock', 'vertical_opening', 'independent_zones'):
        request = cases()[name]
        request['system'] = 'Вектор-1'
        response = frame_engine.run(copy.deepcopy(request))
        result = produce(name, request, response)
        check(result['ok'], name + ': ' + str(result.get('error', result.get('rows'))))
        passport = result['report']['connection_passports'][0]
        check(passport['status'] == 'inventory_only', name + ': available inventory lost')
        check(len(passport['members']) == len(response['rails']), name + ': member count')
        ids = {p['element_id']: p for p in result['report']['elements']}
        for actual, expected in zip(passport['members'], response['connection_passport']['members']):
            check(actual['rail_element_id'].endswith(':rails:' + str(expected['rail_index'])), 'rail stable index')
            check(actual['zone_id'] == ids[actual['rail_element_id']]['zone_id'], 'member root zone')
            check(actual['intervals_mm'] == expected['intervals_mm'], 'span intervals changed')
            for support, source_support in zip(actual['supports'], expected['supports']):
                check([v.rsplit(':', 1)[1] for v in support['bracket_element_ids']] == [str(i) for i in source_support['bracket_indices']], 'bracket stable global indices')
                check(all(ids[v]['zone_id'] == actual['zone_id'] for v in support['bracket_element_ids']), 'cross-zone support')
        check(all(p['product_id'] is None for p in result['report']['elements']), 'reference interpreted as SKU')
        check('connection_passport' not in result['report']['engine_summary'], 'duplicate passport geometry/DTO')
        (out / (name + '_report.json')).write_text(json.dumps(result['report'], ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        fixtures[name] = request, response
    request, response = fixtures['vertical_stock']
    for field, invalid in [('rail_index', -1), ('rail_index', len(response['rails'])), ('rail_index', 0.5), ('rail_index', True), ('rail_index', '0')]:
        bad = copy.deepcopy(response); bad['connection_passport']['members'][0][field] = invalid
        check(not produce('bad_' + str(invalid), request, bad)['ok'], 'invalid physical index accepted')
    bad = copy.deepcopy(response)
    bad['connection_passport']['members'][0]['supports'][0]['bracket_indices'] = [len(response['brackets'])]
    check(not produce('bad_bracket', request, bad)['ok'], 'bad bracket index accepted')
    request, response = fixtures['independent_zones']
    bad = copy.deepcopy(response)
    member = bad['connection_passport']['members'][0]
    foreign = next(i for i, b in enumerate(bad['brackets']) if b['zone'] != member['zone_id'])
    member['supports'][0]['bracket_indices'] = [foreign]
    check(not produce('foreign_zone', request, bad)['ok'], 'cross-zone engine index accepted')
    legacy = copy.deepcopy(response); legacy.pop('connection_passport')
    result = produce('legacy', request, legacy)
    check(result['ok'] and result['report']['connection_passports'] is None, 'legacy response must preserve explicit null')
    files = compiled_sources + [Path(__file__).resolve()] + list((ROOT / 'AFrame/engine').glob('*.py'))
    core_cases = json.loads((out / 'core_cases.json').read_text())
    manifest = {'status': 'PASS', 'producer_checks': checks, 'core_checks': core_cases['checks'],
                'checks': checks + core_cases['checks'], 'live_autocad_checked': False,
                'source_sha256': {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('Connection producer/store checks:', manifest['checks'], 'PASS')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
