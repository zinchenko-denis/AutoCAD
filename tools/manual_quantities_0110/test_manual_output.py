"""Execute production manual inventory/table models and independently read their XLSX.

This checks the shared typed row source used for DWG/XLSX, not an AutoCAD Table
entity, native command interaction, transaction, or live UI. Exit 2 is blocked.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import traceback

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/quantities_3009"))
from probe_runtime import available, command, compile_probe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="manual_output_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    sources = [Path(__file__).with_name("ManualOutputProbe.cs"), ROOT / "Common/FacadeQuantities.cs", ROOT / "Common/ManualQuantities.cs"]
    sources += [ROOT / "Facades/src/AFacadesPlugin" / name for name in (
        "ManualQuantityView.cs", "CladdingTableData.cs", "FrameTableData.cs", "QuantityTableView.cs", "QuantityXlsxFile.cs", "XlsxWriter.cs")]
    tracked = sources + [Path(__file__).resolve(), ROOT / "tools/quantities_3009/probe_runtime.py"]

    def hashes():
        return {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in tracked}

    report = {"status": "BLOCKED", "scope": __doc__, "live_autocad_checked": False, "source_sha256": hashes()}

    def finish(code):
        report["sources_unchanged_during_run"] = hashes() == report["source_sha256"]
        if not report["sources_unchanged_during_run"]:
            report["status"] = "FAIL"
            report["reason"] = "Sources changed during execution; repeat the final check."
            code = 1
        (out / "manifest.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print("Manual output:", report["status"], "report:", out / "manifest.json")
        return code

    if not available():
        report["reason"] = "Native .NET compiler/runtime unavailable; no checks claimed."
        return finish(2)
    try:
        from openpyxl import load_workbook
    except ImportError:
        report["reason"] = "openpyxl unavailable; independent workbook inspection is blocked."
        return finish(2)
    executable = out / "ManualOutputProbe.exe"
    run = compile_probe(sources, ["System.Web.Extensions", "System.Core", "System.Xml", "System.IO.Compression", "System.IO.Compression.FileSystem"], executable)
    (out / "compile.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    report["compile_returncode"] = run.returncode
    if run.returncode:
        report["status"] = "FAIL"
        print(run.stdout + run.stderr)
        return finish(1)
    run = subprocess.run(command(executable) + [str(out)], capture_output=True, text=True)
    (out / "run.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    print(run.stdout + run.stderr, end="")
    report["run_returncode"] = run.returncode
    if (out / "cases.json").is_file():
        report["result"] = json.loads((out / "cases.json").read_text(encoding="utf-8"))
    if run.returncode:
        report["status"] = "FAIL"
        return finish(1)

    checks = []

    def check(name, condition):
        assert condition, name
        checks.append(name)

    def normalized(row):
        result = [None if value == "" else value for value in row]
        while result and result[-1] is None:
            result.pop()
        return result

    try:
        books = {}
        for stem, sheet_name in (("manual_cladding", "Облицовка"), ("manual_frame", "Подсистема")):
            book = load_workbook(out / (stem + ".xlsx"), data_only=False)
            sheet = book[sheet_name]
            rows = list(sheet.values)
            model = json.loads((out / (stem + "_rows.json")).read_text(encoding="utf-8"))
            check(stem + ": all typed row values survive export", [normalized(row) for row in rows] == [normalized(row) for row in model])
            check(stem + ": text never becomes spreadsheet formula", all(cell.data_type != "f" for row in sheet for cell in row))
            check(stem + ": one explicit unaccounted appendix", sum(str(row[0]).startswith("НЕУЧТЁННЫЕ ОБЪЕКТЫ") for row in rows) == 1)
            books[stem] = rows

        clad = books["manual_cladding"]
        total = next(row for row in clad if row[0] == "ИТОГО")
        check("mixed physical total excludes unaccounted objects", total[7] == 3 and total[8] == 0.06)
        check("exact user mark survives XLSX", any("001-К/1,2" in str(row[2]) for row in clad if len(row) > 2))
        check("unaccounted reason remains literal text", clad[-1][1] == "A4" and clad[-1][4] == "=1+1 <не поддержано>")
        check("user note survives before appendix", any(row[0] == "Примечание пользователя:   Примечание & <проверка>  " for row in clad))
        check("outline dimensions named explicitly", any("Габариты контура X/Y" in str(row[4]) for row in clad if len(row) > 4))
        frame = books["manual_frame"]
        rail = next(row for row in frame if row[0] == "Зона А" and row[2] == "Направляющая")
        bracket = next(row for row in frame if row[0] == "Зона А" and row[2] == "Кронштейн")
        check("physical rail metres and exact member length remain numbers", rail[7] == 2500.1234567 and rail[8] == 2 and rail[9] == 5.0002469134)
        check("piece-only bracket has no invented metres", bracket[7] == "—" and bracket[8] == 3 and bracket[9] == "—")
        check("unknown rail object retains reason in export", frame[-1][1] == "F1" and frame[-1][4] == "Длина не подтверждена")
        refusal = json.loads((out / "refusal_rows.json").read_text(encoding="utf-8"))
        check("unknown-only model has inventory but no zero subtotal", len(refusal) == 3 and not any(row[0] == "ИТОГО" for row in refusal))
        report["xlsx_checks"] = checks
        report["status"] = "PASS"
        print("Independent XLSX checks:", len(checks), "PASS")
        return finish(0)
    except Exception as exc:
        report["status"] = "FAIL"
        report["reason"] = str(exc)
        report["xlsx_checks_passed"] = checks
        (out / "xlsx_failure.log").write_text(traceback.format_exc(), encoding="utf-8")
        print(traceback.format_exc())
        return finish(1)


if __name__ == "__main__":
    raise SystemExit(main())
