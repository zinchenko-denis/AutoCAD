#!/usr/bin/env python3
"""Native FrameSettings -> frame_engine/frame_plan architecture regressions.

--observe records existing defects and returns zero if execution succeeded.
Without it, unmet safety expectations are failures. No AutoCAD is executed.
Product files are compiled/imported unchanged; only output artifacts are written.
"""
import argparse
import copy
import hashlib
import io
import importlib
import tarfile
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def role(name, actions):
    return dict(name=name, has_layout=True, floors_picked=3, actions=actions)


def inputs(params):
    out = copy.deepcopy(params)
    out.update(op='frame', contours=[dict(id='outer', pts=[[0, 0], [6000, 0], [6000, 6000], [0, 6000]], bulges=[0]*4)],
               joints_x=list(range(0, 6001, 600)), rows_y=list(range(0, 6001, 600)),
               floors_y=[0, 3000, 6000], corners_x=[0, 6000])
    return out


def recheck_mass(result):
    rep = result['calc_report']
    checks = {}
    for zone, row in rep['layout_verification']['zones'].items():
        if row['max_step'] is None:
            continue
        profile = rep['profile'][zone]
        inp = dict(rep['inputs'], profile=profile, q_rails=max(rep['inputs']['q_rails'], fc.PROFILES[profile]['q']))
        checks[zone] = fc.calc_chain(inp, row['max_step'], zone)
    return checks


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--out', type=Path)
    ap.add_argument('--observe', action='store_true')
    ap.add_argument('--revision', help='Compile/import product sources archived from this commit; the probe itself is current.')
    args = ap.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix='atr_architecture_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    global fp, fc, fe
    archive_dir = tempfile.TemporaryDirectory(prefix='atr_architecture_sources_')
    source_root = ROOT
    revision = subprocess.check_output(['git','rev-parse','--verify',(args.revision or 'HEAD') + '^{commit}'], cwd=ROOT,text=True).strip()
    if args.revision:
        source_root = Path(archive_dir.name)
        paths = ['AFrame/engine', 'AFrame/src/AFramePlugin/FrameSettings.cs', 'AFrame/tools/roles/RolesDump.cs']
        for dependency in ('AFrame/src/AFramePlugin/FrameSolutionSelection.cs',
                           'AFrame/src/AFramePlugin/FrameProjectParameters.cs'):
            if subprocess.run(['git', 'cat-file', '-e', revision + ':' + dependency], cwd=ROOT,
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
                paths.append(dependency)
        archive = subprocess.check_output(['git', 'archive', '--format=tar', revision] + paths, cwd=ROOT)
        with tarfile.open(fileobj=io.BytesIO(archive),mode='r:') as tf:
            tf.extractall(source_root,filter='data')
    sys.path.insert(0,str(source_root/'AFrame/engine'))
    fp, fc, fe = (importlib.import_module(n) for n in ('frame_plan','frame_calc','frame_engine'))
    if not shutil.which('mcs') or not shutil.which('mono'):
        print('BLOCKED: mcs and mono required, no success claimed')
        return 2
    roles = []
    for clad in ('porcelain', 'composite', 'concrete', 'clinker'):
        for sub in ('vertical', 'interfloor', 'ortho'):
            for steps in ('calc', 'manual'):
                roles.append(role(f'{clad}_{sub}_{steps}', [['Cladding', clad], ['SetSubType', sub], ['Steps', steps], ['Mode', 'frame']]))
    for profile in ('Авто', 'ГП-40-40', 'ШП-60-20', 'ГП-60-40'):
        roles.append(role('profile_' + profile, [['Profile', profile], ['Mode', 'frame'], ['NaMax', 565]]))
    for mode in ('all', 'clamps'):
        roles.append(role('composite_' + mode, [['Cladding', 'composite'], ['Mode', mode]]))
    roles += [role('nsp2_calc', [['SetSubType', 'interfloor'], ['Profile', 'НСП-2']]),
              role('nsp2_manual', [['SetSubType', 'interfloor'], ['Profile', 'НСП-2'], ['Steps', 'manual']]),
              role('interfloor_clamps', [['SetSubType', 'interfloor'], ['Mode', 'clamps']]),
              role('interfloor_invalid_height', [['SetSubType', 'interfloor'], ['Height', 0]]),
              role('gp60_manual', [['Profile','ГП-60-40'],['Steps','manual'],['Mode','frame']]),
              role('gp60_clamps', [['Profile','ГП-60-40'],['Mode','clamps']]),
              role('weight_preserved', [['QClad', 42], ['SetSubType', 'interfloor'], ['SetSubType', 'ortho'], ['SetSubType', 'vertical']])]
    (out / 'native_roles.json').write_text(json.dumps(roles, ensure_ascii=False, indent=2), encoding='utf-8')
    exe = out / 'ArchitectureSettings.exe'
    sources = [source_root / 'AFrame/tools/roles/RolesDump.cs', source_root / 'AFrame/src/AFramePlugin/FrameSettings.cs']
    for name in ('FrameSolutionSelection.cs', 'FrameProjectParameters.cs'):
        dependency = source_root / 'AFrame/src/AFramePlugin' / name
        if dependency.exists():
            sources.append(dependency)
    cp = subprocess.run(['mcs', '-nologo', '-r:System.Web.Extensions.dll', '-out:' + str(exe)] + [str(p) for p in sources], capture_output=True, text=True)
    (out / 'compile.log').write_text(cp.stdout + cp.stderr, encoding='utf-8')
    if cp.returncode:
        print(cp.stdout + cp.stderr)
        return 2
    subprocess.run(['mono', str(exe), str(out / 'native_roles.json'), str(out / 'native_params.json')], check=True)
    native = {r['name']: r for r in json.loads((out / 'native_params.json').read_text(encoding='utf-8'))}
    cases = []
    evidence = {}
    def check(name, passed, detail=None):
        cases.append(dict(name=name, passed=bool(passed), detail=detail))
    def run(name, request):
        result = fe.op_frame(copy.deepcopy(request))
        evidence[name] = dict(request=request, result=result)
        return result
    def empty_geometry(result):
        return all(not result.get(key) for key in ('rails', 'hrails', 'brackets', 'clamps', 'fittings'))
    def static_screen(result, status):
        model = (result.get('calc_report', {}).get('static_model', {}) if result['ok']
                 else result.get('static_model', {}))
        return (model.get('status') == 'not_verified'
                and model.get('geometric_screening', {}).get('status') == status
                and model.get('pieces_merged') is False
                and model.get('fixed_sliding') == 'not_modeled'
                and model.get('splice_continuity') == 'not_modeled'
                and (result['ok'] or 'calc_report' not in result))
    def refused_for(result, code, reason):
        reasons = result.get('static_model', {}).get('geometric_screening', {}).get('reasons', [])
        return (not result['ok'] and result.get('error_code') == code and empty_geometry(result)
                and static_screen(result, 'refused') and bool(reasons)
                and all(item.get('reason') == reason for item in reasons))
    check('native_roundtrip_all', all(r['roundtrip'] and not r['apply_error'] for r in native.values()))
    check('previous_fix_weight_is_42', native['weight_preserved']['params']['calc']['q_clad'] == 42)
    check('previous_fix_composite_all_clamps_refused', all(native['composite_' + mode]['valid'] for mode in ('all', 'clamps')))
    check('previous_fix_nsp2_calc_refused_manual_allowed', native['nsp2_calc']['valid'] and native['nsp2_manual']['valid'] is None)
    check('material_gamma_native', all(native[f'{c}_vertical_calc']['params']['calc']['gamma_clad'] == (1.2 if c == 'composite' else 1.1) for c in ('porcelain','composite','concrete','clinker')))
    check('interfloor_native_early_model_refusal', all(
        'не подтверждены' in (native[f'{c}_interfloor_calc']['valid'] or '')
        and 'неподвижные/подвижные' in native[f'{c}_interfloor_calc']['describe']
        for c in ('porcelain', 'composite', 'concrete', 'clinker')))
    check('interfloor_invalid_numeric_precedes_model_refusal',
          native['interfloor_invalid_height']['valid'] == 'Высота здания — от 1 до 500 м.')
    # Existing supported path. No source-code copies or hand-built substitute settings.
    base = inputs(native['porcelain_vertical_calc']['params'])
    baseline = run('supported_vertical', base)
    check('supported_vertical_positive', baseline['ok'] and baseline['calc_report']['layout_verification']['passed'])
    check('vertical_static_model_not_verified', static_screen(baseline, 'passed'))
    req = inputs(native['porcelain_interfloor_calc']['params']); req.pop('corners_x')
    normal_inter = run('interfloor_model_unconfirmed', req)
    check('interfloor_model_typed_empty_refusal', refused_for(normal_inter, 'E_CALC_MODEL_UNCONFIRMED',
                                                          'interfloor_model_unconfirmed'), normal_inter.get('error'))
    req = inputs(native['porcelain_ortho_calc']['params'])
    normal_ortho = run('supported_ortho', req)
    check('supported_ortho_positive', normal_ortho['ok'] and normal_ortho['calc_report']['scheme'] == 'ortho')
    check('ortho_static_model_not_verified', static_screen(normal_ortho, 'passed'))
    # Separate emitted pieces must satisfy the existing coefficient span class.
    # These native requests retain the 3000 mm calculation length and shorten
    # only the actual facade, exposing support-count errors without new norms.
    for sub in ('vertical', 'ortho'):
        for height, reason in ((600, 'insufficient_supports'), (1000, 'single_span_unsupported'),
                               (1800, 'span_class_mismatch')):
            req = inputs(native[f'porcelain_{sub}_calc']['params'])
            req['contours'][0]['pts'] = [[0, 0], [6000, 0], [6000, height], [0, height]]
            # One row zone makes the expected refusal independent of corner spacing.
            req['corners_x'] = []
            result = run(f'{sub}_{height}_topology', req)
            check(f'{sub}_{height}_{reason}', refused_for(result, 'E_CALC_TOPOLOGY_UNSUPPORTED', reason),
                  result.get('unsupported'))
        req = inputs(native[f'porcelain_{sub}_manual']['params'])
        manual = run(sub + '_manual', req)
        check(sub + '_manual_kept', native[f'porcelain_{sub}_manual']['valid'] is None
              and manual['ok'] and manual['rails'] and 'calc_report' not in manual)
    manual_inter = run('interfloor_manual', inputs(native['porcelain_interfloor_manual']['params']))
    check('interfloor_manual_kept', native['porcelain_interfloor_manual']['valid'] is None
          and manual_inter['ok'] and manual_inter['rails'] and manual_inter['hrails']
          and 'calc_report' not in manual_inter)
    clamps_inter = inputs(native['interfloor_clamps']['params'])
    clamps_inter['rails_fixed'] = manual_inter.get('rails', [])
    inter_clamps = run('interfloor_clamps', clamps_inter)
    check('interfloor_clamps_kept', native['interfloor_clamps']['valid'] is None
          and inter_clamps['ok'] and 'calc_report' not in inter_clamps)
    # Mismatch through the existing public system+sub_type protocol, without a forged profile.
    mixed = copy.deepcopy(base); mixed['sub_type'] = 'interfloor'
    mixed_result = run('mixed_system_scheme', mixed)
    check('mixed_system_scheme_refused', not mixed_result['ok'], mixed_result.get('error'))
    if mixed_result['ok']:
        rep = mixed_result['calc_report']; proper = {}
        for zone, interval in rep['layout_verification']['zones'].items():
            if interval['max_step'] is not None:
                proper[zone] = fc.calc_chain(dict(rep['inputs'], profile=rep['profile'][zone], scheme='interfloor'), interval['max_step'], zone)
        evidence['mixed_system_scheme']['recheck_actual_interfloor'] = proper
    mixed = inputs(native['porcelain_interfloor_calc']['params']); mixed['calc']['scheme'] = 'vertical'
    wrong_override = run('explicit_wrong_calc_scheme', mixed)
    check('explicit_wrong_calc_scheme_refused', not wrong_override['ok'], wrong_override.get('error'))
    direct = inputs(native['porcelain_interfloor_calc']['params']); direct.pop('corners_x'); direct['calc']['scheme'] = 'interfloor_direct'
    direct_result = run('type5_not_interfloor_geometry', direct)
    check('type5_geometry_refused', not direct_result['ok'] and direct_result.get('error_code') == 'E_CALC_SCHEME_MISMATCH' and not any(direct_result.get(k) for k in ('rails','hrails','brackets','clamps')), direct_result.get('error'))
    # This profile selection and anchor resistance are both available in the native form.
    mass_request = inputs(native['profile_ШП-60-20']['params'])
    mass_result = run('native_shp_mass', mass_request)
    check('native_shp_positive', mass_result['ok'], mass_result.get('error'))
    if mass_result['ok']:
        rep = mass_result['calc_report']
        checks = recheck_mass(mass_result)
        evidence['native_shp_mass']['recheck_actual_mass'] = checks
        check('native_selected_profile_mass', all(rep['inputs']['q_rails'] >= fc.PROFILES[p]['q'] for p in rep['profile'].values()), {'q_rails':rep['inputs']['q_rails'],'profiles':rep['profile']})
        check('native_actual_spacing_with_actual_mass_passes', all(c['passed'] for c in checks.values()))
    # Profile candidates must each carry their own mass before their step is accepted.
    reference = dict(baseline['calc_report']['inputs'], auto_profile=False)
    for profile in ('ГП-40-40-1,2','ШП-60-20-1,2','ШП-60-20-20-1,2'):
        request = dict(reference, profile=profile, q_rails=0.745, na_max=565)
        actual = fc.calc_chain(request, 300, 'row')
        oracle = fc.calc_chain(dict(request, q_rails=fc.PROFILES[profile]['q']), 300, 'row')
        check('fixed_profile_mass_' + profile, actual == oracle)
        _name, step, chain, variants = fc.pick_profile_step(dict(request, auto_profile=True), 'row', [profile])
        corrected = fc.calc_chain(dict(request, q_rails=fc.PROFILES[profile]['q']), step, 'row') if step is not None else None
        check('candidate_mass_' + profile, chain == corrected and (step is None or corrected['passed']))
    seed_probe = dict(reference, q_rails=0.745, na_max=470, auto_profile=True,
                      profile_candidates=['ГП-40-40-1,2'])
    seed_gp = fc.report(dict(seed_probe,profile='ГП-40-40-1,2'))
    seed_shp = fc.report(dict(seed_probe,profile='ШП-60-20-1,2'))
    check('single_candidate_mass_independent_of_unused_seed', seed_gp['row'] == seed_shp['row'] and seed_gp['row']['step'] is not None,
          {'gp_seed_step':seed_gp['row']['step'],'shp_seed_step':seed_shp['row']['step']})
    passing_seed = dict(seed_probe,na_max=600)
    passing_gp = fc.report(dict(passing_seed,profile='ГП-40-40-1,2'))
    passing_shp = fc.report(dict(passing_seed,profile='ШП-60-20-1,2'))
    check('both_zone_candidates_independent_of_seed', passing_gp == passing_shp
          and all(passing_gp[z]['step'] is not None for z in ('row','corner')))
    heavy = dict(reference, q_rails=5, profile='ГП-40-40-1,2')
    check('explicit_heavier_mass_preserved', fc.resolved_inputs(heavy)['q_rails'] >= 5)
    if normal_ortho['ok']:
        known_mass = fc.PROFILES['ШП-60-20-1,2']['q'] + fc.PROFILES['ГП-40-40-1,2']['q']
        check('ortho_real_shp_and_horizontal_gp_mass', normal_ortho['calc_report']['inputs']['q_rails'] >= known_mass,
              {'actual':normal_ortho['calc_report']['inputs']['q_rails'],'required_catalog_sum':known_mass})
    # A rejected geometry may expose arithmetic inputs for diagnostics; this
    # mass check must never claim that the interfloor static model passed.
    inter_inputs = normal_inter.get('calc_inputs', {})
    check('interfloor_refused_input_mass_not_reduced', inter_inputs.get('q_rails', 0) >= 3.71
          and inter_inputs.get('q_rails_extra', 0) >= 1.4)
    # Source-contract observations; handled by the independent source-contract agent.
    for name, changes in [('unknown_system_string',dict(system='NordFOX')),('unknown_system_dict',dict(system={'name':'NordFOX'})),('unknown_sub_type',dict(sub_type='nordfox'))]:
        req = copy.deepcopy(base); req.update(changes)
        if name == 'unknown_system_dict': req.pop('calc',None)
        run(name, req)
    unknown_profile = copy.deepcopy(base); unknown_profile['rail_profile'] = 'UNCONFIRMED'
    run('unknown_profile', unknown_profile)
    gp60 = inputs(native['profile_ГП-60-40']['params']); gp60_result = run('unconfirmed_gp60',gp60)
    check('gp60_calc_typed_refusal', not gp60_result['ok'] and gp60_result.get('error_code') == 'E_PROFILE_SECTION_UNCONFIRMED'
          and not any(gp60_result.get(k) for k in ('rails','hrails','brackets','clamps')))
    check('gp60_native_early_refusal', bool(native['profile_ГП-60-40']['valid']))
    gp60_manual = run('gp60_manual',inputs(native['gp60_manual']['params']))
    check('gp60_manual_kept', native['gp60_manual']['valid'] is None and gp60_manual['ok'] and gp60_manual['rails']
          and all(r['profile'] == 'ГП-60-40' for r in gp60_manual['rails']) and 'calc_report' not in gp60_manual)
    clamps = inputs(native['gp60_clamps']['params']); clamps['rails_fixed'] = baseline['rails']
    clamps_result = run('gp60_clamps',clamps)
    check('gp60_clamps_kept', native['gp60_clamps']['valid'] is None and clamps_result['ok'] and 'calc_report' not in clamps_result)
    for name, changes in [('unknown_mark', {'rail_profile':'UNCONFIRMED'}),
                          ('wrong_family_profile', {'calc':dict(base['calc'],profile='НСП-69-60-1,2',auto_profile=False)}),
                          ('wrong_family_candidate', {'calc':dict(base['calc'],profile_candidates=['НСП-69-60-1,2'])})]:
        request = copy.deepcopy(base); request.update(changes)
        result = run(name,request)
        check(name + '_typed_refusal', not result['ok'] and result.get('error_code') == 'E_PROFILE_SECTION_UNCONFIRMED'
              and not any(result.get(k) for k in ('rails','hrails','brackets','clamps')))
    for profile in ('ГП-40-40-1,2','ШП-60-20-1,2','ШП-60-20-20-1,2'):
        request = copy.deepcopy(base); request['rail_profile'] = profile
        result = run('known_exact_' + profile,request)
        check('known_exact_' + profile, result['ok'] and result['rails'] and all(r['profile'] == profile for r in result['rails']))
    all_sources = sources + [source_root/'AFrame/engine/frame_plan.py', source_root/'AFrame/engine/frame_calc.py', source_root/'AFrame/engine/frame_engine.py', source_root/'AFrame/engine/systems.json']
    topology_source = source_root/'AFrame/engine/frame_topology.py'
    if topology_source.exists():
        all_sources.append(topology_source)
    summary = {'mode':'observation' if args.observe else 'regression', 'scope':'Actual native FrameSettings and Python engines; no AutoCAD host or engineering certification.',
               'source_revision':revision, 'source_kind':'git_archive' if args.revision else 'working_tree',
               'source_sha256':{str(p.relative_to(source_root)):digest(p) for p in all_sources},
               'probe_sha256':digest(Path(__file__).resolve()),
               'total':len(cases),'passed':sum(c['passed'] for c in cases),'failed':sum(not c['passed'] for c in cases),'cases':cases}
    (out/'evidence.json').write_text(json.dumps(evidence,ensure_ascii=False,indent=2),encoding='utf-8')
    (out/'manifest.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:summary[k] for k in ('mode','total','passed','failed')},ensure_ascii=False))
    for c in cases:
        if not c['passed']: print('FAIL:',c['name'],c.get('detail'))
    print('Reports:',out)
    return 0 if args.observe or not summary['failed'] else 1

if __name__ == '__main__':
    sys.exit(main())
