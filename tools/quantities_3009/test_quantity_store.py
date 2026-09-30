"""Run the actual quantity store and geometry guards against explicit CAD doubles.

Fixtures are produced by the actual zone engine using the existing adapter
fixture builder. This is a persistence/freshness contract check, not AutoCAD
runtime, transaction rollback, COPY, Undo, Save or reopen verification.
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


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--exe", type=Path, help="Precompiled QuantityStoreCheck executable")
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="quantity_store_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    local = Path(__file__).resolve().parent
    sources = [ROOT / "Common" / name for name in (
        "GeometryFingerprint.cs", "ZoneGeometryGuard.cs", "LayoutGeometryGuard.cs",
        "FacadeQuantities.cs", "FacadeQuantityStore.cs", "FacadeQuantityStore.Frame.cs", "ManualQuantities.cs", "ManualQuantityGeometry.cs",
        "FacadeQuantityStore.Manual.cs")]
    sources += [local / "QuantityStoreCheck.cs", local / "QuantityCadDoubles.cs"]
    fixture_builder = ROOT / "tools/fixes_3009/test_zone_geometry_adapter.py"
    legacy_fixture = local / "fixtures/cladding_v1_6222041_xrecords.json"
    tracked = sources + [Path(__file__).resolve(), fixture_builder, legacy_fixture,
                        ROOT / "Facades/engine/facade_zones.py", ROOT / "Facades/engine/facades_engine.py"]
    manifest = {"status": "BLOCKED", "scope": __doc__, "source_sha256": {
        str(path.relative_to(ROOT)): digest(path) for path in tracked if path.is_file()}}

    def finish(code):
        (out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Quantity store:", manifest["status"], "report:", out / "manifest.json")
        return code

    if not all(path.is_file() for path in sources):
        manifest["reason"] = "Production or adapter test sources are missing. No checks claimed."
        return finish(2)
    spec = importlib.util.spec_from_file_location("zone_adapter_fixtures", fixture_builder)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    fixture_file = out / "fixtures.json"
    fixtures = module.make_fixtures()
    fixtures["legacy_xrecords"] = json.loads(legacy_fixture.read_text(encoding="utf-8"))
    fixture_file.write_text(json.dumps(fixtures, ensure_ascii=False, indent=2), encoding="utf-8")
    manifest["fixtures_sha256"] = digest(fixture_file)
    if args.exe:
        executable = args.exe.resolve()
        if not executable.is_file():
            manifest["reason"] = "Precompiled executable does not exist. No checks claimed."
            return finish(2)
        manifest["precompiled_executable_sha256"] = digest(executable)
    else:
        if not shutil.which("mcs") or not shutil.which("mono"):
            manifest["reason"] = "Missing mcs/mono. No checks claimed."
            return finish(2)
        executable = out / "QuantityStoreCheck.exe"
        compile_run = subprocess.run(["mcs", "-nologo", "-r:System.Web.Extensions.dll", "-out:" + str(executable)]
                                     + [str(path) for path in sources], text=True, capture_output=True)
        (out / "compile.log").write_text(compile_run.stdout + compile_run.stderr, encoding="utf-8")
        manifest["compile_returncode"] = compile_run.returncode
        if compile_run.returncode:
            manifest["status"] = "FAIL"
            print(compile_run.stdout + compile_run.stderr)
            return finish(1)
    cases = out / "cases.json"
    if cases.exists():
        cases.unlink()
    launcher = [str(executable)] if os.name == "nt" else ["mono", str(executable)]
    run = subprocess.run(launcher + ["--fixtures", str(fixture_file), "--report", str(cases)],
                         text=True, capture_output=True)
    (out / "run.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    print(run.stdout + run.stderr, end="")
    manifest["run_returncode"] = run.returncode
    if cases.is_file():
        manifest["result"] = json.loads(cases.read_text(encoding="utf-8"))
    manifest["sources_unchanged_during_run"] = all(
        digest(ROOT / name) == value for name, value in manifest["source_sha256"].items())
    manifest["status"] = "PASS" if (run.returncode == 0 and manifest["sources_unchanged_during_run"]
        and manifest.get("result", {}).get("total", 0) > 0) else "FAIL"
    return finish(0 if manifest["status"] == "PASS" else 1)


if __name__ == "__main__":
    sys.exit(main())
