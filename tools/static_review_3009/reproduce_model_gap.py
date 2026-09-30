"""Reproduce the gap between interfloor geometry and its calculation model.

Uses synthetic, public inputs. No private PDF or project geometry is required.
The optional coefficient comparison changes a module callable IN MEMORY only.
It is a sensitivity demonstration, NOT a replacement structural design.

Baseline:
  python tools/static_review_3009/reproduce_model_gap.py --revision ad2a0a2 --out /tmp/model-gap-before.json
Current tree (requires a typed empty model refusal):
  python tools/static_review_3009/reproduce_model_gap.py --out /tmp/model-gap-after.json
"""
import argparse
import copy
import hashlib
import importlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile


ROOT = Path(__file__).resolve().parents[2]


def request(height=3000, floors=(0, 1500, 3000), stock=None, building_height=8):
    system = {"name": "Межэтажная"}
    if stock is not None:
        system["rail_std"] = stock
    return {
        "system": system, "sub_type": "interfloor", "parts": "frame",
        "cladding": "porcelain",
        "contours": [{"outer": [[0, 0], [3000, 0], [3000, height], [0, height]]}],
        "joints_x": [300, 900, 1500, 2100, 2700], "floors_y": list(floors),
        "corners_x": [0],
        "calc": {"wind_region": "II", "terrain": "B", "height": building_height,
                 "q_clad": 25, "offset": 170, "na_max": 3000},
    }


def topology(result):
    """Geometric intersections, not proof of an actual structural support."""
    tolerance = float(result["system_used"]["rail_gap"]) / 2.0 + 1.0
    members = []
    for rail in result["rails"]:
        if rail["kind"] != "НСП" or rail["x"] != 1500:
            continue
        points = sorted(set(h["y"] for h in result["hrails"]
                            if h["kind"] == "НГП" and h["x0"] <= rail["x"] <= h["x1"]
                            and rail["y0"] - tolerance <= h["y"] <= rail["y1"] + tolerance))
        members.append({"rail": rail, "intersection_y": points,
                        "geometric_span_count": max(0, len(points) - 1),
                        "intervals_mm": [b - a for a, b in zip(points, points[1:])],
                        "free_ends_mm": [max(0, points[0] - rail["y0"]),
                                         max(0, rail["y1"] - points[-1])] if points else None,
                        "endpoint_match_tolerance_mm": tolerance})
    return {"center_x": 1500, "members": members,
            "fittings": [f for f in result["fittings"] if f["x"] == 1500],
            "bracket_fields": sorted({k for b in result["brackets"] for k in b}),
            "bracket_kinds": sorted({b["kind"] for b in result["brackets"]}),
            "interpretation": "Intersections and kind names do not establish fixed/sliding or moment transfer."}


