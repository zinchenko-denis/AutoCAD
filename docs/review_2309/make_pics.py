# -*- coding: utf-8 -*-
"""Картинки к docs/REVIEW_23.09.md — из настоящих прогонов движков.
Запуск из корня: PYTHONUTF8=1 python3 docs/review_2309/make_pics.py
Нужны matplotlib, shapely."""
import json
import os
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from matplotlib.patches import Polygon as MPoly  # noqa: E402
from shapely.geometry import LineString, Point, Polygon  # noqa: E402

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
OUT = os.path.dirname(os.path.abspath(__file__))
for sub in ("AFrame/engine", "AClad/engine", "Facades/engine"):
    sys.path.insert(0, os.path.join(ROOT, sub))
import frame_engine as fre   # noqa: E402
import clad_engine as ce     # noqa: E402
import facades_engine as fe  # noqa: E402

RED, GREEN, GREY, BLUE = "#d62728", "#2ca02c", "#7f7f7f", "#1f77b4"


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def wall_patch(ax, pts, **kw):
    ax.add_patch(MPoly(pts, closed=True, **kw))


def pic_lshape():
    L = [[0, 0], [9000, 0], [9000, 6000], [6000, 6000], [6000, 3000], [0, 3000]]
    wall = Polygon(L).buffer(1, join_style=2)
    req = {"op": "frame", "sub_type": "vertical", "system": "Standart", "zones": [],
           "contours": [{"id": "L", "pts": L}], "joints_x": [305 + 610 * k for k in range(15)],
           "rows_y": [605.0 * k for k in range(1, 10)], "floors_y": []}
    r = fre.run(json.loads(json.dumps(req)))
    fig, ax = plt.subplots(figsize=(8, 5.6))
    wall_patch(ax, L, facecolor="#f2efe6", edgecolor="black", lw=1.5)
    n_out = 0
    for s in r["rails"]:
        seg = LineString([(s["x"], s["y0"]), (s["x"], s["y1"])])
        out = seg.difference(wall).length > 1
        n_out += out
        ax.plot([s["x"], s["x"]], [s["y0"], s["y1"]], color=RED if out else GREEN, lw=2.2 if out else 1.6)
    for b in r["brackets"]:
        ax.plot(b["x"], b["y"], "s", ms=3.5, color=RED if not wall.contains(Point(b["x"], b["y"])) else "black")
    ax.set_title("ATFRAME на Г-образной стене: стойки за контуром — красным (%d из %d)"
                 % (n_out, len(r["rails"])), fontsize=11)
    ax.text(1200, 4300, "здесь стены нет:\nподсистема по ГАБАРИТУ\nконтура", fontsize=10, color=RED,
            bbox=dict(facecolor="white", edgecolor=RED, alpha=0.9))
    ax.set_aspect("equal")
    ax.set_xlim(-300, 9300)
    ax.set_ylim(-300, 6400)
    ax.set_xlabel("мм")
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, "afr_lshape.png"), dpi=90)
    plt.close(fig)


