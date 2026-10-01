"""Actual pure node core: strict source/snapshot, exact decimal boundaries, roles and scaling.

No CAD doubles or renderer enter this gate. Timings are observations, not a
promise for live AutoCAD. Golden decimal labels run on Mono and Windows .NET.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/quantities_3009"))
from probe_runtime import available, command, compile_probe
sys.path.insert(0, str(ROOT / "AFrame/engine"))
from test_frame_solution_selection import example_selection, plan_request
from frame_plan import frame_plan
from frame_solution_selection import prepare_solution


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not available():
        print("BLOCKED: native C# compiler/runtime unavailable")
        return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix="node_core_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    src = ROOT / "AFrame/src/AFramePlugin"
    sources = [Path(__file__).with_name("NodeCoreProbe.cs")] + [src / name for name in
        ("FrameNodeGeometry.cs", "FrameProjectParameters.cs", "FrameSolutionSelection.cs")]
    executable = out / "NodeCoreProbe.exe"
    compiled = compile_probe(sources, ["System.Web.Extensions"], executable)
    (out / "compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
    if compiled.returncode:
        raise RuntimeError(compiled.stdout + compiled.stderr)
    infile, outfile = out / "selection.json", out / "native_result.json"
    infile.write_text(json.dumps(example_selection(), ensure_ascii=False), encoding="utf-8")
    subprocess.run(command(executable) + [str(infile), str(outfile)], check=True)
    native = json.loads(outfile.read_text(encoding="utf-8"))
    assert native["status"] == "PASS", native
    checks = []
    def check(name, condition):
        assert condition, name
        checks.append({"name": name, "status": "PASS"})
    expected = json.loads(Path(__file__).with_name("expected_numeric.json").read_text(encoding="utf-8"))
    observed = [{k: row[k] for k in ("name", "gap_text", "clearance_status", "can_insert")} for row in native["numeric"]]
    check("cross-runtime exact decimal status and display fixtures", observed == expected)
    digest_fixture = json.loads(Path(__file__).with_name("expected_digests.json").read_text(encoding="utf-8"))
    check("cross-runtime full result and input digests", digest_fixture["numeric"] ==
          [{k: r[k] for k in ("name", "input_digest", "result_digest")} for r in native["numeric"]])
    check("cross-runtime project-bound snapshot digest", digest_fixture["snapshot"] ==
          {k: native["snapshot"][k] for k in ("input_digest", "result_digest", "snapshot_digest")})
    # Current old layout behaviour stays explicit: node geometry does not
    # silently resize the frame or unlock the historical section calculation.
    baseline = []
    for front, layers in [(180, [150, 50]), (230, [100, 50]), (230, [150, 50])]:
        selection = example_selection()
        selection["geometry"].update(cladding_front_offset_mm=front, insulation_layers_mm=layers)
        request = dict(plan_request(), solution_selection=selection)
        response = frame_plan(request)
        check(f"legacy layout stays preset for {front}/{layers}", response["ok"] and response["solution_report"]["geometry_effect"] == "existing_preset_only")
        check(f"calculation refusal retained for {front}/{layers}", prepare_solution(dict(request, calc={}))[1]["error_code"] == "E_SOLUTION_CALC_UNCONFIRMED")
        baseline.append({"front": front, "layers": layers, "rails": len(response["rails"]), "brackets": len(response["brackets"]), "geometry_effect": response["solution_report"]["geometry_effect"]})
    check("node planes do not silently resize legacy physical frame", len({(c["rails"], c["brackets"]) for c in baseline}) == 1)
    files = sources + [Path(__file__).resolve(), Path(__file__).with_name("expected_numeric.json"), Path(__file__).with_name("expected_digests.json"), ROOT / "tools/quantities_3009/probe_runtime.py"]
    # Hash actual imported local dependencies, including the unchanged baseline
    # engine and source catalogue; no private fixtures are used here.
    engine = (ROOT / "AFrame/engine").resolve()
    for module in tuple(sys.modules.values()):
        path = getattr(module, "__file__", None)
        if path and Path(path).resolve().parent == engine and Path(path).suffix == ".py":
            files.append(Path(path).resolve())
    files += [engine / "systems.json", engine / "assembly_catalog.json"]
    manifest = {"status": "PASS", "checks": native["checks"] + len(checks), "native_checks": native["checks"],
        "cases": native["cases"] + checks, "numeric": native["numeric"], "performance": native["performance"], "cad_conversions": native["cad_conversions"],
        "legacy_frame_baseline": baseline, "live_autocad_checked": False,
        "scope": "Actual pure C# node core and unchanged real Python layout; no CAD/renderer",
        "source_sha256": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}}
    (out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Node geometry core:", manifest["checks"], "PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
