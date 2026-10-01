"""Run the facade regression cases found by the 30 September review.

The SPK probes are intentionally outside this runner: those modules remain
unchanged. --python-only is an explicit CI subset, not full validation.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
PROBES = ROOT / "tools/review_3009"


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", type=Path)
    ap.add_argument("--python-only", action="store_true")
    args = ap.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="facades_regressions_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, PYTHONUTF8="1", PYTHONIOENCODING="utf-8", PYTHONDONTWRITEBYTECODE="1")
    results = []

    def run(name, command):
        t0 = time.monotonic()
        p = subprocess.run(command, cwd=ROOT, env=env, capture_output=True, text=True)
        (out / (name + ".log")).write_text(p.stdout + p.stderr, encoding="utf-8")
        results.append({"name": name, "returncode": p.returncode,
                        "seconds": round(time.monotonic() - t0, 3)})
        print(name + ": " + ("PASS" if p.returncode == 0 else "FAIL"), flush=True)
        if p.returncode:
            print(p.stdout + p.stderr)
        return p.returncode

    for name in ("test_clad_frontons", "test_domain_review", "test_frame_regressions",
                 "test_facades_engine_edges"):
        run(name, [sys.executable, str(PROBES / (name + ".py"))])
    run("test_supported_workflows", [sys.executable, str(Path(__file__).with_name("test_supported_workflows.py"))])
    if not args.python_only:
        if not all(shutil.which(t) for t in ("mono", "mcs")):
            results.append({"name": "native_CSharp_probes", "returncode": 2,
                            "blocked": "mono/mcs unavailable"})
            print("BLOCKED: native C# probes require mono/mcs", flush=True)
        else:
            for name in ("test_facades_copy_handles", "test_facades_workstatement"):
                run(name, [sys.executable, str(PROBES / (name + ".py"))])
            run("test_frame_rail_metadata", [sys.executable, str(Path(__file__).with_name("test_frame_rail_metadata.py"))])
            run("test_frame_geometry_consumer", [sys.executable, str(Path(__file__).with_name("test_frame_geometry_consumer.py"))])
            run("test_zone_geometry", [sys.executable, str(Path(__file__).with_name("test_zone_geometry.py")),
                                      "--out", str(out / "zone_geometry")])
            run("test_zone_geometry_adapter", [sys.executable, str(Path(__file__).with_name("test_zone_geometry_adapter.py")),
                                              "--out", str(out / "zone_geometry_adapter")])
            run("test_clad_label_cleanup", [sys.executable, str(Path(__file__).with_name("test_clad_label_cleanup.py")),
                                           "--out", str(out / "clad_label_cleanup")])
            clad_exe = out / "CladSafetyCheck.exe"
            if run("CladSafetyCheck_compile", ["mcs", "-out:" + str(clad_exe),
                    str(Path(__file__).with_name("CladSafetyCheck.cs")),
                    str(ROOT / "AClad/src/ACladPlugin/LayoutSafety.cs")]) == 0:
                run("CladSafetyCheck", ["mono", str(clad_exe)])
            exe = out / "FrameSettingsProbe.exe"
            if run("FrameSettingsProbe_compile", ["mcs", "-r:System.Web.Extensions.dll",
                   "-out:" + str(exe), str(PROBES / "FrameSettingsProbe.cs"),
                   str(ROOT / "AFrame/src/AFramePlugin/FrameSettings.cs"),
                   str(ROOT / "AFrame/src/AFramePlugin/FrameSolutionSelection.cs"),
                   str(ROOT / "AFrame/src/AFramePlugin/FrameProjectParameters.cs")]) == 0:
                run("FrameSettingsProbe", ["mono", str(exe)])
    (out / "manifest.json").write_text(json.dumps({"scope": "facades only",
        "python_only": args.python_only, "results": results}, ensure_ascii=False, indent=2), encoding="utf-8")
    print("Logs:", out)
    if any(r["returncode"] == 2 for r in results):
        return 2
    return int(any(r["returncode"] for r in results))


if __name__ == "__main__":
    sys.exit(main())
