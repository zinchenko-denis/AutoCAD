"""Reproduce unresolved shared support candidates through actual engine/producer/table.

All dimensions are synthetic geometry inputs, not permissible mounting rules.
Original passports and statuses are retained; no CAD host is exercised.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]


def setup(root):
    global ROOT
    ROOT = Path(root).resolve()
    for path in ('tools/quantities_3009', 'tools/catalog_connections_0110', 'AFrame/engine'):
        sys.path.insert(0, str(ROOT / path))


def make_fixture(out, pieces=2, zone_count=2, gap=0, producer=None):
    """No output patching: all shared candidate IDs come from frame_engine.run."""
    setup(ROOT)
    import frame_engine
    from test_frame_solution_selection import example_selection
    from test_connection_store import compile_actual_producer
    from probe_runtime import command
    out = Path(out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    height = pieces * 1500 + max(0, pieces - 1) * gap
    request = {'op': 'frame', 'system': {'name': 'Вектор-1', 'rail_std': 1500,
        'rail_gap': gap, 'bracket_start_offset': 0, 'bracket_step': 1500},
        'sub_type': 'vertical', 'cladding': 'porcelain', 'parts': 'frame',
        'solution_selection': example_selection(), 'joints_x': [600],
        'zones': [{'zone_id': 'Z%d' % i, 'zone': {'contour': {'pts':
            [[0, 0], [1200, 0], [1200, height], [0, height]]}, 'openings': []}}
            for i in range(zone_count)]}
    response = frame_engine.run(copy.deepcopy(request))
    assert response['ok'], response
    assert response['solution_report']['physical_assignment'] == 'not_asserted'
    assert response['connection_passport']['coverage']['fixed_sliding'] == 'not_modeled'
    payload = {'request': request, 'response': response, 'mapping': {}}
    input_path, output_path = out / 'fixture_input.json', out / 'fixture_output.json'
    input_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    executable = producer or compile_actual_producer(out / 'producer')[0]
    subprocess.run(command(executable) + ['--produce', str(input_path), str(output_path)], check=True)
    produced = json.loads(output_path.read_text(encoding='utf-8'))
    assert produced['ok'], produced
    report_path = out / 'fixture_report.json'
    report_path.write_text(json.dumps(produced['report'], ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return report_path


def compile_table(out):
    setup(ROOT)
    from probe_runtime import compile_probe
    out = Path(out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    sources = [ROOT / 'tools/catalog_connections_0110/ConnectionTableProbe.cs', ROOT / 'Common/FacadeQuantities.cs']
    sources += [ROOT / 'Facades/src/AFacadesPlugin' / name for name in (
        'ConnectionTableData.cs', 'QuantityTableIdentity.cs', 'QuantityTableView.cs', 'QuantityTableView.Connections.cs',
        'CladdingTableData.cs', 'FrameTableData.cs', 'QuantityXlsxFile.cs', 'XlsxWriter.cs')]
    exe = out / 'ConnectionTableProbe.exe'
    compiled = compile_probe(sources, ['System.Web.Extensions', 'System.Xml', 'System.IO.Compression', 'System.IO.Compression.FileSystem'], exe)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    assert compiled.returncode == 0, compiled.stdout + compiled.stderr
    return exe, sources


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--repo', type=Path, default=ROOT)
    parser.add_argument('--fixture', type=Path)
    parser.add_argument('--expect', choices=('baseline', 'fixed'), required=True)
    args = parser.parse_args()
    setup(args.repo)
    from probe_runtime import command
    from openpyxl import load_workbook
    out = args.out.resolve(); out.mkdir(parents=True, exist_ok=True)
    report_path = args.fixture.resolve() if args.fixture else make_fixture(out / 'shared_support')
    report = json.loads(report_path.read_text(encoding='utf-8'))
    links = {}
    for passport in report['connection_passports']:
        for member in passport['members']:
            for support in member['supports']:
                for bracket in support['bracket_element_ids']:
                    links.setdefault(bracket, []).append(member['rail_element_id'])
    shared = {key: value for key, value in links.items() if len(value) > 1}
    assert len(shared) == 4 and all(len(value) == 2 for value in shared.values())
    exe, sources = compile_table(out)
    xlsx = out / 'shared_support.xlsx'
    run = subprocess.run(command(exe) + ['--report', str(report_path), str(xlsx)], text=True, capture_output=True)
    (out / 'table.log').write_text(run.stdout + run.stderr, encoding='utf-8')
    assert run.returncode == 0, run.stdout + run.stderr
    rows = list(load_workbook(xlsx)['Соединения'].values)
    messages = [row[0] for row in rows if isinstance(row[0], str) and row[0].startswith('Монтажная принадлежность не определена: кронштейн ')]
    flagged = [row for row in rows if len(row) > 10 and isinstance(row[10], str) and 'Монтажная принадлежность не определена: общих кандидатов-кронштейнов — ' in row[10]]
    assert len(messages) == (0 if args.expect == 'baseline' else 4), messages
    assert len(flagged) == (0 if args.expect == 'baseline' else 4), flagged
    for bracket, rail_ids in shared.items():
        if args.expect == 'fixed':
            matches = [message for message in messages if 'кронштейн ' + bracket + ' —' in message]
            assert len(matches) == 1 and all(rail in matches[0] for rail in rail_ids)
    manifest = {'status': 'REPRODUCED' if args.expect == 'baseline' else 'PASS',
        'expected': args.expect, 'physical_members': 4, 'shared_brackets': shared,
        'addressed_messages': len(messages), 'flagged_members': len(flagged),
        'report_path': str(report_path), 'report_sha256': hashlib.sha256(report_path.read_bytes()).hexdigest(),
        'path': 'actual frame_engine -> actual FrameQuantities producer -> common validation -> table -> XLSX',
        'engineering_result': 'mounting_not_confirmed; fixed/sliding and continuity not inferred',
        'live_autocad_checked': False,
        'source_sha256': {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in sources}}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(manifest['status'], 'shared brackets:', len(shared), 'addressed messages:', len(messages), 'flagged members:', len(flagged))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
