#!/usr/bin/env python3
"""Independent arithmetic review of the facade mass fix; synthetic evidence only.

First run architecture_probe.py --out DIR, then pass its evidence.json here.
No AutoCAD, source mutation, or private engineering file is involved.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'AFrame/engine'))
import frame_calc as fc


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--evidence', type=Path, required=True)
    ap.add_argument('--out', type=Path, required=True)
    a = ap.parse_args()
    evidence = json.loads(a.evidence.read_text(encoding='utf-8'))
    checks = []

    def check(name, ok, actual=None):
        checks.append(dict(name=name, ok=bool(ok), actual=actual))
        if not ok:
            raise AssertionError(name)

    for name in ('supported_vertical', 'supported_interfloor', 'supported_ortho', 'native_shp_mass'):
        result = evidence[name]['result']
        check(name + ': successful engine result', result['ok'])
        rep = result['calc_report']
        inp = rep['inputs']
        step = rep['steps']['main']
        # Recompute Np directly from the published chain, without calling
        # resolved_inputs/calc_chain as the arithmetic oracle.
        if inp['scheme'] == 'interfloor':
            width, height = step / 1000.0, inp['rail_len'] / 1000.0
        else:
            width = inp['b_row'] / 1000.0
            height = (inp['v_step'] if inp['scheme'] == 'ortho' else step) / 1000.0
        expected = round((inp['q_clad'] * inp['gamma_clad'] * width +
                          inp['q_rails'] * 1.05) * height, 1)
        check(name + ': rail gamma 1.05 applied once', rep['row']['n_p'] == expected,
              dict(q_rails=inp['q_rails'], Np=rep['row']['n_p'], expected=expected))
    ortho = evidence['supported_ortho']['result']['calc_report']['inputs']
    check('ortho emitted ShP plus horizontal GP mass', ortho['q_rails'] >= 1.22 + 0.745,
          ortho['q_rails'])
    inter = evidence['supported_interfloor']['result']['calc_report']['inputs']
    check('interfloor retains project total 3.71 and horizontal minimum 1.4',
          inter['q_rails'] >= 3.71 and inter['q_rails_extra'] >= 1.4)
    base = dict(evidence['supported_vertical']['result']['calc_report']['inputs'],
                q_rails=0.745, auto_profile=True, profile_candidates=['ГП-40-40-1,2'])
    low = fc.report(dict(base, profile='ГП-40-40-1,2', na_max=470))
    high = fc.report(dict(base, profile='ШП-60-20-1,2', na_max=470))
    check('row candidate GP independent of seed GP/ShP at 470 N', low['row'] == high['row'],
          dict(GP=low['row']['step'], ShP=high['row']['step']))
    low = fc.report(dict(base, profile='ГП-40-40-1,2', na_max=600))
    high = fc.report(dict(base, profile='ШП-60-20-1,2', na_max=600))
    check('successful two-zone report independent of seed at 600 N', low == high)
    check('explicit project total 5 preserved',
          fc.resolved_inputs(dict(base, profile='ГП-40-40-1,2', q_rails=5))['q_rails'] == 5)
    manifest = dict(passed=len(checks), failed=0, checks=checks,
                    source_sha256={str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                                   for p in [ROOT / 'AFrame/engine/frame_calc.py', ROOT / 'AFrame/engine/frame_plan.py']})
    a.out.parent.mkdir(parents=True, exist_ok=True)
    a.out.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(manifest, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
