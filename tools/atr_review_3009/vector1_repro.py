"""Read-only evidence for Vector1 ATR review; no private PDF data is embedded.

Run from any working directory. This records current engine behaviour, not
engineering acceptance. The leaf 10.5 reinforcement is conditional on the
project's fire assessment; the current request has no field for that condition.
"""
import argparse
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "AFrame/engine"))
from frame_plan import frame_plan


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def run():
    request = {
        "system": "Вектор-1", "sub_type": "vertical", "cladding": "porcelain",
        "contours": [{"outer": rect(0, 0, 3000, 4200),
                      "holes": [rect(900, 900, 2100, 2100)]}],
        "joints_x": [300, 900, 1500, 2100, 2700],
        "rows_y": list(range(300, 4200, 600)), "corners_x": [],
    }
    result = frame_plan(request)
    assert result["ok"], result
    above = [c for c in result["clamps"]
             if 600 <= c["x"] <= 2400 and 2100 <= c["y"] <= 2750]
    xs = sorted(set(c["x"] for c in above if c["y"] == 2700))
    ys = sorted(set(c["y"] for c in above if c["x"] == 1500))
    horizontal = [b - a for a, b in zip(xs, xs[1:])]
    vertical = [b - a for a, b in zip(ys, ys[1:])]
    assert horizontal == [600.0, 600.0] and vertical == [600.0]
    assert not result["hrails"] and not result["fittings"]

    floors_request = {
        "system": "Межэтажная", "sub_type": "interfloor", "cladding": "porcelain",
        "contours": [{"outer": rect(0, 0, 3000, 9000)}],
        "joints_x": [300, 900, 1500, 2100, 2700],
        "rows_y": list(range(300, 9000, 600)), "floors_y": [3000, 6000],
        "corners_x": [],
    }
    floors_result = frame_plan(floors_request)
    assert floors_result["ok"], floors_result
    center_rails = [r for r in floors_result["rails"] if r["x"] == 1500]
    center_fittings = [f for f in floors_result["fittings"] if f["x"] == 1500]
    return {
        "scope": "current geometry outputs; not full structural/fire acceptance",
        "above_opening": {
            "request": request, "ok": result["ok"], "summary": result["summary"],
            "clamps_in_leaf_10_5_zone": above,
            "horizontal_gaps_at_y2700": horizontal, "vertical_gaps_at_x1500": vertical,
            "notes": result["notes"], "hrails": result["hrails"], "fittings": result["fittings"],
        },
        "interfloor_joint": {
            "request": floors_request, "ok": floors_result["ok"],
            "center_rails": center_rails, "center_fittings": center_fittings,
            "horizontal_support_levels": sorted(set(h["y"] for h in floors_result["hrails"])),
            "notes": floors_result["notes"],
        },
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    evidence = json.dumps(run(), ensure_ascii=False, indent=2)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(evidence + "\n", encoding="utf-8")
    print(evidence)
