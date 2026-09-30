"""Real frame table model and exact XLSX values, independent openpyxl inspection."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/quantities_3009'))
from probe_runtime import available, command, compile_probe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    if not available():
        print('BLOCKED: native .NET test compiler/runtime unavailable; no table execution claimed')
        return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix='frame_table_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    sources = [Path(__file__).with_name('FrameTableProbe.cs'), ROOT / 'Common/FacadeQuantities.cs']
    sources += [ROOT / 'Facades/src/AFacadesPlugin' / name for name in ('FrameTableData.cs', 'QuantityXlsxFile.cs', 'XlsxWriter.cs')]
    exe = out / 'FrameTableProbe.exe'
    compiled = compile_probe(sources, ['System.Web.Extensions', 'System.Xml', 'System.IO.Compression', 'System.IO.Compression.FileSystem'], exe)
    (out / 'compile.log').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return 1
    subprocess.run(command(exe) + [str(out)], check=True)
    from openpyxl import load_workbook
    book = load_workbook(out / 'frame_exact.xlsx')
    sheet = book['Подсистема']
    rows = list(sheet.values)
    checks = 0

    def check(value, message):
        nonlocal checks
        checks += 1
        assert value, message

    rail = next(row for row in rows if row[0] == 'А' and row[2] == 'Направляющая')
    check(rail[7] == 2500.1234567 and rail[8] == 2 and rail[9] == 5.0002469134, 'Exact installed length or count lost')
    check(rail[4] == 'не указано' and rail[5] == 'не указано', 'Unknown material/coating became guessed text')
    unknown = next(row for row in rows if row[0] == 'Б' and row[2] == 'Направляющая')
    check(unknown[7] == 'не определена' and unknown[9] == 'не определён', 'Unknown member length became zero')
    bracket = next(row for row in rows if row[0] == 'А' and row[2] == 'Кронштейн')
    check(bracket[7] == '—' and bracket[9] == '—' and bracket[8] == 6, 'Piece quantities mixed with metres')
    totals = [row for row in rows if row[0] == 'ИТОГО']
    check(len(totals) == 6 and sum(row[8] for row in totals) == 28, 'Category subtotal units or counts differ')
    check(any(row[2] == 'Оценка хлыстов' and row[8] == 2 for row in rows), 'Separate stock estimate missing')
    check(any(row[2] == 'Оценка хлыстов' and row[7] == 6000 for row in rows), 'Explicit source stock length lost')
    check(sum(str(row[0]).startswith('Неопределённость:') for row in rows) == 2, 'Warnings lost or duplicated')
    check(rows[-1][0] == 'Примечание пользователя:   Проверено & <пример> =1+1  ', 'User note/whitespace damaged')
    check(all(cell.data_type != 'f' for row in sheet for cell in row), 'Unexpected spreadsheet formula')
    check(any('сохранённые элементы' in str(row[0]) for row in rows), 'Retained-frame scope absent')
    manifest = {'status': 'PASS', 'xlsx_checks': checks, 'live_autocad_checked': False,
                'source_sha256': {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in sources}}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('Frame table XLSX checks:', checks, 'PASS')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
