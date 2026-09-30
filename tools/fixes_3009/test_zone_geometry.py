"""Compile and execute independent native C# geometry freshness regressions.

Compiles the actual production pure helper. Does not load Autodesk assemblies,
run AutoCAD, simulate COPY/reactors, or create a release. Exit 2 means blocked.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="zone_geometry_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    sources = [ROOT / "Common/GeometryFingerprint.cs", Path(__file__).with_suffix(".cs")]
    report = {
        "scope": "Production pure C# geometry helper; no AutoCAD runtime or database adapter checks.",
        "status": "BLOCKED",
        "source_sha256": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                          for p in sources if p.is_file()},
    }

    def finish(code):
        (out / "manifest.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("Geometry regressions:", report["status"], "report:", out / "manifest.json")
        return code

    if not all(p.is_file() for p in sources) or not shutil.which("mcs") or not shutil.which("mono"):
        report["reason"] = "Missing mcs/mono or production helper; no checks claimed."
        return finish(2)
    executable = out / "GeometrySafetyCheck.exe"
    command = ["mcs", "-nologo", "-r:System.Web.Extensions.dll", "-out:" + str(executable)]
    command.extend(str(p) for p in sources)
    compiled = subprocess.run(command, capture_output=True, text=True)
    (out / "compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
    report["compile_returncode"] = compiled.returncode
    if compiled.returncode:
        report["status"] = "FAIL"
        report["reason"] = "Native harness compilation failed."
        print(compiled.stdout + compiled.stderr)
        return finish(1)
    cases_path = out / "cases.json"
    if cases_path.exists():
        cases_path.unlink()  # Never accept a previous run's report after a crash.
    run = subprocess.run(["mono", str(executable), "--report", str(cases_path)],
                         capture_output=True, text=True)
    (out / "run.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    print(run.stdout + run.stderr, end="")
    report["run_returncode"] = run.returncode
    if cases_path.is_file():
        report["result"] = json.loads(cases_path.read_text(encoding="utf-8"))
    report["status"] = "PASS" if run.returncode == 0 and report.get("result", {}).get("total", 0) > 0 else "FAIL"
    return finish(0 if report["status"] == "PASS" else 1)


if __name__ == "__main__":
    sys.exit(main())
