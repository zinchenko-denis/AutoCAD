"""Check frame core operation counts at 10k/50k/150k physical elements.

Wall-clock and memory values are observations, never pass/fail thresholds.
--quick runs 10k and 50k for CI. Every case checks full and subset selections.
No AutoCAD host, live DWG, UI or installer is exercised. Exit 2 means blocked.
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--quick", action="store_true")
    parser.add_argument("--sizes", type=int, nargs="+", help="Explicit positive counts, overriding --quick")
    args = parser.parse_args()
    sizes = args.sizes or ([10000, 50000] if args.quick else [10000, 50000, 150000])
    if any(size <= 0 for size in sizes):
        parser.error("--sizes values must be positive")
    out = (args.out or Path(tempfile.mkdtemp(prefix="frame_quantities_perf_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    sources = [ROOT / "Common/FacadeQuantities.cs", Path(__file__).with_name("FrameCorePerf.cs")]
    report = {"scope": "Production pure C# frame quantities; no live AutoCAD validation.", "status": "BLOCKED",
              "sizes": sizes, "clock_is_observation_only": True,
              "source_sha256": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources}, "results": []}

    def finish(code):
        (out / "manifest.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Frame quantities operation counts:", report["status"], "report:", out / "manifest.json")
        return code

    if not available():
        report["reason"] = "Native .NET compiler/runtime unavailable; no checks claimed."
        return finish(2)
    executable = out / "FrameCorePerf.exe"
    run = compile_probe(sources, ["System.Web.Extensions", "System.Core"], executable)
    (out / "compile.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    report["compile_returncode"] = run.returncode
    if run.returncode:
        report["status"] = "FAIL"
        print(run.stdout + run.stderr)
        return finish(1)
    for size in sizes:
        run = subprocess.run(command(executable) + [str(size)], capture_output=True, text=True)
        (out / (str(size) + ".log")).write_text(run.stdout + run.stderr, encoding="utf-8")
        if run.returncode:
            report["status"] = "FAIL"
            report["failed_size"] = size
            print(run.stdout + run.stderr)
            return finish(1)
        try:
            result = json.loads(run.stdout)
        except ValueError as exc:
            report["status"] = "FAIL"
            report["reason"] = "Probe did not emit valid JSON: " + str(exc)
            return finish(1)
        report["results"].append(result)
        if result.get("status") != "PASS" or len(result.get("workloads", [])) != 2:
            report["status"] = "FAIL"
            return finish(1)
        print("PASS", size, "physical elements; full/subset core seconds:",
              ", ".join(format(workload["core_s"], ".6f") for workload in result["workloads"]))
    report["status"] = "PASS"
    return finish(0)


if __name__ == "__main__":
    sys.exit(main())
