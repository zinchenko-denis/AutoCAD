# -*- coding: utf-8 -*-
"""Картинка к PDF №29: настоящая раскладка фронтона движком ATTILE (tile_pattern, 29.09v) —
фронтон 6 × 3,4 м с окном, плитка 290×82, руст 7: целые плитки, подрезка, плитки по скату.
Запуск (из корня): PYTHONUTF8=1 python3 AFrame/docs/make_pics29.py → build29/gable_attile.png"""
import os
import sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "AClad", "engine"))
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Polygon as MP
import tile_pattern as tp

G = [[0, 0], [6000, 0], [6000, 2400], [3000, 3400], [0, 2400]]
W = [[2400, 700], [3600, 700], [3600, 2000], [2400, 2000]]
r = tp.tile_pattern({"tile": {"w": 290.0, "h": 82.0}, "gap": {"v": 7.0, "h": 7.0}, "datum": {"mode": "bbox"},
                     "min_piece": 10.0, "contours": [{"id": "Ф", "outer": G, "holes": [W]}]})
assert r["ok"], r
fig, ax = plt.subplots(figsize=(10, 6.2), dpi=130)
for p in r["pieces"]:
    pts = p["rings"][0] if p.get("rings") else [[p["x"], p["y"]], [p["x"] + p["w"], p["y"]],
                                                [p["x"] + p["w"], p["y"] + p["h"]], [p["x"], p["y"] + p["h"]]]
    col = "#c9a57a" if p["full"] else ("#e05a3a" if p.get("slope") else "#f2d49b")
    ax.add_patch(MP(pts, closed=True, fc=col, ec="#6b5a48", lw=0.25))
ax.add_patch(MP(G, closed=True, fill=False, ec="#222", lw=1.4))
ax.add_patch(MP(W, closed=True, fill=False, ec="#1c5fb0", lw=1.2))
ax.set_xlim(-150, 6150); ax.set_ylim(-150, 3550); ax.set_aspect("equal"); ax.axis("off")
pz = r["per_zone"][0]
n_s = sum(1 for p in r["pieces"] if p.get("slope"))
ax.set_title("ATTILE, фронтон 6,0 × 3,4 м с окном: целых %d, подрезка %d (из них по скату %d)"
             % (pz["full"], pz["cut"], n_s), fontsize=10)
out = os.path.join(HERE, "build29", "gable_attile.png")
fig.savefig(out, bbox_inches="tight")
print(out, "| целых", pz["full"], "подрезка", pz["cut"], "по скату", n_s, "| рядов до %.0f" % max(pz["rows_y"]))
