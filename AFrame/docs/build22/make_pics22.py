# -*- coding: utf-8 -*-
"""Картинки к PDF сборки №22 — из настоящих прогонов движков.
Запуск: PYTHONUTF8=1 python3 AFrame/docs/build22/make_pics22.py"""
import json
import os
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from matplotlib.patches import Polygon as MP  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
for sub in ("AFrame/engine", "AClad/engine", "Facades/engine"):
    sys.path.insert(0, os.path.join(ROOT, sub))
import frame_engine as fre   # noqa: E402
import clad_engine as ce     # noqa: E402
import facades_engine as fe  # noqa: E402


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def rt(o):
    return json.loads(json.dumps(o, ensure_ascii=False))


def pic_joints():
    r = fe.run({"op": "zones", "units": "mm", "cladding": "КГ", "zone_prefix": "Ф-", "start_index": 1,
                "contours": [{"id": "A0", "pts": rect(0, 0, 9000, 1200)},
                             {"id": "B0", "pts": rect(0, 1200, 9000, 4200)},
                             {"id": "B1", "pts": rect(1300, 2100, 2700, 3700)},
                             {"id": "B2", "pts": rect(5100, 2100, 6800, 3700)}]})
    parts = {p["id"]: p for p in r["zones_full"]}
    zs = sorted(parts)
    clad = ce.run(rt({"op": "cladding", "tile": {"w": 600, "h": 600}, "gap": {"v": 10, "h": 10},
                      "origin": {"y": 0.0}, "vjoints": [], "hjoints": [],
                      "zones": [{"zone_id": z, "zone": parts[z]} for z in zs]}))
    own = {pz["zone_id"]: pz for pz in clad["per_zone"]}
    base = {"op": "frame", "sub_type": "vertical", "system": "Standart", "contours": [], "floors_y": []}
    was = fre.run(rt(dict(base, joints_x=clad["joints_x"], rows_y=clad["rows_y"],
                          zones=[{"zone_id": z, "zone": parts[z]} for z in zs])))
    now = fre.run(rt(dict(base, joints_x=clad["joints_x"], rows_y=clad["rows_y"],
                          zones=[{"zone_id": z, "zone": parts[z], "joints_x": own[z]["joints_x"],
                                  "rows_y": own[z]["rows_y"]} for z in zs])))
    fig, axs = plt.subplots(1, 2, figsize=(11, 4.0), sharey=True)
    for ax, res, title in ((axs[0], was, "было: оси всех зон одним списком"),
                           (axs[1], now, "стало: у каждой зоны свои оси")):
        n = 0
        for t in res["rails"]:
            mine = {round(x, 1) for x in own[t["zone"]]["joints_x"]}
            other = {round(x, 1) for z2 in zs if z2 != t["zone"] for x in own[z2]["joints_x"]}
            foreign = round(t["x"], 1) in other and round(t["x"], 1) not in mine
            ax.plot([t["x"], t["x"]], [t["y0"], t["y1"]], color="#d62728" if foreign else "#2ca02c", lw=1.3)
            n += 1
        for z in zs:
            ax.add_patch(MP(parts[z]["outer"]["pts"], closed=True, facecolor="none", edgecolor="black", lw=1.2))
            for o in parts[z]["openings"]:
                ax.add_patch(MP(o["poly"]["pts"], closed=True, facecolor="white", edgecolor="black", lw=1.0))
        ax.set_title("%s — стоек %d" % (title, n), fontsize=10)
        ax.set_aspect("equal")
        ax.set_xlim(-200, 9200)
    fig.suptitle("Цоколь под этажом с окнами: красное — стойки по швам ЧУЖОЙ зоны", fontsize=11)
    fig.tight_layout()
    fig.savefig(os.path.join(HERE, "joints_fix.png"), dpi=85)
    plt.close(fig)


def pic_phantom():
    wall, win = rect(0, 0, 8000, 5000), rect(-50, 1000, 1450, 2500)
    r = fe.run({"op": "zones", "units": "mm", "cladding": "КГ",
                "contours": [{"id": "W", "pts": wall}, {"id": "O", "pts": win}]})
    fig, axs = plt.subplots(1, 2, figsize=(11, 3.9))
    for ax in axs:
        ax.add_patch(MP(wall, closed=True, facecolor="#cfe3f5", edgecolor="#1f77b4", lw=1.5))
        ax.set_aspect("equal")
        ax.set_xlim(-1700, 8300)
        ax.set_ylim(-300, 5300)
    axs[0].add_patch(MP(win, closed=True, facecolor="#f7c6c6", edgecolor="#d62728", lw=1.5, hatch="//"))
    axs[0].text(2400, 3900, "Ф-1: 40.000 м² (окно не вычтено)", fontsize=9)
    axs[0].text(-30, 2650, "Ф-2: 2.25 м² — окно стало зоной", fontsize=9, color="#d62728")
    axs[0].set_title("было: молча две зоны", fontsize=10)
    axs[1].add_patch(MP(win, closed=True, facecolor="none", edgecolor="#d62728", lw=1.5, ls="--"))
    msg = [i["msg"] for i in r["issues"] if i["code"] == "E_CONTOUR_CROSSES"]
    axs[1].text(-1500, 4600, "Ошибка: " + (msg[0][:62] + "…" if msg else "—"), fontsize=8.5, color="#d62728",
                bbox=dict(facecolor="white", edgecolor="#d62728"))
    axs[1].text(2400, 3200, "зона одна; окно не учтено,\nпока не поправлена обводка", fontsize=9)
    axs[1].set_title("стало: сообщение с именами контуров", fontsize=10)
    fig.suptitle("ATFZONE: окно нарисовано с нахлёстом за край стены (50 мм)", fontsize=11)
    fig.tight_layout()
    fig.savefig(os.path.join(HERE, "phantom_fix.png"), dpi=85)
    plt.close(fig)


if __name__ == "__main__":
    pic_joints()
    pic_phantom()
    print("ok", sorted(f for f in os.listdir(HERE) if f.endswith(".png")))
