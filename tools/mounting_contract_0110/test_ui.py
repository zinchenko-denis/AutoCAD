"""Real connection preview: shared physical bracket warnings and native layout.

Consumes the fresh engine -> producer fixture emitted by test_connection_table.
No AutoCAD host is opened; lack of a native display remains BLOCKED.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / 'tools/solution_catalog_0110/test_solution_ui.py'
spec = importlib.util.spec_from_file_location('connection_ui_runner', BASE)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    report = args.report.resolve()
    if not report.is_file():
        parser.error('Fresh shared-support report is required: ' + str(report))
    os.environ['FACADE_CONNECTION_UI_REPORT'] = str(report)
    sys.argv = [sys.argv[0], '--out', str(args.out)]
    sources = [ROOT / 'Common/FacadeQuantities.cs']
    sources += [ROOT / 'Facades/src/AFacadesPlugin' / name for name in (
        'ConnectionTableData.cs', 'QuantityTableView.cs', 'QuantityTableView.Connections.cs',
        'QuantityTablePreview.cs', 'CladdingTableData.cs', 'FrameTableData.cs')]
    result = runner.main(probe=Path(__file__).with_name('ConnectionUiProbe.cs'),
                         runner_path=Path(__file__).resolve(), source_names=sources)
    manifest_path = args.out.resolve() / 'manifest.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    manifest['report_origin'] = 'shared_support_real_engine_to_actual_producer'
    manifest['report_sha256'] = hashlib.sha256(report.read_bytes()).hexdigest()
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return result


if __name__ == '__main__':
    raise SystemExit(main())
