"""Pure mounting coverage: actual C# declarations, prior node numeric goldens and refusal boundaries.

No CAD doubles, source PDFs or internet enter this test. Timings are observations
of selected-input processing, not promises for a live AutoCAD drawing.
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
from test_frame_solution_selection import example_selection


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not available():
        print("BLOCKED: native C# compiler/runtime unavailable")
        return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix="mounting_core_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    src = ROOT / "AFrame/src/AFramePlugin"
    sources = [Path(__file__).with_name("MountingCoreProbe.cs")] + [src / name for name in
        ("FrameMountingAssessment.cs", "FrameNodeGeometry.cs", "FrameProjectParameters.cs", "FrameSolutionSelection.cs")]
    executable = out / "MountingCoreProbe.exe"
    compiled = compile_probe(sources, ["System.Web.Extensions"], executable)
    (out / "compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
    if compiled.returncode:
        raise RuntimeError(compiled.stdout + compiled.stderr)
    sample = out / "selection.json"
    sample.write_text(json.dumps(example_selection(), ensure_ascii=False), encoding="utf-8")
    numeric = ROOT / "tools/node_geometry_0110/expected_numeric.json"
    subprocess.run(command(executable) + [str(sample), str(out / "native_result.json"), str(numeric)], check=True)
    native = json.loads((out / "native_result.json").read_text(encoding="utf-8"))
    assert native["status"] == "PASS", native
    expected = json.loads(numeric.read_text(encoding="utf-8"))
    observed = [{k: row[k] for k in ("name", "gap_text", "clearance_status", "can_insert")} for row in native["numeric"]]
    assert observed == expected, "mounting assessment changed the published node numeric fixtures"
    golden = ROOT / "tools/node_geometry_0110/expected_digests.json"
    golden_rows = json.loads(golden.read_text(encoding="utf-8"))["numeric"]
    assert [{k: row[k] for k in ("name", "input_digest", "result_digest")} for row in native["numeric"]] == golden_rows, "existing geometry input/result digests changed"
    files = sources + [Path(__file__).resolve(), numeric, golden, ROOT / "tools/quantities_3009/probe_runtime.py",
        ROOT / "AFrame/engine/test_frame_solution_selection.py", ROOT / "AFrame/engine/frame_solution_catalog.py"]
    manifest = dict(native)
    manifest["checks"] += 2
    manifest["cases"] += [
        {"name": "published exact numeric statuses preserved", "status": "PASS"},
        {"name": "published node input/result digests preserved", "status": "PASS"}]
    manifest["scope"] = "Actual pure C# mounting assessment; no CAD/files/process in product core; no mounting fit or static approval"
    manifest["source_sha256"] = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
    (out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (out / "review_text.txt").write_text(native["review_text"], encoding="utf-8")
    print("Mounting assessment:", manifest["checks"], "PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
