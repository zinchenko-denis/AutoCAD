"""Trace the pilot's existing declaration-only boundary through actual product code.

Exit zero means the acceptance audit reproduced the current contract correctly.
It NEVER grants mounting/engineering admission. Current schema/1 has no physical
UK assignment or typed GP-UK / UK-KR2 interfaces. Future implementation requires
a separately reviewed contract and revision of this acceptance fixture.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
for directory in ('AFrame/engine', 'tools/catalog_connections_0110', 'tools/quantities_3009'):
    sys.path.insert(0, str(ROOT / directory))
import frame_engine
from frame_solution_catalog import catalog_snapshot
from test_frame_solution_selection import example_selection
from test_connection_store import compile_actual_producer
from probe_runtime import available, command, compile_probe

SCHEMA = 'pilot_interface_mapping_audit/1'
ARRAYS = ('rails', 'hrails', 'brackets', 'clamps', 'fittings')
CHECKS = []


def need(value, reason):
    if not value:
        raise AssertionError(reason)
    CHECKS.append(reason)


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + '\n', encoding='utf-8')


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inspect_trace(request, response, observed, expected_selection):
    """Check evidence integrity, never infer an engineering relation from a label."""
    need(observed.get('core_ok') is True, 'actual core accepts complete physical inventory')
    need(observed['response_unchanged'] and observed['request_unchanged'], 'actual consumer/producer do not modify request or response')
    need(observed['native_parameters_exact'], 'consumer request contains actual restored EngineParams without substitution')
    need(observed['source_unchanged'], 'table does not mutate the accepted report')
    report = observed['report']
    need(request.get('solution_selection') == expected_selection, 'request preserves intended declaration')
    need(report['parameters'].get('solution_selection') == expected_selection, 'producer preserves exact declaration')
    p = report['connection_passports'][0]
    need(p['schema'] == 'aframe_connection_passport/1', 'current passport schema is explicitly audited')
    need(observed['actual_dto_properties']['support'] == ['bracket_element_ids', 'offset_mm'],
         'actual support DTO contains geometric references only; revision requires independent review')
    need('interfaces' not in observed['actual_dto_properties']['passport'], 'actual current passport has no typed montage interfaces')
    need(len(observed['table_members']) == (len(p['members']) if p['status'] == 'inventory_only' else 1),
         'table represents each physical member or the exact unavailable control')
    need(all(e['product_id'] is None for e in report['elements']), 'declaration is not silently assigned as physical product')
    need(not any(e['role'] == 'extender' for e in report['elements']), 'no physical UK has been invented by the current producer')
    elements = {e['element_id']: e for e in report['elements']}
    need(len(elements) == len(report['elements']), 'physical IDs are unique')
    need(p['source_run_id'] == report['run_id'], 'new passport belongs to the actual producer run')
    for category in ARRAYS:
        for index, _ in enumerate(response[category]):
            need(report['run_id'] + ':' + category + ':' + str(index) in elements, 'each emitted array slot has its actual physical ID')
    need(len(elements) == sum(len(response[k]) for k in ARRAYS), 'no physical part is added or dropped')
    candidates = []
    for member in p['members']:
        rail = elements.get(member['rail_element_id'])
        need(rail is not None and rail['role'] == 'rail', 'candidate member resolves to the actual rail')
        for support in member['supports']:
            for bracket_id in support['bracket_element_ids']:
                bracket = elements.get(bracket_id)
                need(bracket is not None and bracket['role'] == 'bracket', 'candidate endpoint resolves to the actual bracket symbol')
                need(rail['zone_ids'] == bracket['zone_ids'] == [member['zone_id']], 'candidate IDs preserve canonical zone ownership')
                candidates.append({'rail_element_id': rail['element_id'], 'bracket_element_id': bracket_id,
                    'zone_id': member['zone_id'], 'offset_mm': support['offset_mm'],
                    'meaning': 'geometric_candidate_only', 'uk_element_id': None,
                    'gp_uk_interface': 'not_represented', 'uk_kr2_interface': 'not_represented'})
    if expected_selection is not None:
        need(response['solution_report']['selection'] == expected_selection, 'engine preserves exact historical declaration')
        status = report['engine_summary']['solution_report']
        need(status['physical_assignment'] == 'not_asserted' and status['calculation_binding'] == 'not_confirmed',
             'producer retains refusal to assign products or bind calculation')
        need(p['status'] == 'inventory_only' and p['catalog_scope'] == 'reference_project_only', 'geometric inventory is not an approved montage contract')
        need(p['sources'] == response['connection_passport']['sources'] and
             p['references'] == response['connection_passport']['references'], 'producer preserves reference provenance exactly')
        need(p['sources'][0]['source_id'] == 'vector1_type1_reference_2026' and
             expected_selection['source_id'] == 'vector1_2015', 'reference calculation and historical declaration remain distinct sources')
        need(p['coverage'] == {'fixed_sliding': 'not_modeled', 'splice_continuity': 'not_modeled',
             'gravity_load_distribution': 'not_verified', 'strength': 'not_verified'}, 'geometric links do not upgrade engineering coverage')
        need(len(candidates) > 0, 'positive geometric links are present in the acceptance fixture')
    else:
        need(p['status'] == 'unavailable' and not p['members'], 'legacy Standart control does not gain a pilot inventory')
        need('solution_report' not in response and 'solution_report' not in report['engine_summary'], 'no historical declaration is invented for legacy mode')
    return {'run_id': report['run_id'], 'physical_elements': [{k: e[k] for k in
        ('element_id', 'role', 'zone_ids', 'mark', 'type', 'product_id', 'origin')} for e in report['elements']],
        'candidate_pairs': candidates, 'physical_extender_count': 0, 'confirmed_typed_interfaces': 0,
        'historical_selection': expected_selection, 'connection_sources': p['sources'],
        'profile_matches': [m['profile_match'] for m in p['members']],
        'candidate_schema_properties': observed['actual_dto_properties']['support'],
        'assessment': 'BLOCKED' if expected_selection is not None else 'OUTSIDE_EXPLICIT_PILOT',
        'run_identity_note': 'Separate producer runs have different real IDs; only emitted arrays are compared across A/B.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    if not available():
        print('BLOCKED: native .NET compiler/runtime unavailable; audit NOT_RUN')
        return 2
    out = args.out.resolve(); out.mkdir(parents=True, exist_ok=True)
    build = out / 'build'; build.mkdir(exist_ok=True)
    started = time.perf_counter()
    _, producer_sources = compile_actual_producer(build / 'producer')
    src = ROOT / 'AFrame/src/AFramePlugin'
    repository_sources = [Path(__file__).with_name('InterfaceAuditProbe.cs'), src / 'FrameSettings.cs',
        src / 'FrameSolutionSelection.cs', src / 'FrameProjectParameters.cs', ROOT / 'Common/FacadeQuantities.cs',
        ROOT / 'Facades/src/AFacadesPlugin/ConnectionTableData.cs',
        ROOT / 'Facades/src/AFacadesPlugin/QuantityTableIdentity.cs']
    extracted_producer = build / 'producer/FrameQuantitiesPure.cs'
    sources = repository_sources + [extracted_producer]
    exe = build / 'InterfaceAuditProbe.exe'
    compiled = compile_probe(sources, ['System.Web.Extensions'], exe)
    (build / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    need(compiled.returncode == 0, 'actual settings/producer/core/table probe compiles')
    timings = {'compile_seconds': time.perf_counter() - started, 'cases': {}}

    def native(mode, value, folder, stem):
        infile, outfile = folder / (stem + '_input.json'), folder / (stem + '_output.json')
        save(infile, value)
        subprocess.run(command(exe) + [mode, str(infile), str(outfile)], check=True)
        return json.loads(outfile.read_text(encoding='utf-8'))

    first = example_selection()
    second = copy.deepcopy(first)
    second['bracket'].update(nominal_width_mm=85, L_mm=350, execution='corrosion_resistant')
    second['extender'].update(nominal_width_mm=85, L_mm=150, thickness_mm=1.5, execution='corrosion_resistant')
    second['profile'].update(a_mm=80, b_mm=60, thickness_mm=1.5, execution='corrosion_resistant')
    settings_inputs = [{'name': name, 'selection': selected} for name, selected in
        (('A', first), ('B', second), ('no_selection', None))]
    settings_inputs += [{'name': 'step_below_ui_minimum', 'selection': first, 'step_main': 99},
        {'name': 'calc_selected', 'selection': first, 'steps': 'calc'}]
    settings = native('--settings', settings_inputs, out, 'settings')
    need(isinstance(settings, list), 'actual settings scenarios execute')
    by_name = {s['name']: s for s in settings}
    need(by_name['step_below_ui_minimum']['validation_error'] is not None, 'actual Validate refuses manual step99')
    need(by_name['calc_selected']['validation_error'] is not None and by_name['calc_selected']['engine_error'] is not None,
         'actual settings refuse selected historical calculation before engine request')
    runs = {}; traces = {}
    for name, expected in (('A', first), ('B', second), ('no_selection', None)):
        s = by_name[name]
        need(s['validation_error'] is None and s['engine_error'] is None and s['roundtrip'], 'actual settings validate and roundtrip: ' + name)
        need(s['parameters']['system'] == s['system_override'], 'EngineParams consumes actual SysOverride: ' + name)
        expected_system = 'Standart' if expected is None else 'Вектор-1'
        need(s['system_override']['name'] == expected_system, 'legacy/explicit preset boundary: ' + name)
        need(s['parameters']['system']['bracket_step'] == 800 and s['parameters']['system']['bracket_step_corner'] == 800,
             'normal positive manual steps reach engine: ' + name)
        request = copy.deepcopy(s['parameters'])
        request.update(op='frame', floors_y=[1500], joints_x=[600], zones=[{'zone_id': 'Z0', 'zone': {
            'contour': {'pts': [[0, 0], [1200, 0], [1200, 3000], [0, 3000]]}, 'openings': []}}])
        before = copy.deepcopy(request)
        tick = time.perf_counter(); response = frame_engine.run(request); engine_seconds = time.perf_counter() - tick
        need(request == before and response['ok'], 'real engine succeeds without changing input: ' + name)
        folder = out / 'cases' / name
        save(folder / 'request.json', request); save(folder / 'response.json', response)
        payload = {'request': request, 'response': response, 'settings': s['settings']}
        observed = native('--run', payload, build / name, 'consume')
        need('rejected' not in observed, 'actual solution-response consumer accepts: ' + name)
        save(folder / 'report.json', observed['report'])
        table = {key: value for key, value in observed.items() if key not in ('report', 'milliseconds')}
        save(folder / 'table.json', table)
        traces[name] = inspect_trace(request, response, observed, expected)
        timings['cases'][name] = {'engine_seconds': engine_seconds, 'native_milliseconds': observed['milliseconds']}
        runs[name] = (request, response, observed, payload)
    need(first != second, 'paired valid declarations are different')
    need(all(runs['A'][1][key] == runs['B'][1][key] for key in ARRAYS), 'A/B preserve all actual emitted physical arrays')
    need(runs['A'][1]['connection_passport'] == runs['B'][1]['connection_passport'], 'A/B preserve geometric connection passport')
    need(traces['A']['run_id'] != traces['B']['run_id'], 'separate producer runs are not called the same physical parts')
    need(first['profile']['a_mm'] == 40 and second['profile']['a_mm'] == 80 and
         {e['mark'] for e in runs['A'][2]['report']['elements'] if e['role'] == 'rail'} ==
         {e['mark'] for e in runs['B'][2]['report']['elements'] if e['role'] == 'rail'} == {'ГП-40-40-1,2'},
         'different declared GP does not relabel preset physical marks')

    negatives = []
    for name, mutate in (
        ('lost_declaration', lambda x: x[2]['report']['parameters'].pop('solution_selection')),
        ('wrong_selection', lambda x: x[2]['report']['parameters']['solution_selection']['profile'].update(a_mm=80)),
        ('source_substitution', lambda x: x[2]['report']['connection_passports'][0]['sources'][0].update(source_id='vector1_2015')),
        ('invented_product_assignment', lambda x: x[2]['report']['elements'][0].update(product_id='declared-gp-is-not-an-installed-product')),
        ('lost_candidate_identity', lambda x: x[2]['report']['connection_passports'][0]['members'][0]['supports'][0]['bracket_element_ids'].__setitem__(0, 'missing'))):
        altered = copy.deepcopy(runs['A'][:3]); mutate(altered)
        count = len(CHECKS)
        try:
            inspect_trace(*altered, first)
        except AssertionError as error:
            negatives.append({'name': name, 'rejected_by': 'audit evidence-integrity assertions', 'reason': str(error)})
        else:
            raise AssertionError('False verification PASS: ' + name)
        finally:
            del CHECKS[count:]
        need(True, 'audit rejects integrity mutation: ' + name)
    bad = copy.deepcopy(runs['A'][2]['report'])
    bad['connection_passports'][0]['members'][0]['supports'][0]['bracket_element_ids'][0] = 'missing'
    refused = native('--inspect', bad, build / 'negative', 'missing_physical_endpoint')
    need(refused.get('core_ok') is False and any(i['code'] == 'Q_CONNECTION_INVALID' for i in refused['issues']),
         'actual common core refuses a missing candidate endpoint')
    negatives.append({'name': 'missing_physical_endpoint', 'rejected_by': 'actual common core', 'issues': refused['issues']})
    bad_response = copy.deepcopy(runs['A'][3]); bad_response['response']['solution_report']['physical_assignment'] = 'assigned'
    refused = native('--run', bad_response, build / 'negative', 'false_engine_assignment')
    need(refused.get('rejected') is True, 'actual command solution-report guard refuses invented assignment')
    negatives.append({'name': 'false_engine_assignment', 'rejected_by': 'actual ValidateEngineReport', 'reason': refused['error']})
    bad_request = copy.deepcopy(runs['A'][0]); bad_request['calc'] = {}
    calc = frame_engine.run(bad_request)
    need(calc.get('error_code') == 'E_SOLUTION_CALC_UNCONFIRMED' and 'rails' not in calc, 'actual engine refuses selected historical calculation')
    wrong_source = copy.deepcopy(first); wrong_source['source_sha256'] = '0' * 64
    refused = native('--settings', [{'name': 'wrong_historical_source', 'selection': wrong_source}], build / 'negative', 'wrong_historical_source')
    need(isinstance(refused, dict) and refused.get('rejected') is True, 'actual C# settings reject changed historical source identity')
    negative_request = copy.deepcopy(runs['A'][0]); negative_request['solution_selection'] = wrong_source
    refused_engine = frame_engine.run(negative_request)
    need(refused_engine.get('error_code') == 'E_SOLUTION_IDENTITY' and 'rails' not in refused_engine,
         'actual engine rejects changed historical source before geometry')
    negatives.append({'name': 'wrong_historical_source', 'rejected_by': 'actual C# settings and actual engine',
        'settings_reason': refused['error'], 'engine_response': refused_engine})
    save(out / 'negative_controls.json', negatives)
    save(out / 'trace.json', traces)
    save(out / 'calc_refusal.json', calc)
    assessment = {'status': 'BLOCKED', 'mounting_contract_confirmed': False, 'automatic_selection_allowed': False,
        'reasons': [
            {'code': 'PHYSICAL_INTERFACE_MODEL_NOT_REPRESENTED', 'detail': 'Current schema stores rail-axis to bracket-symbol candidates. Physical UK assignment and typed GP-UK / UK-KR2 interfaces are absent.'},
            {'code': 'ENGINEERING_FACTS_NOT_CONFIRMED', 'detail': 'Current audited contract preserves not_asserted/not_confirmed/not_modeled. Fastener/hole geometry, movements, load portions and continuity are not established by these candidates.'}],
        'scope': 'Exact refusal of current contract; no generic readiness evaluator or user-bool permission mechanism. Future contract needs separate engineering review.'}
    dependencies = set(producer_sources + repository_sources +
        [Path(__file__).resolve(), ROOT / 'tools/catalog_connections_0110/test_connection_store.py',
         ROOT / 'tools/quantities_3009/probe_runtime.py', ROOT / 'AFrame/engine/systems.json'])
    dependencies.update((ROOT / 'AFrame/engine').glob('*.py'))
    artifacts = [p for p in out.rglob('*.json') if 'build' not in p.relative_to(out).parts and p.name != 'manifest.json']
    manifest = {'schema': SCHEMA, 'verification': {'status': 'PASS', 'checks': len(CHECKS), 'assertions': CHECKS},
        'engineering_assessment': assessment, 'full_acceptance_cycle': 'NOT_RUN', 'live_autocad_checked': False,
        'path': 'actual FrameSettings.Validate / EngineParams / SysOverride -> frame_engine -> actual ValidateEngineReport -> actual FrameQuantities -> common core -> table',
        'cad_identity': 'explicit CAD doubles only; actual AutoCAD objects/WinForms clicks not exercised',
        'response_modified': False, 'negative_controls': 'Separate copied DTO/response mutations only, never substituted for real case outputs',
        'pair_comparison': 'A/B arrays only; producer run IDs differ, no equality of physical identities across runs is asserted',
        'declaration_source': catalog_snapshot()['source'],
        'counts': {name: {'physical_elements': len(trace['physical_elements']), 'candidate_pairs': len(trace['candidate_pairs']),
            'physical_extenders': trace['physical_extender_count'], 'confirmed_typed_interfaces': trace['confirmed_typed_interfaces']} for name, trace in traces.items()},
        'performance': {'scope': 'Audit runtime only; no live AutoCAD performance claim', **timings},
        'generated_probe_source': {'path': 'build/producer/FrameQuantitiesPure.cs', 'sha256': digest(extracted_producer),
            'kind': 'extracted build artifact, not a repository source'},
        'source_sha256': {str(p.relative_to(ROOT)): digest(p) for p in sorted(dependencies)},
        'artifact_sha256': {str(p.relative_to(out)): digest(p) for p in sorted(artifacts)}}
    save(out / 'manifest.json', manifest)
    print('Pilot interface audit:', len(CHECKS), 'verification PASS; engineering admission BLOCKED; full acceptance NOT_RUN')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
