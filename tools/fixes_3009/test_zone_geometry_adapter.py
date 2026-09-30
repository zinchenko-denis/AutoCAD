"""Actual zone guard + CAD API doubles + actual Python engine schema/report fixtures.

This checks adapter decisions, not Autodesk runtime behavior. No release is built.
Use --exe to run a precompiled .NET Framework test executable on Windows CI.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def make_fixtures():
    sys.path.insert(0, str(ROOT / "Facades/engine"))
    from facades_engine import op_zones

    def rectangle(x, y, width, height):
        return [[x, y], [x + width, y], [x + width, y + height], [x, y + height]]

    contours = [
        {"id": "1", "pts": rectangle(0, 0, 6000, 6000)},
        {"id": "2", "pts": rectangle(1000, 1500, 1200, 1500)},
        {"id": "3", "pts": rectangle(8000, 0, 4000, 4000)},
        {"id": "4", "pts": rectangle(8500, 1000, 600, 800)},
    ]
    fixtures = {}
    for name, count, merged in (("single", 2, False), ("merged", 4, True), ("curved", 2, False)):
        request = {"op": "zones", "contours": contours[:count], "merge": merged,
                   "cladding": "porcelain", "zone_prefix": "F-", "units": "mm"}
        request = json.loads(json.dumps(request))
        if name == "curved":
            request["contours"][1]["bulges"] = [0, 0, 1, 0]
        result = op_zones(request)
        if not result.get("ok") or len(result.get("zones", [])) != 1 or result.get("failed"):
            raise ValueError("Engine rejected adapter positive fixture " + name + ": " + json.dumps(result))
        if len(result["zones_full"]) != (2 if merged else 1):
            raise ValueError("Engine omitted positive fixture parts")
        fixtures[name] = {"request": request, "result": result}
    return fixtures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--exe", type=Path, help="Precompiled test_zone_geometry_adapter.csproj executable")
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="zone_geometry_adapter_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    sources = [ROOT / "Common/GeometryFingerprint.cs", ROOT / "Common/ZoneGeometryGuard.cs", ROOT / "Common/LayoutGeometryGuard.cs",
               Path(__file__).with_suffix(".cs"), Path(__file__).with_name("test_zone_geometry_cad_doubles.cs")]
    report = {"status": "BLOCKED", "scope": "Native production guards with CAD API doubles; no AutoCAD runtime.",
              "source_sha256": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                                for p in sources if p.is_file()}}
    for name in ("facade_zones.py", "facades_engine.py"):
        path = ROOT / "Facades/engine" / name
        report["source_sha256"][str(path.relative_to(ROOT))] = hashlib.sha256(path.read_bytes()).hexdigest()

    def finish(code):
        (out / "manifest.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("Zone adapter:", report["status"], "report:", out / "manifest.json")
        return code

    fixture_file = out / "fixtures.json"
    fixture_file.write_text(json.dumps(make_fixtures(), ensure_ascii=False, indent=2), encoding="utf-8")
    report["fixtures_sha256"] = hashlib.sha256(fixture_file.read_bytes()).hexdigest()
    if args.exe:
        executable = args.exe.resolve()
        if not executable.is_file():
            report["reason"] = "Precompiled test executable does not exist."
            return finish(2)
        report["precompiled_executable_sha256"] = hashlib.sha256(executable.read_bytes()).hexdigest()
    else:
        if not all(p.is_file() for p in sources) or not shutil.which("mcs") or not shutil.which("mono"):
            report["reason"] = "Missing production guard or mcs/mono. No checks claimed."
            return finish(2)
        executable = out / "ZoneGeometryAdapterCheck.exe"
        compiled = subprocess.run(["mcs", "-nologo", "-r:System.Web.Extensions.dll", "-out:" + str(executable)]
                                  + [str(p) for p in sources], capture_output=True, text=True)
        (out / "compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
        report["compile_returncode"] = compiled.returncode
        if compiled.returncode:
            report["status"] = "FAIL"
            print(compiled.stdout + compiled.stderr)
            return finish(1)
    cases = out / "cases.json"
    if cases.exists():
        cases.unlink()
    launcher = [str(executable)] if os.name == "nt" else ["mono", str(executable)]
    run = subprocess.run(launcher + ["--fixtures", str(fixture_file), "--report", str(cases)], capture_output=True, text=True)
    (out / "run.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    print(run.stdout + run.stderr, end="")
    report["run_returncode"] = run.returncode
    if cases.is_file():
        report["result"] = json.loads(cases.read_text(encoding="utf-8"))
    report["status"] = "PASS" if run.returncode == 0 and report.get("result", {}).get("total", 0) > 0 else "FAIL"
    return finish(0 if report["status"] == "PASS" else 1)


if __name__ == "__main__":
    sys.exit(main())