def exercise(plan, calc, name, req, numerical=False):
    result = plan(copy.deepcopy(req))
    record = {"name": name, "request": req, "ok": result["ok"]}
    if not result["ok"]:
        assert result.get("error_code") == "E_CALC_MODEL_UNCONFIRMED", result
        assert not any(result.get(k) for k in ("rails", "hrails", "brackets", "clamps", "fittings")), result
        assert not result.get("calc_report"), result
        assert result.get("static_model", {}).get("status") == "not_verified", result
        manual = copy.deepcopy(req)
        manual.pop("calc")
        manual_result = plan(manual)
        assert manual_result["ok"] and not manual_result.get("calc_report"), manual_result
        record.update({"status": "typed_empty_model_refusal", "error_code": result["error_code"],
                       "error": result["error"], "empty_geometry": True,
                       "static_model": result["static_model"],
                       "manual_control": topology(manual_result),
                       "coefficient_comparison": "Not run: the planner did not approve this model."})
        return record
    report = result["calc_report"]
    assert report["row"]["passed"] and report["corner"]["passed"], report
    assert report["layout_verification"]["passed"], report
    record.update({"status": "baseline_model_gap_reproduced", "topology": topology(result),
                   "resolved_inputs": report["inputs"], "calc_report": report,
                   "used_coefficients": list(calc.spans_const("interfloor", report["inputs"]["rail_len"],
                                                              report["inputs"]["rail_len"]))})
    if numerical:
        members = record["topology"]["members"]
        assert len(members) == 1 and members[0]["intersection_y"] == [0, 1500, 3000], members
        assert members[0]["free_ends_mm"] == [0, 0], members
        interval = report["layout_verification"]["zones"]["corner"]["max_step"]
        assert interval == 675.0, report
        current = calc.calc_chain(report["inputs"], interval, "corner")
        original = calc.spans_const
        try:
            calc.spans_const = lambda *_args: calc.SPAN_CONST[2]
            alternative = calc.calc_chain(report["inputs"], interval, "corner")
        finally:
            calc.spans_const = original
        assert current["passed"] and not alternative["passed"], (current, alternative)
        record["coefficient_comparison"] = {
            "not_replacement_design": True,
            "scope": "Sensitivity of the same chain at an ACTUALLY EMITTED corner interval; no support model is approved.",
            "actual_interval_mm": interval,
            "original_coefficients": list(calc.SPAN_CONST["multi"]),
            "comparison_existing_coefficients": list(calc.SPAN_CONST[2]),
            "original": current, "comparison": alternative,
        }
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", help="Read this existing local git revision; no fetch or checkout.")
    parser.add_argument("--expect", choices=("baseline", "refusal"),
                        help="Required outcome; default: baseline with --revision, refusal for the working tree.")
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="aframe-model-gap-") as temporary:
        source = ROOT
        revision = "working-tree"
        if args.revision:
            revision = subprocess.check_output(["git", "rev-parse", args.revision + "^{commit}"],
                                               cwd=ROOT, text=True).strip()
            archive = subprocess.check_output(["git", "archive", "--format=tar", revision, "AFrame/engine"], cwd=ROOT)
            source = Path(temporary)
            with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
                tar.extractall(source, filter="data")
        engine = source / "AFrame/engine"
        sys.path.insert(0, str(engine))
        plan = importlib.import_module("frame_plan").frame_plan
        calc = importlib.import_module("frame_calc")
        cases = [exercise(plan, calc, "two_spans_on_one_3000_member", request(), numerical=True),
                 exercise(plan, calc, "single_span_with_terminal_intersections",
                          request(floors=(0, 3000), building_height=6)),
                 exercise(plan, calc, "6000_with_10mm_splice",
                          request(height=6000, floors=(0, 3000, 6000), building_height=6)),
                 exercise(plan, calc, "6000_unsplit_custom_member",
                          request(height=6000, floors=(0, 3000, 6000), stock=6000, building_height=6))]
        statuses = {c["status"] for c in cases}
        assert len(statuses) == 1, statuses
        expectation = args.expect or ("baseline" if args.revision else "refusal")
        expected_status = ("baseline_model_gap_reproduced" if expectation == "baseline"
                           else "typed_empty_model_refusal")
        assert statuses == {expected_status}, ("Unexpected planner outcome", expected_status, statuses)
        if cases[0]["ok"]:
            assert len(cases[2]["topology"]["members"]) == 2, cases[2]
            assert len(cases[3]["topology"]["members"]) == 1, cases[3]
        hash_files = ["frame_plan.py", "frame_calc.py", "systems.json"]
        if (engine / "frame_topology.py").is_file():
            hash_files.append("frame_topology.py")
        evidence = {"revision": revision, "expected_outcome": expected_status, "source_sha256": {
            name: hashlib.sha256((engine / name).read_bytes()).hexdigest()
            for name in hash_files},
            "not_replacement_design": True, "synthetic_public_geometry": True,
            "cases": cases}
        if args.out:
            args.out.parent.mkdir(parents=True, exist_ok=True)
            args.out.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print("Model-gap probe: 4/4 PASS; " + next(iter(statuses)))


if __name__ == "__main__":
    main()
