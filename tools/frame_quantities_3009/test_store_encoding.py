"""Check actual store UTF-8 hashing/compression against legacy whole-buffer operations.

Tests empty text, Cyrillic, chunk boundaries, split surrogate pairs and standard
fallback for isolated surrogates. CAD doubles satisfy compilation only; no DWG,
AutoCAD host, UI, transaction or installer is exercised. Exit 2 means blocked.
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


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    out = (args.out or Path(tempfile.mkdtemp(prefix="quantity_store_encoding_"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    sources = [ROOT / "Common" / name for name in (
        "GeometryFingerprint.cs", "ZoneGeometryGuard.cs", "LayoutGeometryGuard.cs",
        "FacadeQuantities.cs", "FacadeQuantityStore.cs", "FacadeQuantityStore.Frame.cs", "ManualQuantities.cs", "ManualQuantityGeometry.cs",
        "FacadeQuantityStore.Manual.cs")]
    sources += [ROOT / "tools/quantities_3009/QuantityCadDoubles.cs",
                Path(__file__).with_name("StoreEncodingProbe.cs")]
    tracked = sources + [Path(__file__).resolve(), ROOT / "tools/quantities_3009/probe_runtime.py"]
    report = {"status": "BLOCKED", "scope": __doc__, "source_sha256": {
        str(path.relative_to(ROOT)): digest(path) for path in tracked if path.is_file()}}

    def finish(code):
        (out / "manifest.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Quantity store UTF-8:", report["status"], "report:", out / "manifest.json")
        return code

    if not all(path.is_file() for path in tracked) or not available():
        report["reason"] = "Production/test sources or native .NET compiler/runtime unavailable; no checks claimed."
        return finish(2)
    executable = out / "StoreEncodingProbe.exe"
    compiled = compile_probe(sources, ["System.Web.Extensions", "System.Core"], executable)
    (out / "compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
    report["compile_returncode"] = compiled.returncode
    if compiled.returncode:
        report["status"] = "FAIL"
        print(compiled.stdout + compiled.stderr)
        return finish(1)
    cases = out / "cases.json"
    cases.unlink(missing_ok=True)
    run = subprocess.run(command(executable) + ["--report", str(cases)], capture_output=True, text=True)
    (out / "run.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    print(run.stdout + run.stderr, end="")
    report["run_returncode"] = run.returncode
    if cases.is_file():
        report["result"] = json.loads(cases.read_text(encoding="utf-8"))
    report["sources_unchanged_during_run"] = all(
        digest(ROOT / name) == value for name, value in report["source_sha256"].items())
    report["status"] = "PASS" if (run.returncode == 0 and report["sources_unchanged_during_run"]
        and report.get("result", {}).get("total") == 16 and report["result"].get("failed") == 0) else "FAIL"
    return finish(0 if report["status"] == "PASS" else 1)


if __name__ == "__main__":
    sys.exit(main())
