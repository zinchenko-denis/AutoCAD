"""Measure only Python frame-engine work; no producer, CAD API, table or UI.

Elapsed times are descriptive. Stable count assertions protect the scenario;
they are not an AutoCAD latency promise or a hard CI time threshold.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import time

from engine_oracles import ARRAYS, ROOT, frame_engine, rect, zone


def base_request(mode):
    request = {'op': 'frame', 'system': 'Вектор-1', 'sub_type': 'vertical',
               'joints_x': [i * 608.0 + 304 for i in range(8)], 'floors_y': [3000],
               'rows_y': [605 * i for i in range(1, 10)]}
    if mode == 'limited_calc':
        request['calc'] = {'wind_region': 'II', 'terrain': 'B', 'height': 41.2, 'q_clad': 25,
                           'offset': 230, 'na_max': 1880, 'profile': 'ШП-60-20-20-1,2', 'q_rails': 1.21}
    return request


def one_zone(index):
    x = index * 10000.0
    return zone('Z' + str(index), rect(x, 0, 5000, 6000), joints_x=[x + i * 608.0 + 304 for i in range(8)])


def worker(target, mode):
    request = base_request(mode)
    sample = frame_engine.run(dict(request, zones=[one_zone(0)]))
    assert sample['ok'], sample.get('error')
    count_per_zone = sum(len(sample[key]) for key in ARRAYS)
    zone_count = math.ceil(target / count_per_zone)
    started = time.perf_counter()
    request['zones'] = [one_zone(index) for index in range(zone_count)]
    input_seconds = time.perf_counter() - started
    started = time.perf_counter()
    result = frame_engine.run(request)
    elapsed = time.perf_counter() - started
    assert result['ok'], result.get('error')
    counts = {key: len(result[key]) for key in ARRAYS}
    actual_count = sum(counts.values())
    assert actual_count == zone_count * count_per_zone
    assert len(result['per_zone']) == zone_count
    if mode == 'limited_calc':
        assert len(result['calc_reports']) == zone_count
        assert all(item['report']['static_model']['status'] == 'not_verified' for item in result['calc_reports'])
    try:
        import resource
        peak_rss_bytes = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024
    except ImportError:
        peak_rss_bytes = None
    return {'target_elements': target, 'actual_elements': actual_count, 'zones': zone_count,
            'mode': mode, 'counts': counts, 'input_build_seconds': input_seconds,
            'engine_seconds': elapsed, 'peak_process_rss_bytes': peak_rss_bytes,
            'memory_scope': 'child process including Python runtime, input request, engine result and calibration sample',
            'static_model_verified': False, 'live_autocad_checked': False}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--out', type=Path)
    ap.add_argument('--targets', default='10000,50000,150000')
    ap.add_argument('--worker', type=int)
    ap.add_argument('--mode', choices=['manual', 'limited_calc'], default='manual')
    args = ap.parse_args()
    if args.worker is not None:
        print(json.dumps(worker(args.worker, args.mode), ensure_ascii=False))
        return 0
    out = (args.out or Path(tempfile.mkdtemp(prefix='frame_engine_perf_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    targets = [int(value) for value in args.targets.split(',')]
    results = []
    for mode in ('manual', 'limited_calc'):
        for target in targets:
            completed = subprocess.run([sys.executable, str(Path(__file__).resolve()), '--worker', str(target), '--mode', mode],
                                       capture_output=True, text=True, check=True)
            result = json.loads(completed.stdout)
            results.append(result)
            print(mode, target, 'actual', result['actual_elements'], 'seconds', round(result['engine_seconds'], 3), flush=True)
    files = [Path(__file__).resolve(), Path(__file__).with_name('engine_oracles.py')]
    files += [path for path in (ROOT / 'AFrame/engine').glob('*.py') if not path.name.startswith('test_')]
    files += [ROOT / 'AFrame/engine/systems.json']
    manifest = {'status': 'PASS', 'scope': 'Python frame engine only; C# aggregation and CAD/store are separately measured',
                'timing_is_descriptive_only': True, 'live_autocad_checked': False,
                'results': results, 'source_sha256': {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
