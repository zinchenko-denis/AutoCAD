"""Replay the five pre-assessment cases against exact git-backed historical C# sources."""
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", default="29dd38627ebb756dc3ef42f8db8a76a0e52a7d59")
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not available():
        print("BLOCKED: native C# compiler/runtime unavailable")
        return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix="mounting_baseline_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    revision = subprocess.check_output(["git", "rev-parse", "--verify", args.revision + "^{commit}"], cwd=ROOT, text=True).strip()
    baseline = json.loads((ROOT / "docs/mounting_assessment_0110/baseline.json").read_text(encoding="utf-8"))
    sample = out / "selection.json"
    sample.write_text(json.dumps(baseline["cases"][0]["selection"], ensure_ascii=False), encoding="utf-8")
    probe = Path(__file__).with_name("MountingBaselineProbe.cs")
    sources = [probe]
    hashes = {str(probe.relative_to(ROOT)): hashlib.sha256(probe.read_bytes()).hexdigest()}
    for filename in ("FrameNodeGeometry.cs", "FrameProjectParameters.cs", "FrameSolutionSelection.cs"):
        relative = "AFrame/src/AFramePlugin/" + filename
        data = subprocess.check_output(["git", "show", revision + ":" + relative], cwd=ROOT)
        target = out / filename
        target.write_bytes(data)
        sources.append(target)
        hashes[relative] = hashlib.sha256(data).hexdigest()
    executable = out / "MountingBaselineProbe.exe"
    compiled = compile_probe(sources, ["System.Web.Extensions"], executable)
    (out / "compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
    if compiled.returncode:
        raise RuntimeError(compiled.stdout + compiled.stderr)
    result = out / "baseline.json"
    subprocess.run(command(executable) + [str(sample), str(result)], check=True)
    observed = json.loads(result.read_text(encoding="utf-8"))
    assert observed["cases"] == baseline["cases"], "historical baseline values or digests differ"
    observed.update(head=revision, source_sha256=hashes, historical_baseline_equal=True)
    result.write_text(json.dumps(observed, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Historical mounting gap: 5 cases reproduced; exact published baseline match")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
