"""Run independent audit probes; return 1 for failures and 2 for blocked checks.

This is a diagnostic suite, intentionally red on audited commit e2a4e4d.
It does not modify production sources, build bundles, or launch AutoCAD.
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
HERE = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, help="Directory for logs and manifest")
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="autocad_review_3009_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, PYTHONUTF8="1", PYTHONIOENCODING="utf-8", PYTHONDONTWRITEBYTECODE="1")
    results = []

    def run(name, command):
        started = time.monotonic()
        proc = subprocess.run(command, cwd=ROOT, env=env, capture_output=True, text=True)
        log = proc.stdout + proc.stderr
        (out / (name + ".log")).write_text(log, encoding="utf-8")
        row = {"name": name, "command": command, "returncode": proc.returncode,
               "seconds": round(time.monotonic() - started, 3)}
        results.append(row)
        print(f"{name}: exit {proc.returncode}", flush=True)
        return proc.returncode

    for script in sorted(HERE.glob("test_*.py")):
        if script.name == "test_spec_csharp.py" and not all(shutil.which(t) for t in ("mcs", "mono")):
            results.append({"name": script.stem, "returncode": 2, "blocked": "mono/mcs unavailable"})
            continue
        run(script.stem, [sys.executable, str(script)])
    if all(shutil.which(t) for t in ("mcs", "mono")):
        exe = out / "FrameSettingsProbe.exe"
        code = run("FrameSettingsProbe_compile", ["mcs", "-r:System.Web.Extensions.dll",
                   "-out:" + str(exe), str(HERE / "FrameSettingsProbe.cs"),
                   str(ROOT / "AFrame/src/AFramePlugin/FrameSettings.cs"),
                   str(ROOT / "AFrame/src/AFramePlugin/FrameSolutionSelection.cs"),
                   str(ROOT / "AFrame/src/AFramePlugin/FrameProjectParameters.cs")])
        if code == 0:
            run("FrameSettingsProbe", ["mono", str(exe)])
    else:
        results.append({"name": "FrameSettingsProbe", "returncode": 2, "blocked": "mono/mcs unavailable"})
    manifest = {"base_commit": "e2a4e4df69a1936664c76c44cacff311ad44b07d",
                "python": sys.version, "results": results}
    (out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print("Logs:", out)
    if any(r["returncode"] == 2 for r in results):
        return 2
    return 1 if any(r["returncode"] != 0 for r in results) else 0


if __name__ == "__main__":
    sys.exit(main())
