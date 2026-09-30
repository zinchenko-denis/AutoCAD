"""Compile actual pure C# quantities core and run independent geometry/quantity cases.

No AutoCAD runtime, DWG database, UI or installer is exercised. Exit 2 means blocked.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from probe_runtime import available, command, compile_probe

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="quantities_core_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    sources = [ROOT / "Common/FacadeQuantities.cs", Path(__file__).with_suffix(".cs")]
    report = {"scope": "Production pure C# quantities aggregation; no AutoCAD runtime.", "status": "BLOCKED",
              "source_sha256": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources}}

    def finish(code):
        (out / "manifest.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Quantities core:", report["status"], "report:", out / "manifest.json")
        return code

    if not available():
        report["reason"] = "Native .NET test compiler/runtime unavailable; no checks claimed."
        return finish(2)
    executable = out / "QuantitiesCoreCheck.exe"
    run = compile_probe(sources, ["System.Web.Extensions", "System.Core"], executable)
    (out / "compile.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    report["compile_returncode"] = run.returncode
    if run.returncode:
        report["status"] = "FAIL"
        print(run.stdout + run.stderr)
        return finish(1)
    result_path = out / "cases.json"
    result_path.unlink(missing_ok=True)
    run = subprocess.run(command(executable) + ["--report", str(result_path)], capture_output=True, text=True)
    (out / "run.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    print(run.stdout + run.stderr, end="")
    report["run_returncode"] = run.returncode
    if result_path.is_file():
        report["result"] = json.loads(result_path.read_text(encoding="utf-8"))
    report["status"] = "PASS" if run.returncode == 0 and report.get("result", {}).get("total", 0) else "FAIL"
    return finish(0 if report["status"] == "PASS" else 1)


if __name__ == "__main__":
    sys.exit(main())
