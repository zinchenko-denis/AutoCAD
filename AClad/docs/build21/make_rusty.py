import sys, json
import os
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "engine"))
import clad_engine as ce
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Rectangle
def rect(x0, y0, x1, y1): return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]
base = {"op": "tile_pattern", "tile": {"w": 600, "h": 300}, "gap": {"v": 10, "h": 10}, "types": ["КГ"],
        "anchor": {"h": "L", "v": "B", "center": "tile", "ref": "bbox"}, "gap_around": True,
        "bond": {"kind": "alternate", "value": "1/2", "units": "frac"},
        "contours": [{"id": "A", "pts": rect(0, 0, 6000, 3000)}, {"id": "W", "pts": rect(3600, 700, 4800, 2200)}]}
fig, axs = plt.subplots(1, 2, figsize=(13, 3.9))
for ax, (title, extra) in zip(axs, (("без принудительных рустов", {}),
                                    ("русты: вертикальный x=2500, горизонтальный по верху окна", {"vjoints": [2500], "hjoints": [2200]}))):
    r = ce.run(json.loads(json.dumps(dict(base, **extra))))
    for p in r["pieces"]:
        ax.add_patch(Rectangle((p["x"], p["y"]), p["w"], p["h"], facecolor="#e8d5b0" if p["full"] else "#b8864b", edgecolor="#7a5a2e", lw=0.4))
    ax.add_patch(Rectangle((3600, 700), 1200, 1500, facecolor="white", edgecolor="black", lw=1))
    if extra:
        ax.axvline(2500, color="red", lw=1.2, ls="--"); ax.axhline(2205, color="red", lw=1.2, ls="--")
        ax.text(2530, 2950, "руст", color="red", fontsize=9, va="top")
        ax.text(4850, 2240, "руст над окном", color="red", fontsize=9)
    ax.set_xlim(-100, 6100); ax.set_ylim(-100, 3100); ax.set_aspect("equal"); ax.set_title(title, fontsize=10)
fig.suptitle("ATTILE, отсчёт снизу-слева, разбежка 1/2: светлые — целые, тёмные — подрезка. За рустом раскладка заново от руста", fontsize=10)
fig.tight_layout(); fig.savefig("" + os.path.join(os.path.dirname(os.path.abspath(__file__)), "rusty.png") + "", dpi=85)
print("ok")
