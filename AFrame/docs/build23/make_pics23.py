# -*- coding: utf-8 -*-
"""Картинки к PDF сборки №23 — настоящие прогоны движков: «было» — код
aa47a78 (git show … в каталог OLD), «стало» — текущий.
Запуск: PYTHONUTF8=1 python3 AFrame/docs/build23/make_pics23.py [OLD_DIR]"""
import importlib.util
import json
import os
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from matplotlib.patches import Polygon as MP, Rectangle  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
OLD = sys.argv[1] if len(sys.argv) > 1 else "/tmp/old_ce"


def load(dirpath, name, modname):
    spec = importlib.util.spec_from_file_location(modname, os.path.join(dirpath, name))
    m = importlib.util.module_from_spec(spec)
    sys.path.insert(0, dirpath)
    spec.loader.exec_module(m)
    sys.path.pop(0)
    return m


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def pic_u():
    U = [[0, 0], [2600, 0], [2600, 2400], [2500, 2400], [2500, 100], [100, 100], [100, 2400], [0, 2400]]
    inner = rect(300, 300, 2300, 2200)
    req = {"op": "tile_pattern", "tile": {"w": 200, "h": 100}, "gap": {"v": 5, "h": 5}, "types": ["КГ"],
           "bond": {"kind": "alternate", "value": "1/2", "units": "frac"}, "merge_touching": False,
           "contours": [{"id": "U", "pts": U}, {"id": "I", "pts": inner}]}
    for k in ("clad_engine", "cladding_plan", "tile_pattern"):
        sys.modules.pop(k, None)
    sys.path.insert(0, OLD)
    old = load(OLD, "clad_engine.py", "clad_engine")
    r_old = old.run(json.loads(json.dumps(req)))
    sys.path.remove(OLD)
    for k in ("clad_engine", "cladding_plan", "tile_pattern"):
        sys.modules.pop(k, None)
    newdir = os.path.join(ROOT, "AClad", "engine")
    sys.path.insert(0, newdir)
    new = load(newdir, "clad_engine.py", "clad_engine")
    r_new = new.run(json.loads(json.dumps(req)))
    sys.path.remove(newdir)
    fig, axs = plt.subplots(1, 2, figsize=(10, 4.4))
    for ax, r, title in ((axs[0], r_old, "было"), (axs[1], r_new, "стало")):
        for p in r["pieces"]:
            ax.add_patch(Rectangle((p["x"], p["y"]), p["w"], p["h"], facecolor="#e8d5b0" if p["full"] else "#b8864b",
                                   edgecolor="#7a5a2e", lw=0.3))
        ax.add_patch(MP(U, closed=True, facecolor="none", edgecolor="#1f77b4", lw=1.4))
        ax.add_patch(MP(inner, closed=True, facecolor="none", edgecolor="#1f77b4", lw=1.4))
        a = sum(p["area"] for p in r["pieces"]) / 1e6
        ax.set_title("%s: разложено %.2f м² (%d зон)" % (title, a, len(r.get("per_zone", []))), fontsize=10)
        ax.set_aspect("equal")
        ax.set_xlim(-100, 2700)
        ax.set_ylim(-100, 2500)
    fig.suptitle("ATTILE: U-образная полоса вокруг отдельной зоны — раньше полоса пропадала", fontsize=11)
    fig.tight_layout()
    fig.savefig(os.path.join(HERE, "u_fix.png"), dpi=85)
    plt.close(fig)


def pic_calc():
    sys.path.insert(0, os.path.join(ROOT, "AFrame", "engine"))
    import frame_engine as fre
    req = {"op": "frame", "sub_type": "vertical", "system": "Вектор-1", "corners_x": [0, 3100],
           "contours": [{"id": "A", "pts": rect(0, 0, 3100, 3000)}],
           "joints_x": [100, 200, 300, 400, 1600, 2800], "rows_y": [600.0 * k for k in range(1, 5)],
           "calc": {"wind_region": "II", "terrain": "B", "height": 30, "q_clad": 25, "offset": 230, "na_max": 3000}}
    r = fre.run(json.loads(json.dumps(req)))
    fig, ax = plt.subplots(figsize=(6.2, 5.2))
    ax.add_patch(MP(rect(0, 0, 3100, 3000), closed=True, facecolor="#f2efe6", edgecolor="black", lw=1.2))
    for t in r["rails"]:
        ax.plot([t["x"], t["x"]], [t["y0"], t["y1"]], color="#2ca02c", lw=1.6)
    for b in r["brackets"]:
        ax.plot(b["x"], b["y"], "s", color="#d62728", ms=4)
    st = r["calc_report"]["steps"]
    ax.set_title("Оси раскладки 100/200/300/400/1600/2800 мм: шаг кронштейнов\n"
                 "рядовая %d, угловая %d мм (было 800/800 — по грузовой ширине 100)" % (st["main"], st["corner"]),
                 fontsize=9.5)
    ax.set_aspect("equal")
    ax.set_xlim(-200, 3300)
    ax.set_ylim(-200, 3200)
    fig.tight_layout()
    fig.savefig(os.path.join(HERE, "calc_fix.png"), dpi=85)
    plt.close(fig)


if __name__ == "__main__":
    pic_u()
    pic_calc()
    print("ok", sorted(f for f in os.listdir(HERE) if f.endswith(".png")))
