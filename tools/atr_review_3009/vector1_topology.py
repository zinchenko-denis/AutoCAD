"""Regressions for NSP free ends, unconfirmed static model and manual controls.

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


def case(name, height, floors, stock=None, calculation=True, refusal=None):
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
        expected_code = ("E_CALC_MODEL_UNCONFIRMED" if refusal == "model"
                         else "E_CALC_TOPOLOGY_UNSUPPORTED")
        assert result.get("error_code") == expected_code, result
        for key in ("rails", "hrails", "brackets", "clamps", "fittings"):
            assert result.get(key, []) == [], (key, result)
        model = result["static_model"]
        assert model["status"] == "not_verified" and model["pieces_merged"] is False, model
        assert model["fixed_sliding"] == model["splice_continuity"] == "not_modeled", model
        assert "calc_report" not in result, result
        assert model["geometric_screening"] == {
            "status": "refused", "reasons": [{"reason": "interfloor_model_unconfirmed"}]}, model
        if refusal == "model":
            assert result["unsupported"] == [{"reason": "interfloor_model_unconfirmed"}], result
            center = [m for m in model["members"] if m["geometry"]["x"] == 1500]
            assert len(center) == 1 and center[0]["support_y"] == floors, center
            assert center[0]["support_count"] == 5 and center[0]["span_count"] == 4, center
            assert center[0]["intervals"] == [750.0] * 4, center
            assert center[0]["bottom_free"] == center[0]["top_free"] == 0.0, center
            return {"name": name, "request": request, "ok": False,
                    "error_code": result["error_code"], "empty_geometry": True,
                    "static_model": model, "error": result["error"]}
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
    assert not calculation, "Interfloor calculation cannot claim static-model approval"
    return record


def run():
    records = [case("default_two_inner_supports", 3000, [1000, 2000], refusal="free_end"),
               case("default_clustered_supports", 3000, [1400, 1600], refusal="free_end"),
               case("custom_long_member", 9000, [3000, 6000], 9000, refusal="free_end"),
               case("end_supported_model_unconfirmed", 3000, [0, 750, 1500, 2250, 3000], refusal="model")]
    for name, height, floors, stock in [("manual_two_inner_supports", 3000, [1000, 2000], None),
                                         ("manual_clustered_supports", 3000, [1400, 1600], None),
                                         ("manual_long_member", 9000, [3000, 6000], 9000)]:
        manual = case(name, height, floors, stock, calculation=False)
        assert manual["members"][0]["support_count"] == 2
        assert manual["members"][0]["free_end_lengths"] == [floors[0], height - floors[-1]]
        records.append(manual)
    manual = case("manual_end_supported_control", 3000, [0, 750, 1500, 2250, 3000], calculation=False)
    assert manual["members"][0]["free_end_lengths"] == [0.0, 0.0]
    assert manual["members"][0]["support_count"] == 5
    assert manual["members"][0]["supported_spans"] == [750.0] * 4
    records.append(manual)
    return {"interpretation": "Three preserved free-end refusals; one unconfirmed-model refusal despite five intersections; four manual project-layout controls.",
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
    print("Vector1 topology regression: %d/8 PASS (3 free-end refusals, 1 model refusal, 4 manual controls)" % result["passed_cases"])
    if not args.out:
        print(evidence)
