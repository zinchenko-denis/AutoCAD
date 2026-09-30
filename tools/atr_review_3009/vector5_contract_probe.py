#!/usr/bin/env python3
"""Synthetic Vector-5 capability probe; real pure methods, no AutoCAD host.

Run after sourcing the local Mono environment. --source can point at an
exported Git revision; no private PDF or drawing is used by this probe.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--source', type=Path, default=Path(__file__).resolve().parents[2])
    ap.add_argument('--out', type=Path, required=True)
    a = ap.parse_args()
    root, out = a.source.resolve(), a.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    sys.path[:0] = [str(root / 'AFrame/engine'), str(root / 'AClad/engine')]
    from frame_plan import frame_plan
    from cladding_plan import cladding_plan
    checks = []

    def check(name, condition):
        checks.append({'name': name, 'ok': bool(condition)})
        if not condition:
            raise AssertionError(name)

    roles = []
    for sub in ('vertical', 'interfloor', 'ortho'):
        roles.append({'name': sub, 'has_layout': True, 'floors_picked': 1,
                      'actions': [['Cladding', 'composite'], ['Mode', 'frame'],
                                  ['SetSubType', sub], ['Steps', 'manual']]})
    roles += [{'name': 'zero_gap', 'has_layout': True, 'floors_picked': 1,
               'actions': [['Cladding', 'composite'], ['Mode', 'frame'],
                           ['Steps', 'manual'], ['RailGap', 0]]}]
    for mode in ('all', 'clamps'):
        roles.append({'name': mode, 'has_layout': True, 'floors_picked': 1,
                      'actions': [['Cladding', 'composite'], ['Mode', mode]]})
    (out / 'settings_input.json').write_text(json.dumps(roles), encoding='utf-8')
    subprocess.run([shutil.which('mcs') or 'mcs', '-r:System.Web.Extensions.dll',
                    '-out:' + str(out / 'settings.exe'),
                    str(root / 'AFrame/tools/roles/RolesDump.cs'),
                    str(root / 'AFrame/src/AFramePlugin/FrameSettings.cs')], check=True)
    subprocess.run([shutil.which('mono') or 'mono', str(out / 'settings.exe'),
                    str(out / 'settings_input.json'), str(out / 'settings_output.json')], check=True)
    settings = json.loads((out / 'settings_output.json').read_text())
    rect = [[0, 0], [6000, 0], [6000, 6000], [0, 6000]]
    contours = [{'outer': rect}]
    layout_req = {'contours': contours, 'tile': {'w': 1000, 'h': 1000},
                  'gap': {'v': 10, 'h': 10}, 'mode': 'edge'}
    layout = cladding_plan(layout_req)
    check('AClad planar layout succeeds', layout['ok'])
    check('AClad 36 faces, 25 full, 11 cut', layout['summary']['tiles'] == 36 and
          layout['summary']['full'] == 25 and layout['summary']['cut'] == 11)
    check('AClad face has only XY dimensions in this rectangular case',
          all(set(p) == {'x', 'y', 'w', 'h'} for p in layout['inserts']))
    base = {'contours': contours, 'joints_x': layout['joints_x'],
            'rows_y': layout['rows_y'], 'floors_y': [3000]}
    results = {}
    requests = {}
    for s in settings:
        name = s['name']
        check(name + ': C# actions applied', s['apply_error'] is None)
        check(name + ': settings roundtrip', s['roundtrip'])
        req = dict(copy.deepcopy(base), **s['params'])
        # Clamps mode is added by FrameCommand, not EngineParams.
        if name == 'clamps':
            req['parts'] = 'clamps'
        p = frame_plan(req)
        requests[name], results[name] = req, p
        if name in ('all', 'clamps'):
            check(name + ': C# validation refuses', bool(s['valid']))
            check(name + ': Python refuses unsupported AKP fasteners',
                  not p['ok'] and p.get('error_code') == 'E_COMPOSITE_FASTENERS_UNSUPPORTED')
        else:
            check(name + ': C# validation accepts manual frame', not s['valid'])
            check(name + ': frame engine succeeds', p['ok'])
            check(name + ': no tile clamps or tile rails for AKP',
                  not p['clamps'] and not p.get('tile_rails'))
    check('interfloor emits NGP and NSP, album type4 topology',
          {p['kind'] for p in results['interfloor']['hrails']} == {'НГП'} and
          {p['kind'] for p in results['interfloor']['rails']} == {'НСП'})
    check('interfloor fittings are generic XY markers',
          all(set(p) == {'x', 'y', 'kind'} for p in results['interfloor']['fittings']))
    check('ortho emits GP and ShP, not reinforced NSP',
          {p['profile'] for p in results['ortho']['hrails']} == {'ГП-40-40'} and
          {p['profile'] for p in results['ortho']['rails']} == {'ШП-60-20'})
    x = layout['joints_x'][0]
    spans = sorted((p['y0'], p['y1']) for p in results['zero_gap']['rails'] if p['x'] == x)
    check('zero manual gap yields touching rails at 3000', spans == [(0.0, 3000.0), (3000.0, 6000.0)])
    defreq = copy.deepcopy(requests['vertical'])
    defreq['system']['min_from_defshov'] = 1000
    changed = frame_plan(defreq)
    check('min_from_defshov override does not affect geometry', all(
        changed[k] == results['vertical'][k] for k in ('rails', 'hrails', 'brackets', 'clamps', 'fittings')))
    unknown = frame_plan(dict(copy.deepcopy(requests['vertical']), cladding='metal_cassette'))
    check('MK has no accepted material contract', not unknown['ok'])
    files = ['AFrame/engine/frame_plan.py', 'AFrame/engine/frame_calc.py',
             'AFrame/engine/systems.json', 'AFrame/src/AFramePlugin/FrameSettings.cs',
             'AClad/engine/cladding_plan.py']
    report = {'source': str(root), 'checks': checks, 'passed': len(checks),
              'source_sha256': {p: hashlib.sha256((root / p).read_bytes()).hexdigest() for p in files},
              'layout_request': layout_req, 'layout': layout, 'settings': settings,
              'requests': requests, 'results': results, 'unknown_mk': unknown,
              'min_from_defshov_1000_result': changed}
    (out / 'vector5_contract_probe.json').write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'passed': len(checks), 'failed': 0,
                      'summary': {k: v.get('summary', v.get('error_code')) for k, v in results.items()}},
                     ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
