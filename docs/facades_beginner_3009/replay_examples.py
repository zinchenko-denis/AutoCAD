"""Replay saved public tutorial requests without regenerating images or DXF."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
ASSETS = Path(__file__).resolve().parent / "assets"
OUTPUT = ROOT / "docs/static_review_3009/manual_examples.json"
for module in ("Facades", "AClad", "AFrame"):
    sys.path.insert(0, str(ROOT / module / "engine"))
import facades_engine
import clad_engine
import frame_engine


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=OUTPUT, help="Verification receipt path; use a fresh path for a new revision.")
    args = parser.parse_args()
    checks = []
    old_manifest = json.loads((ROOT / "docs/atr_review_3009/logs/manual_examples.json").read_text())
    modules = {"zone": facades_engine, "tile": clad_engine, "frame": frame_engine}
    for previous in old_manifest["checks"]:
        name = previous["example"]
        request_path = ASSETS / (name + "_request.json")
        baseline_path = ASSETS / (name + "_result.json")
        request = json.loads(request_path.read_text())
        baseline = json.loads(baseline_path.read_text())
        # Normalize tuples to the JSON boundary used by the stored examples.
        current = json.loads(json.dumps(modules[name.split("_")[-1]].run(request)))
        equal_fields = [field for field in previous["equal_fields"]
                        if baseline.get(field) == current.get(field)]
        differences = sorted(field for field in baseline.keys() | current.keys()
                             if baseline.get(field) != current.get(field))
        allowed = {"notes", "design_scope", "connection_passport"} if name == "rect_frame" else set()
        # The original tutorial deliberately uses Standart/concrete geometry,
        # outside the Vector-1/vertical/porcelain connection pilot. Validate
        # that the added metadata explicitly refuses that unsupported scope.
        passport = current.get("connection_passport") if name == "rect_frame" else None
        passport_scope_check = (name != "rect_frame" or (
            isinstance(passport, dict) and passport.get("schema") == "aframe_connection_passport/1"
            and passport.get("status") == "unavailable" and bool(passport.get("reason"))
            and passport.get("members") == [] and passport.get("joints") == []
            and passport.get("coverage") == {"fixed_sliding": "not_modeled",
                "splice_continuity": "not_modeled", "gravity_load_distribution": "not_verified",
                "strength": "not_verified"}))
        passed = (current.get("ok") is True and equal_fields == previous["equal_fields"]
                  and set(differences) <= allowed and passport_scope_check)
        checks.append(dict(example=name, equal_fields=equal_fields,
                           different_metadata_fields=differences, passed=passed,
                           connection_scope_check=passport_scope_check,
                           request_sha256=digest(request_path),
                           baseline_result_sha256=digest(baseline_path)))
    paths = sorted(path for module in ("Facades", "AClad", "AFrame")
                   for path in (ROOT / module / "engine").iterdir()
                   if path.suffix in (".py", ".json"))
    result = dict(
        baseline_commit=old_manifest["baseline_commit"],
        source_base_commit=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        source_is_working_tree=True,
        comparison="Saved tutorial requests replayed; geometry and quantities unchanged. Frame notes/design_scope remain restrictive; connection_passport explicitly reports the original Standart/concrete example as unsupported.",
        checks=checks,
        passed=all(check["passed"] for check in checks),
        engine_source_sha256={str(path.relative_to(ROOT)): digest(path) for path in paths},
        live_autocad_checked=False,
    )
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("PASS" if result["passed"] else "FAIL", len(checks), "tutorial requests")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
