"""Read-only AFrame API contract probes using one synthetic rectangle.

Run from any working directory; --output optionally saves the printed JSON.
Without --revision, the source is the current working tree. With --revision,
only AFrame/engine is extracted from that commit into a temporary directory;
the repository and working files are not changed. working_tree_dirty records
the repository status of AFrame/engine, even when an archived commit is used.
The observations describe current behaviour, not NordFOX support or engineering
acceptance. No private drawings, albums, or PDF-derived geometry are included.
"""
import argparse
from copy import deepcopy
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True


BASE_REQUEST = {
    "op": "frame",
    "system": "Standart",
    "sub_type": "vertical",
    "cladding": "porcelain",
    "contours": [{"id": "wall", "pts": [[0, 0], [1200, 0],
                                           [1200, 9000], [0, 9000]]}],
    "joints_x": [300, 900],
    "rows_y": list(range(0, 9001, 600)),
    "corners_x": [],
}
CALC = {"wind_region": "II", "terrain": "B", "height": 30,
        "q_clad": 25, "offset": 230, "na_max": 3000}
OVERRIDES = {
    "manual_control": {},
    "unknown_system_string": {"system": "NordFOX"},
    "unknown_system_name_dict": {"system": {"name": "NordFOX"}},
    "manual_unknown_sub_type": {"sub_type": "nordfox"},
    "calc_control": {"system": "Вектор-1", "calc": CALC},
    "calc_unknown_sub_type": {"system": "Вектор-1", "calc": CALC,
                              "sub_type": "nordfox"},
    "calc_unknown_scheme": {"system": "Вектор-1",
                            "calc": dict(CALC, scheme="nordfox")},
}


def summarize(result):
    report = result.get("calc_report") or {}
    verification = report.get("layout_verification") or {}
    return {
        "ok": result.get("ok"),
        "error": result.get("error"),
        "system_name": (result.get("system_used") or {}).get("_name"),
        "rails": len(result.get("rails", [])),
        "brackets": len(result.get("brackets", [])),
        "clamps": len(result.get("clamps", [])),
        "calc_scheme": report.get("scheme"),
        "layout_verification_passed": verification.get("passed"),
        "checked_intervals": {zone: value["intervals"]
                              for zone, value in verification.get("zones", {}).items()},
    }


def geometry_equal(left, right):
    return all(left.get(key) == right.get(key)
               for key in ("rails", "hrails", "brackets", "clamps", "fittings"))


def run_cases(engine_run):
    results = {}
    for name, override in OVERRIDES.items():
        request = deepcopy(BASE_REQUEST)
        request.update(deepcopy(override))
        results[name] = engine_run(request)
    summaries = {name: summarize(result) for name, result in results.items()}
    unknown_calc = summaries["calc_unknown_sub_type"]
    unknown_dict = summaries["unknown_system_name_dict"]
    observations = {
        "manual_control_has_12_rails_and_48_brackets":
            summaries["manual_control"]["ok"] is True
            and summaries["manual_control"]["rails"] == 12
            and summaries["manual_control"]["brackets"] == 48,
        "unknown_system_string_is_rejected":
            summaries["unknown_system_string"]["ok"] is False,
        "unknown_system_dict_is_accepted_with_12_rails_and_no_brackets":
            unknown_dict["ok"] is True and unknown_dict["system_name"] == "NordFOX"
            and unknown_dict["rails"] == 12 and unknown_dict["brackets"] == 0,
        "unknown_sub_type_produces_control_geometry":
            summaries["manual_unknown_sub_type"]["ok"] is True
            and geometry_equal(results["manual_control"], results["manual_unknown_sub_type"]),
        "calc_control_checks_36_intervals":
            summaries["calc_control"]["ok"] is True
            and summaries["calc_control"]["checked_intervals"] == {"row": 36, "corner": 0},
        "calc_unknown_sub_type_produces_control_geometry":
            unknown_calc["ok"] is True
            and geometry_equal(results["calc_control"], results["calc_unknown_sub_type"]),
        "calc_unknown_sub_type_passes_verification_without_checked_intervals":
            unknown_calc["calc_scheme"] == "vertical"
            and unknown_calc["layout_verification_passed"] is True
            and unknown_calc["checked_intervals"] == {"row": 0, "corner": 0},
        "unknown_calc_scheme_is_rejected":
            summaries["calc_unknown_scheme"]["ok"] is False,
    }
    return {
        "scope": "Observed API behaviour only; true observations are not engineering acceptance.",
        "base_request": BASE_REQUEST,
        "cases": {name: {"request_overrides": override, "summary": summaries[name]}
                  for name, override in OVERRIDES.items()},
        "observations": observations,
        "all_recorded_observations_reproduced": all(observations.values()),
    }


def run(revision=None):
    git = ["git", "-C", str(ROOT)]
    source_revision = subprocess.check_output(
        git + ["rev-parse", "--verify", (revision or "HEAD") + "^{commit}"],
        text=True).strip()
    dirty = bool(subprocess.check_output(
        git + ["status", "--porcelain=v1", "--untracked-files=normal", "--", "AFrame/engine"],
        text=True).strip())
    with tempfile.TemporaryDirectory(prefix="nordfox-contract-") as temporary:
        source_root = ROOT
        if revision:
            source_root = Path(temporary)
            archive = subprocess.check_output(
                git + ["archive", "--format=tar", source_revision, "AFrame/engine"])
            with tarfile.open(fileobj=io.BytesIO(archive), mode="r:") as tar:
                tar.extractall(source_root, filter="data")
        sys.path.insert(0, str(source_root / "AFrame/engine"))
        from frame_engine import run as engine_run
        evidence = run_cases(engine_run)
        return {
            "source_kind": "git_archive" if revision else "working_tree",
            "source_revision": source_revision,
            "working_tree_dirty": dirty,
            **evidence,
        }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", help="Probe AFrame/engine archived from this commit.")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    evidence = json.dumps(run(args.revision), ensure_ascii=False, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(evidence, encoding="utf-8")
    print(evidence, end="")