def pic_bleed():
    r = fe.run({"op": "zones", "units": "mm", "cladding": "КГ", "zone_prefix": "Ф-", "start_index": 1,
                "contours": [{"id": "A0", "pts": rect(0, 0, 9000, 1200)},
                             {"id": "B0", "pts": rect(0, 1200, 9000, 4200)},
                             {"id": "B1", "pts": rect(1300, 2100, 2700, 3700)},
                             {"id": "B2", "pts": rect(5100, 2100, 6800, 3700)}]})
    parts = {p["id"]: p for p in r["zones_full"]}
    za, zb = sorted(parts)
    clad = {"op": "cladding", "tile": {"w": 600, "h": 600}, "gap": {"v": 10, "h": 10}, "origin": {"y": 0.0},
            "vjoints": [], "hjoints": []}
    own = {z: ce.run(json.loads(json.dumps(dict(clad, zones=[{"zone_id": z, "zone": parts[z]}])))) for z in (za, zb)}
    union = sorted(set(own[za]["joints_x"]) | set(own[zb]["joints_x"]))
    raw = {za: [{"id": "A0", "pts": rect(0, 0, 9000, 1200)}],
           zb: [{"id": "B0", "pts": rect(0, 1200, 9000, 4200)}, {"id": "B1", "pts": rect(1300, 2100, 2700, 3700)},
                {"id": "B2", "pts": rect(5100, 2100, 6800, 3700)}]}
    fig, axs = plt.subplots(1, 2, figsize=(11, 4.2), sharey=True)
    for ax, mode in zip(axs, ("свои оси каждой зоны", "как сейчас: оси всех меток одним списком")):
        n = 0
        for z in (za, zb):
            jz = own[z]["joints_x"] if mode.startswith("свои") else union
            f = fre.run(json.loads(json.dumps({"op": "frame", "sub_type": "vertical", "system": "Standart",
                                                "zones": [], "contours": raw[z], "joints_x": jz,
                                                "rows_y": own[z]["rows_y"], "floors_y": []})))
            n += len(f["rails"])
            for s in f["rails"]:
                foreign = mode.startswith("как") and round(s["x"], 1) not in {round(x, 1) for x in own[z]["joints_x"]}
                ax.plot([s["x"], s["x"]], [s["y0"], s["y1"]], color=RED if foreign else GREEN, lw=1.3)
        for c in raw[za] + raw[zb]:
            wall_patch(ax, c["pts"], facecolor="none", edgecolor="black", lw=1.2)
        ax.set_title("%s — стоек %d" % (mode, n), fontsize=10)
        ax.set_aspect("equal")
        ax.set_xlim(-200, 9200)
    fig.suptitle("Цоколь под этажом с окнами: чужие оси швов дают лишние стойки (красные)", fontsize=11)
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, "afr_joints_bleed.png"), dpi=90)
    plt.close(fig)


def pic_phantom():
    wall, win = rect(0, 0, 8000, 5000), rect(-50, 1000, 1450, 2500)
    r = fe.run({"op": "zones", "units": "mm", "cladding": "КГ", "contours": [{"id": "W", "pts": wall},
                                                                          {"id": "O", "pts": win}]})
    fig, ax = plt.subplots(figsize=(7.5, 4.6))
    wall_patch(ax, wall, facecolor="#cfe3f5", edgecolor=BLUE, lw=1.5)
    wall_patch(ax, win, facecolor="#f7c6c6", edgecolor=RED, lw=1.5, hatch="//")
    z = {x["zone_id"]: x for x in r["zones"]}
    ax.text(3500, 3900, "%s: %.3f м² (окно НЕ вычтено)" % ("Ф-1", z["Ф-1"]["report"]["area_net_m2"]), fontsize=10)
    ax.text(-30, 2650, "%s: %.2f м² — окно стало зоной" % ("Ф-2", z["Ф-2"]["report"]["area_net_m2"]),
            fontsize=10, color=RED)
    ax.text(3500, 3300, "должно быть: одна зона %.3f м²" % (40.0 - 1.45 * 1.5), fontsize=10, color=GREEN)
    ax.annotate("нахлёст 50 мм", xy=(-50, 1700), xytext=(-1500, 700), arrowprops=dict(arrowstyle="->"), fontsize=9)
    ax.set_title("ATFZONE: проём чуть за краем стены — молча вторая зона, ошибок нет", fontsize=11)
    ax.set_aspect("equal")
    ax.set_xlim(-1700, 8300)
    ax.set_ylim(-300, 5300)
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, "fz_phantom.png"), dpi=90)
    plt.close(fig)


if __name__ == "__main__":
    pic_lshape()
    pic_bleed()
    pic_phantom()
    print("картинки:", sorted(f for f in os.listdir(OUT) if f.endswith(".png")))
