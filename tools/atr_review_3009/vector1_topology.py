"""Regression for the audited NSP free ends and a genuinely supported control.

No alternative structural model is introduced. End distances and support counts
are geometric facts; constants come from the actual frame_calc implementation.
Before-fix evidence: docs/atr_review_3009/logs/vector1_topology_before.json.
"""
import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "AFrame/engine"))
from frame_plan import frame_plan
from frame_calc import spans_const


def case(name, height, floors, stock=None, calculation=True, refusal=False):
    system = {"name": "Межэтажная"}
    if stock is not None:
        system["rail_std"] = stock
    request = {
        "system": system, "sub_type": "interfloor", "parts": "frame", "cladding": "porcelain",
        "contours": [{"outer": [[0, 0], [3000, 0], [3000, height], [0, height]]}],
        "joints_x": [300, 900, 1500, 2100, 2700], "floors_y": floors, "corners_x": [],
        "calc": {"wind_region": "II", "terrain": "B", "height": 6,
                 "q_clad": 25, "offset": 170, "na_max": 3000},
    }
    if not calculation:
        del request["calc"]
    result = frame_plan(request)
    if refusal:
        assert not result["ok"], result
        assert result.get("error_code") == "E_CALC_TOPOLOGY_UNSUPPORTED", result
        for key in ("rails", "hrails", "brackets", "clamps", "fittings"):
            assert result.get(key, []) == [], (key, result)
        center = [r for r in result["unsupported"] if r["x"] == 1500]
        assert len(center) == 1 and center[0]["reason"] == "unsupported_free_end", result
        assert center[0]["support_y"] == floors, center
        assert center[0]["bottom_free"] == floors[0] and center[0]["top_free"] == height - floors[-1], center
        return {"name": name, "request": request, "ok": False,
                "error_code": result["error_code"], "empty_geometry": True,
                "unsupported_center": center, "error": result["error"]}
    assert result["ok"], result
    report = result.get("calc_report")
    assert bool(report) == calculation, result
    assert result["rails"] and result["hrails"] and result["brackets"], result
    assert not result["clamps"], result  # parts=frame, including manual controls
    members = []
    for rail in result["rails"]:
        if rail["kind"] != "НСП" or rail["x"] != 1500:
            continue
        supports = sorted(set(h["y"] for h in result["hrails"]
                              if h["kind"] == "НГП" and h["x0"] <= rail["x"] <= h["x1"]
                              and rail["y0"] <= h["y"] <= rail["y1"]))
        members.append({
            "rail": rail, "supports_y": supports, "support_count": len(supports),
            "supported_span_count": max(0, len(supports) - 1),
            "supported_spans": [b - a for a, b in zip(supports, supports[1:])],
            "free_end_lengths": [supports[0] - rail["y0"], rail["y1"] - supports[-1]] if supports else None,
        })
    record = {
        "name": name, "request": request, "ok": result["ok"],
        "members": members, "calculation_requested": calculation,
    }
    assert len(members) == 1, members
    if calculation:
        assert report["row"]["passed"] and report["corner"]["passed"], report
        assert report["layout_verification"]["passed"], report
        assert all(check["ok"] for zone in ("row", "corner") for check in report[zone]["checks"]), report
        record.update({
            "rail_len_used": report["inputs"]["rail_len"], "steps": report["steps"],
            "cM_k_cf": spans_const("interfloor", report["inputs"]["rail_len"], report["inputs"]["rail_len"]),
            "row_passed": report["row"]["passed"], "corner_passed": report["corner"]["passed"],
            "layout_verification_passed": report["layout_verification"]["passed"], "method": report["method"],
        })
    return record


def run():
    records = [case("default_two_inner_supports", 3000, [1000, 2000], refusal=True),
               case("default_clustered_supports", 3000, [1400, 1600], refusal=True),
               case("custom_long_member", 9000, [3000, 6000], 9000, refusal=True),
               case("end_supported_control", 3000, [0, 750, 1500, 2250, 3000])]
    assert records[3]["members"][0]["free_end_lengths"] == [0.0, 0.0]
    assert records[3]["members"][0]["support_count"] == 5
    assert records[3]["members"][0]["supported_spans"] == [750.0] * 4
    for name, height, floors, stock in [("manual_two_inner_supports", 3000, [1000, 2000], None),
                                         ("manual_clustered_supports", 3000, [1400, 1600], None),
                                         ("manual_long_member", 9000, [3000, 6000], 9000)]:
        manual = case(name, height, floors, stock, calculation=False)
        assert manual["members"][0]["support_count"] == 2
        assert manual["members"][0]["free_end_lengths"] == [floors[0], height - floors[-1]]
        records.append(manual)
    return {"interpretation": "Three typed empty refusals; supported calc control; three manual project-layout controls.",
            "passed_cases": len(records),
            "cases": records}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    result = run()
    evidence = json.dumps(result, ensure_ascii=False, indent=2)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(evidence + "\n", encoding="utf-8")
    print("Vector1 topology regression: %d/7 PASS (3 typed empty refusals, 1 calc control, 3 manual controls)" % result["passed_cases"])
    if not args.out:
        print(evidence)
