"""Measure actual Store/Read CAD operations and enforce bounded verification work.

The instrumented doubles do not measure AutoCAD execution time. The legacy
layout-write setup is reported separately: its repeated source snapshots remain
a documented limitation, not an exception hidden inside the new bound.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--quick", action="store_true", help="Mandatory 10/40-zone operation-growth gate")
    parser.add_argument("--out", type=Path)
    parser.add_argument("--exe", type=Path)
    parser.add_argument("--large", action="store_true", help="Also measure one zone with 10k/50k/150k physical elements")
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="quantity_store_perf_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    here = Path(__file__).resolve().parent
    sources = [ROOT / "Common" / name for name in ("GeometryFingerprint.cs", "ZoneGeometryGuard.cs",
        "LayoutGeometryGuard.cs", "FacadeQuantities.cs", "FacadeQuantityStore.cs", "FacadeQuantityStore.Frame.cs", "FacadeProjectParameterStore.cs", "ManualQuantities.cs", "ManualQuantityGeometry.cs",
        "FacadeQuantityStore.Manual.cs")]
    sources += [here / name for name in ("QuantityStoreCheck.cs", "QuantityCadDoubles.cs", "QuantityStorePerfCheck.cs")]
    fixture_builder = ROOT / "tools/fixes_3009/test_zone_geometry_adapter.py"
    tracked = sources + [Path(__file__).resolve(), fixture_builder,
                         ROOT / "Facades/engine/facade_zones.py", ROOT / "Facades/engine/facades_engine.py"]
    manifest = {"status": "BLOCKED", "scope": __doc__, "source_sha256": {
        str(path.relative_to(ROOT)): sha(path) for path in tracked if path.is_file()}, "cases": []}

    def finish(code):
        (out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Quantity store performance:", manifest["status"], "report:", out / "manifest.json")
        return code

    if not all(path.is_file() for path in sources):
        manifest["reason"] = "Missing source files; no benchmark claimed."
        return finish(2)
    spec = importlib.util.spec_from_file_location("zone_adapter_fixtures", fixture_builder)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    fixtures = out / "fixtures.json"
    fixtures.write_text(json.dumps(module.make_fixtures(), ensure_ascii=False, indent=2), encoding="utf-8")
    manifest["fixtures_sha256"] = sha(fixtures)
    if args.exe:
        executable = args.exe.resolve()
        if not executable.is_file():
            manifest["reason"] = "Missing precompiled executable; no benchmark claimed."
            return finish(2)
        manifest["precompiled_executable_sha256"] = sha(executable)
    else:
        if not shutil.which("mcs") or not shutil.which("mono"):
            manifest["reason"] = "Missing mcs/mono; no benchmark claimed."
            return finish(2)
        executable = out / "QuantityStorePerfCheck.exe"
        build = subprocess.run(["mcs", "-nologo", "-r:System.Web.Extensions.dll", "-main:QuantityStorePerfCheck",
                                "-out:" + str(executable)] + [str(p) for p in sources], capture_output=True, text=True)
        (out / "compile.log").write_text(build.stdout + build.stderr, encoding="utf-8")
        manifest["compile_returncode"] = build.returncode
        if build.returncode:
            manifest["status"] = "FAIL"
            print(build.stdout + build.stderr)
            return finish(1)
    configurations = [(kind, zones, 3 if args.quick else 300)
                      for kind in ("cladding", "frame") for zones in ((10, 40) if args.quick else (10, 50, 100))]
    if args.large:
        configurations += [(kind, 1, pieces) for kind in ("cladding", "frame") for pieces in (10000, 50000, 150000)]
    launcher = [str(executable)] if os.name == "nt" else ["mono", str(executable)]
    for kind, zones, per_zone in configurations:
        name = f"{kind}_z{zones}_p{per_zone}"
        result_file = out / (name + ".json")
        if result_file.exists():
            result_file.unlink()
        command = launcher + [str(zones), str(per_zone), str(fixtures), str(result_file), kind]
        run = subprocess.run(command, text=True, capture_output=True)
        (out / (name + ".log")).write_text(run.stdout + run.stderr, encoding="utf-8")
        case = {"name": name, "returncode": run.returncode, "status": "FAIL", "violations": []}
        if run.returncode == 0 and result_file.is_file():
            result = json.loads(result_file.read_text(encoding="utf-8"))
            case["result"] = result
            pieces = zones * per_zone
            for phase in result["phases"]:
                label = phase["phase"]
                if label in ("quantity_store", "quantity_read"):
                    # Each live physical entity must be checked; unique source
                    # verification must not repeat the entire group per owner.
                    if phase["get_object_read"] > 20 * pieces + 250 * zones + 1000:
                        case["violations"].append(label + ": CAD source work exceeds linear bound")
                    if phase["hatch_loops_read"] > 10 * zones + 20:
                        case["violations"].append(label + ": unique zone geometry checked repeatedly")
                if label == "quantity_read" and phase["modelspace_visits"] > pieces + 4 * zones + 5:
                    case["violations"].append(label + ": more than one ModelSpace scan")
                if label == "frame_source_capture" and phase["hatch_loops_read"] > 12 * zones + 20:
                    case["violations"].append(label + ": source capture repeats zone geometry")
            if not case["violations"] and result.get("status") == "PASS":
                case["status"] = "PASS"
        else:
            print(run.stdout + run.stderr)
        manifest["cases"].append(case)
        print(name, case["status"], "; ".join(case["violations"]))
    manifest["sources_unchanged_during_run"] = all(sha(ROOT / name) == value for name, value in manifest["source_sha256"].items())
    manifest["status"] = "PASS" if manifest["sources_unchanged_during_run"] and all(c["status"] == "PASS" for c in manifest["cases"]) else "FAIL"
    return finish(0 if manifest["status"] == "PASS" else 1)


if __name__ == "__main__":
    sys.exit(main())
