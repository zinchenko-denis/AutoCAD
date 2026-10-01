"""Actual pure C# project/zone resolver and settings, followed by real engine.

No CAD API is available to this executable. Timing is observational only.
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
import frame_engine


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not available():
        print("BLOCKED: native C# compiler/runtime unavailable")
        return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix="project_parameters_core_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    src = ROOT / "AFrame/src/AFramePlugin"
    sources = [Path(__file__).with_name("CoreProbe.cs")] + [src / name for name in ("FrameProjectParameters.cs", "FrameSolutionSelection.cs", "FrameSettings.cs")]
    executable = out / "CoreProbe.exe"
    compiled = compile_probe(sources, ["System.Web.Extensions"], executable)
    (out / "compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
    if compiled.returncode:
        raise RuntimeError(compiled.stdout + compiled.stderr)
    infile, outfile = out / "selection.json", out / "native_result.json"
    infile.write_text(json.dumps(example_selection(), ensure_ascii=False), encoding="utf-8")
    subprocess.run(command(executable) + [str(infile), str(outfile)], check=True)
    native = json.loads(outfile.read_text(encoding="utf-8"))
    assert native["status"] == "PASS", native
    request = {"op": "frame", "contours": [{"id": "project-zone", "pts": [[0,0],[1200,0],[1200,2400],[0,2400]]}], "joints_x": [300,900]}
    request.update(native["pipeline_engine_params"])
    response = frame_engine.run(request)
    assert response["ok"], response
    assert response["solution_report"]["selection"] == native["pipeline_settings"]["solution_selection"]
    assert response["solution_report"]["assembly_compatibility"] == "not_verified"
    assert request["rail_profile"] is None
    assert "project_parameters_context" not in request
    assert native["pipeline_settings"]["project_parameters_context"]["origins"]["profile.b_mm"] == "zone_override"
    bad = dict(request, calc={})
    assert frame_engine.run(bad)["error_code"] == "E_SOLUTION_CALC_UNCONFIRMED"
    (out / "engine_request.json").write_text(json.dumps(request, ensure_ascii=False, indent=2)+"\n",encoding="utf-8")
    (out / "engine_response.json").write_text(json.dumps(response,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    files = sources + [Path(__file__).resolve(),ROOT / "AFrame/engine/frame_solution_selection.py",ROOT / "AFrame/engine/frame_solution_catalog.py"]
    manifest = {"status":"PASS","checks":native["checks"]+7,"native_checks":native["checks"],"pipeline_checks":7,
        "cases":native["cases"],"performance":native["performance"],"live_autocad_checked":False,
        "scope":"Actual pure C# resolver/settings and real Python engine; no CAD API or drawing writes",
        "source_sha256":{str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}}
    (out / "manifest.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print("Project parameters core/engine:",manifest["checks"],"PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
