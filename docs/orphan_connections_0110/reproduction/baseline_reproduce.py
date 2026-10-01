"""Replay the measured before-state with the original no-counter C# probe.

Use --repo pointing to a detached/read-only source checkout of 1a6066d.
The current final runner intentionally supports only --expect fixed.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys

HERE = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    repo, out = args.repo.resolve(), args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    recorded = json.loads((HERE / 'baseline_manifest.json').read_text(encoding='utf-8'))
    for relative, expected in recorded['source_sha256'].items():
        actual = hashlib.sha256((repo / relative).read_bytes()).hexdigest()
        assert actual == expected, 'Before-state source differs: ' + relative
    for path in ('AFrame/engine', 'tools/catalog_connections_0110', 'tools/quantities_3009'):
        sys.path.insert(0, str(repo / path))
    import frame_engine
    from test_connection_store import compile_actual_producer
    from probe_runtime import command, compile_probe
    baseline = json.loads((HERE / 'baseline_minimal_input.json').read_text(encoding='utf-8'))
    response = frame_engine.run(copy.deepcopy(baseline['request']))
    assert response == baseline['response'], 'Before-state engine output changed'
    producer, _ = compile_actual_producer(out / 'producer')
    payload = out / 'input.json'
    payload.write_text(json.dumps({'request': baseline['request'], 'response': response, 'mapping': {}}, ensure_ascii=False), encoding='utf-8')
    produced_path = out / 'producer_output.json'
    subprocess.run(command(producer) + ['--produce', str(payload), str(produced_path)], check=True)
    produced = json.loads(produced_path.read_text(encoding='utf-8'))
    assert produced['ok'], produced
    report_path = out / 'report.json'
    report_path.write_text(json.dumps(produced['report'], ensure_ascii=False), encoding='utf-8')
    sources = [HERE / 'BaselineOrphanScopeProbe.cs', repo / 'Common/FacadeQuantities.cs']
    sources += [repo / 'Facades/src/AFacadesPlugin' / name for name in ('ConnectionTableData.cs', 'QuantityTableIdentity.cs',
        'QuantityTableView.cs', 'QuantityTableView.Connections.cs', 'CladdingTableData.cs', 'FrameTableData.cs', 'QuantityXlsxFile.cs', 'XlsxWriter.cs')]
    exe = out / 'BaselineOrphanScopeProbe.exe'
    compiled = compile_probe(sources, ['System.Web.Extensions', 'System.Xml', 'System.IO.Compression', 'System.IO.Compression.FileSystem'], exe)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    assert compiled.returncode == 0, compiled.stdout + compiled.stderr
    subprocess.run(command(exe) + [str(report_path), str(out / 'scope')], check=True)
    observed = json.loads((out / 'scope/cases.json').read_text(encoding='utf-8'))
    actual = next(case for case in observed['cases'] if case['name'] == 'all')
    assert actual['core_ok'] and len(actual['orphan_ids']) == 1 and actual['orphan_ids_shown_in_table'] == [], actual
    result = {'status': 'REPRODUCED', 'expected_source_head': recorded['head'], 'source_hashes_match_recorded_before': True,
        'engine_response_matches_recorded_before': True, 'orphan_count': 1, 'orphan_ids_shown_in_table': 0,
        'original_probe_sha256': hashlib.sha256((HERE / 'BaselineOrphanScopeProbe.cs').read_bytes()).hexdigest(),
        'live_autocad_checked': False}
    (out / 'manifest.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print('REPRODUCED: 1 physical orphan, 0 addressed table identities; original baseline probe')


if __name__ == '__main__':
    main()
