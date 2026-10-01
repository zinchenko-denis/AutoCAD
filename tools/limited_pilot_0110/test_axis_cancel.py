"""Execute the exact ATFRAME no-layout axis block with real FrameSettings.

Prompt statuses are explicit CAD doubles. Only continued cases run the real
Python engine. This is not the full command, a CAD transaction or native Esc.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
for directory in ('AFrame/engine', 'tools/quantities_3009'):
    sys.path.insert(0, str(ROOT / directory))
import frame_engine
from test_frame_solution_selection import example_selection
from probe_runtime import available, command, compile_probe


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + '\n', encoding='utf-8')


def extract(source):
    prefix = '            bool clampsOnly = fs.ClampsOnly;\n\n'
    start_marker = '            if (joints.Count == 0)\n'
    end_marker = '            if (oldMeta > 0)\n'
    assert source.count(prefix + start_marker) == 1, 'axis block must be unique'
    start = source.index(prefix + start_marker) + len(prefix)
    end = source.index(end_marker, start)
    # Ignore braces in C# comments/strings when proving that the whole block is
    # directly in RunCore, not in a lambda/local method whose return is weaker.
    token = r'//[^\n]*|/\*.*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\''
    masked = re.sub(token, lambda m: ''.join('\n' if c == '\n' else ' ' for c in m[0]), source, flags=re.S)
    method = source.index('private void RunCore(')
    body = masked.index('{', method)
    depth = lambda pos: masked[body:pos].count('{') - masked[body:pos].count('}')
    level = 0
    for method_end in range(body, len(masked)):
        level += (masked[method_end] == '{') - (masked[method_end] == '}')
        if level == 0:
            break
    assert level == 0, 'complete RunCore body must be found'
    assert depth(start) == depth(end) == 1, 'axis segment must be directly within RunCore'
    assert masked[start:end].count('{') == masked[start:end].count('}'), 'whole axis block only'
    rows_start = source.index('            if (fs.Mode == "frame" && !fs.IsTile)', end)
    rows_end = source.index('                rowsY.Clear();\n', rows_start) + len('                rowsY.Clear();\n')
    assert depth(rows_start) == depth(rows_end) == 1, 'row cleanup must be directly within RunCore'
    markers = [start, end, rows_start, source.index('FacadeQuantityStore.CaptureFrameSources(', end),
               source.index('res = ser.DeserializeObject(CallEngine(', end),
               source.index('using (doc.LockDocument())', end),
               source.index('projectOperation.VerifyFresh(', end)]
    assert markers == sorted(markers), 'axes return must precede source capture, engine and CAD prewrite'
    assert all(body < pos < method_end and depth(pos) >= 1 for pos in markers), 'source boundaries stay in RunCore'
    return source[start:end], source[rows_start:rows_end], dict(zip(('axis_start', 'axis_end_exclusive', 'frame_rows_clear', 'source_capture', 'engine_call',
        'cad_write_lock', 'fresh_guard'), [source.count('\n', 0, pos) + 1 for pos in markers]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--source', type=Path, default=ROOT / 'AFrame/src/AFramePlugin/FrameCommand.cs',
                        help='Optional saved pre-fix FrameCommand.cs, for exact baseline replay')
    parser.add_argument('--expect', choices=('fixed', 'broken'), default='fixed')
    args = parser.parse_args()
    if not available():
        print('BLOCKED: native .NET compiler/runtime unavailable; axis cancellation NOT_RUN')
        return 2
    out = args.out.resolve(); build = out / 'build'; build.mkdir(parents=True, exist_ok=True)
    source = args.source.resolve(); source_before = digest(source)
    segment, rows_segment, boundaries = extract(source.read_text(encoding='utf-8'))
    template = Path(__file__).with_name('AxisCancelProbe.cs')
    assert template.read_text(encoding='utf-8').count('/* ACTUAL_AXIS_SEGMENT */') == 1
    assert template.read_text(encoding='utf-8').count('/* ACTUAL_ROWS_MODE_SEGMENT */') == 1
    generated = build / 'AxisCancelExtracted.cs'
    generated.write_text(template.read_text(encoding='utf-8').replace('/* ACTUAL_AXIS_SEGMENT */', segment)
                         .replace('/* ACTUAL_ROWS_MODE_SEGMENT */', rows_segment), encoding='utf-8')
    (build / 'actual_axis_segment.txt').write_text(segment, encoding='utf-8')
    (build / 'actual_rows_mode_segment.txt').write_text(rows_segment, encoding='utf-8')
    src = ROOT / 'AFrame/src/AFramePlugin'
    actual = [src / name for name in ('FrameSettings.cs', 'FrameSolutionSelection.cs', 'FrameProjectParameters.cs')]
    exe = build / 'AxisCancelProbe.exe'
    compiled = compile_probe([generated, *actual], ['System.Web.Extensions'], exe)
    (build / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    checks = []
    def need(value, reason):
        if not value:
            raise AssertionError(reason)
        checks.append(reason)
    need(compiled.returncode == 0, 'unchanged axis block and actual settings compile')
    point = lambda status, x=0: {'status': status, 'x': x}
    scenarios = [{'name': 'point_cancel', 'points': [point('OK', 600), point('Cancel')]},
        {'name': 'point_none', 'points': [point('OK', 600), point('None')]},
        {'name': 'initial_cancel', 'points': [point('Cancel')]},
        {'name': 'empty_none', 'points': [point('None')]}]
    save(out / 'input.json', {'selection': example_selection(), 'scenarios': scenarios})
    subprocess.run(command(exe) + [str(out / 'input.json'), str(out / 'native_output.json')], check=True)
    native = json.loads((out / 'native_output.json').read_text(encoding='utf-8'))
    need(native['validation_error'] is None and native['settings_roundtrip'], 'actual no-layout settings validate and roundtrip')
    fs = native['settings']
    need(fs['step_main'] == fs['step_corner'] == 800 and fs['axes'] == 'points', 'normal manual800 point axes are used')
    by_name = {r['name']: r for r in native['results']}
    need(set(by_name) == {s['name'] for s in scenarios}, 'all prompt scenarios execute')
    responses = {}
    for name, result in by_name.items():
        expected_continued = name == 'point_none' or (name == 'point_cancel' and args.expect == 'broken')
        need(result['continued'] == expected_continued, 'axis continuation matches ' + args.expect + ': ' + name)
        need(result['remaining_prompts'] == 0 and len(result['prompts']) == len(next(s['points'] for s in scenarios if s['name'] == name)),
             'exact requested prompt sequence consumed: ' + name)
        need(all(p['allow_none'] for p in result['prompts']), 'Enter is explicitly enabled: ' + name)
        need(result['joints'] == ([600] if name.startswith('point_') else []), 'accepted axis values retained only as local input: ' + name)
        need((result['request'] is not None) == expected_continued, 'harness calls EngineParams only after continuation: ' + name)
        if name.endswith('cancel') and args.expect == 'fixed':
            need(any('отмен' in s.lower() for s in result['messages']), 'cancellation is explicitly reported: ' + name)
        if not expected_continued:
            continue
        request = result['request']; before = copy.deepcopy(request)
        need(result['rows_after_axes'] == [605, 1210, 1815, 2420] and request['rows_y'] == result['rows'] == [],
             'actual frame-only continuation clears rows before payload: ' + name)
        need(request['system']['bracket_step'] == request['system']['bracket_step_corner'] == 800,
             'actual EngineParams preserves positive manual steps: ' + name)
        response = frame_engine.run(request)
        need(response.get('ok') is True and request == before, 'actual engine succeeds without input mutation: ' + name)
        need(len(response['rails']) == 1 and len(response['brackets']) == 4, 'continued engine creates one rail and four bracket symbols: ' + name)
        need(response['solution_report']['physical_assignment'] == 'not_asserted' and
             response['solution_report']['calculation_binding'] == 'not_confirmed', 'engine does not grant physical or engineering assignment: ' + name)
        save(out / name / 'request.json', request); save(out / name / 'response.json', response)
        responses[name] = response
    if args.expect == 'broken':
        need(by_name['point_cancel']['request'] == by_name['point_none']['request'], 'baseline Esc produces the same engine request as Enter')
        need(responses['point_cancel'] == responses['point_none'], 'baseline Esc creates the same physical arrays as Enter')
    need(digest(source) == source_before, 'source unchanged during extraction and execution')
    need(boundaries['axis_end_exclusive'] < boundaries['source_capture'] < boundaries['engine_call'] < boundaries['cad_write_lock'],
         'actual RunCore returns from axes before engine and CAD prewrite; source structure checked')
    dependencies = {Path(__file__).resolve(), template, *actual, ROOT / 'tools/quantities_3009/probe_runtime.py', ROOT / 'AFrame/engine/systems.json'}
    dependencies.update((ROOT / 'AFrame/engine').glob('*.py'))
    # A saved baseline is explicitly separate from the current repository binding.
    repository_sources = {str(p.relative_to(ROOT)): digest(p) for p in sorted(dependencies) if p.is_relative_to(ROOT)}
    if source == ROOT / 'AFrame/src/AFramePlugin/FrameCommand.cs':
        repository_sources['AFrame/src/AFramePlugin/FrameCommand.cs'] = source_before
    artifacts = [p for p in out.rglob('*.json') if 'build' not in p.relative_to(out).parts and p.name != 'manifest.json']
    save(out / 'manifest.json', {'schema': 'frame_axis_cancel_regression/1',
        'verification': {'status': 'PASS', 'expected_behavior': args.expect, 'checks': len(checks), 'assertions': checks},
        'defect_reproduced': args.expect == 'broken', 'engineering_assessment': 'BLOCKED', 'live_autocad_checked': False,
        'full_command_execution': 'NOT_RUN', 'cad_transaction_rollback': 'NOT_RUN',
        'scope': 'Exact no-layout axis segment plus actual FrameSettings with prompt doubles; real engine invoked by harness only for continued cases.',
        'source_control_flow': boundaries, 'command_source': {'path': str(source), 'sha256': source_before},
        'generated_probe_source': {'path': str(generated.relative_to(out)), 'sha256': digest(generated)},
        'actual_segment_sha256': hashlib.sha256(segment.encode('utf-8')).hexdigest(),
        'continuation_rows_segment_sha256': hashlib.sha256(rows_segment.encode('utf-8')).hexdigest(),
        'source_sha256': repository_sources,
        'artifact_sha256': {str(p.relative_to(out)): digest(p) for p in sorted(artifacts)},
        'counts': {name: {'continued': result['continued'], 'axis_count': len(result['joints']),
            'real_engine_invoked_by_harness': name in responses,
            'rails': len(responses[name]['rails']) if name in responses else 0,
            'brackets': len(responses[name]['brackets']) if name in responses else 0} for name, result in by_name.items()}})
    print('Axis cancellation:', len(checks), 'PASS; expected', args.expect, '; full command/AutoCAD NOT_RUN; engineering BLOCKED')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
