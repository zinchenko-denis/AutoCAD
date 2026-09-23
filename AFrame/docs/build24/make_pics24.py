# -*- coding: utf-8 -*-
"""Картинки к PDF сборки №24: раскладка по образцу — настоящий прогон
движка ATTILE (образец: 3 типа плиток, раппорт 3×3; тип = слой образца).
Остальные картинки — build23 (make_pics23.py) и снимок окна со стенда mono.
Запуск: PYTHONUTF8=1 python3 AFrame/docs/build24/make_pics24.py"""
import json
import os
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from matplotlib.patches import Polygon as MP, Rectangle  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "AClad", "engine"))
import clad_engine as ce  # noqa: E402

COL = {"Облицовка_серая": "#9e9e9e", "Облицовка_белая": "#f4f1ea", "Облицовка_графит": "#4a4a4a"}


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def main():
    W, H, G = 600, 300, 10
    order = [["Облицовка_серая", "Облицовка_белая", "Облицовка_графит"],
             ["Облицовка_белая", "Облицовка_графит", "Облицовка_серая"],
             ["Облицовка_графит", "Облицовка_серая", "Облицовка_белая"]]
    sx, sy = -3000, 0
    sample = []
    for r in range(3):
        for c in range(3):
            x0 = sx + c * (W + G)
            y0 = sy + r * (H + G)
            sample.append({"x0": x0, "y0": y0, "x1": x0 + W, "y1": y0 + H, "type": order[r][c]})
    req = {"op": "tile_pattern", "tile": {"w": W, "h": H}, "gap": {"v": G, "h": G},
           "types": sorted(COL), "sample": sample, "bond": {"kind": "none"},
           "anchor": {"h": "L", "v": "B", "center": "tile", "ref": "bbox"}, "gap_around": True,
           "contours": [{"id": "A", "pts": rect(0, 0, 6000, 3000)},
                        {"id": "W", "pts": rect(2400, 900, 3600, 2200)}]}
    res = ce.run(json.loads(json.dumps(req)))
    fig, ax = plt.subplots(figsize=(11, 3.9))
    for q in sample:
        ax.add_patch(Rectangle((q["x0"], q["y0"]), W, H, facecolor=COL[q["type"]], edgecolor="black", lw=0.8))
    ax.text(sx, sy + 3 * (H + G) + 120, "образец: блоки на трёх слоях", fontsize=9)
    for p in res.get("pieces", []):
        t = p.get("type")
        ax.add_patch(Rectangle((p["x"], p["y"]), p["w"], p["h"], facecolor=COL.get(t, "#dddddd"),
                               edgecolor="#555555", lw=0.3, alpha=1.0 if p["full"] else 0.75))
    ax.add_patch(MP(rect(2400, 900, 3600, 2200), closed=True, facecolor="white", edgecolor="black", lw=1.0))
    ax.add_patch(MP(rect(0, 0, 6000, 3000), closed=True, facecolor="none", edgecolor="#1f77b4", lw=1.2))
    ax.text(0, 3150, "раскладка: каждый тип — на СЛОЕ своего образца, тем же блоком", fontsize=9)
    ax.set_aspect("equal")
    ax.set_xlim(-3200, 6200)
    ax.set_ylim(-200, 3500)
    ax.axis("off")
    fig.tight_layout()
    fig.savefig(os.path.join(HERE, "sample_layers.png"), dpi=85)
    plt.close(fig)
    print("ok", len(res.get("pieces", [])), res.get("ok"), res.get("error"))


if __name__ == "__main__":
    main()
