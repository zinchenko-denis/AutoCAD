# -*- coding: utf-8 -*-
"""Картинки к PDF №30 (30.09): (1) межэтажная — НСП вдоль боковых откосов (ответ Германа на (а)
PDF №29; АТР «Вектор-1», тип 4), (2) ATCLAD — фронтон под керамогранит (ответ на (б)).
Настоящие прогоны движков frame_plan и cladding_plan. Запуск (из корня репо):
PYTHONUTF8=1 python3 AFrame/docs/make_pics30.py → AFrame/docs/build30/*.png (нужен matplotlib)."""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "AFrame", "engine"))
sys.path.insert(0, os.path.join(ROOT, "AClad", "engine"))
import matplotlib                     # noqa: E402
matplotlib.use("Agg")
import matplotlib.pyplot as plt       # noqa: E402
from matplotlib.patches import Polygon as MPoly, Rectangle  # noqa: E402
from frame_plan import frame_plan     # noqa: E402
from cladding_plan import cladding_plan  # noqa: E402

OUT = os.path.join(HERE, "build30")


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def pic_interfloor():
    wins = [rect(1000, 3800, 2000, 5300), rect(2150, 3800, 3150, 5300), rect(3650, 3800, 4650, 5300),
            rect(1000, 800, 2000, 2300)]
    req = {"system": "Межэтажная", "sub_type": "interfloor", "cladding": "clinker", "tile_step_x": 600,
           "exact_step": True, "parts": "frame", "floors_y": [3000, 6000],
           "rows_y": [88 * k for k in range(1, 102)],
           "contours": [{"outer": rect(0, 0, 6000, 9000), "holes": wins}]}
    res = frame_plan(req)
    assert res["ok"], res.get("error")
    base = set(round(x, 1) for x in (100, 700, 1300, 1900, 2500, 3100, 3700, 4300, 4900, 5500, 5900))
    fig, ax = plt.subplots(figsize=(8.2, 9.0))
    ax.add_patch(Rectangle((0, 0), 6000, 9000, fill=False, lw=1.2, ec="#333"))
    for w in wins:
        ax.add_patch(Rectangle((w[0][0], w[0][1]), w[1][0] - w[0][0], w[2][1] - w[0][1],
                               fc="#dfe7ef", ec="#667", lw=0.8))
    for f in (3000, 6000):
        ax.plot([0, 6000], [f, f], ls=(0, (6, 4)), color="#999", lw=0.8)
        ax.text(6060, f, "перекрытие %d" % f, va="center", fontsize=8, color="#666")
    for h in res["hrails"]:
        if h["kind"] == "НГП":
            ax.plot([h["x0"], h["x1"]], [h["y"], h["y"]], color="#3b6fb6", lw=3, solid_capstyle="butt")
        elif h["kind"] == "СП-60-40":
            ax.plot([h["x0"], h["x1"]], [h["y"], h["y"]], color="#2e9d57", lw=3, solid_capstyle="butt")
    for r in res["rails"]:
        new = round(r["x"], 1) not in base
        ax.plot([r["x"], r["x"]], [r["y0"], r["y1"]], lw=3.2 if new else 1.4,
                color="#e0611a" if new else ("#8aa0b8" if r["kind"] == "НСП" else "#b9c7d6"),
                solid_capstyle="butt")
    for fz in res["fittings"]:
        if fz["kind"] == "вставка" and round(fz["x"], 1) not in base:
            ax.plot(fz["x"], fz["y"], "s", ms=5, color="#e0611a")
    ax.text(2075, 5450, "простенок\n150 мм", ha="center", fontsize=7.5, color="#a04010")
    ax.text(3400, 5450, "500 мм", ha="center", fontsize=7.5, color="#a04010")
    ax.set_xlim(-200, 7000)
    ax.set_ylim(-300, 9300)
    ax.set_aspect("equal")
    ax.axis("off")
    ax.set_title("Межэтажная, клинкер, шаг 600: оранжевые — НСП вдоль боковых откосов\n"
                 "(в 100 мм от грани; в простенке уже 200 — посередине; от перекрытия до перекрытия,\n"
                 "вставки на перекрытиях); зелёные — СП-60-40 под окнами — к ним; синие — НГП",
                 fontsize=9)
    p = os.path.join(OUT, "interfloor_reveals.png")
    fig.savefig(p, dpi=130, bbox_inches="tight")
    plt.close(fig)
    return p, res


def pic_gable():
    gab = [[0, 0], [12000, 0], [12000, 4000], [6000, 6500], [0, 4000]]
    wins = [rect(1500, 1200, 3000, 2800), rect(9000, 1200, 10500, 2800), rect(5200, 4300, 6800, 5300)]
    res = cladding_plan({"tile": {"w": 1200, "h": 600}, "gap": {"v": 8, "h": 8}, "origin": {"y": 0},
                         "contours": [{"outer": gab, "holes": wins}]})
    assert res["ok"]
    fig, ax = plt.subplots(figsize=(10, 6.2))
    for t in res["inserts"]:
        if t.get("pts"):
            ax.add_patch(MPoly(t["pts"], closed=True, fc="#f3a15b", ec="#8a4a14", lw=0.6))
        else:
            full = abs(t["w"] - 1200) < 1e-6 and abs(t["h"] - 600) < 1e-6
            ax.add_patch(Rectangle((t["x"], t["y"]), t["w"], t["h"], fc="#d9d4c7" if full else "#ece8dc",
                                   ec="#7a7466", lw=0.5))
    ax.add_patch(MPoly(gab, closed=True, fill=False, ec="#222", lw=1.4))
    for w in wins:
        ax.add_patch(Rectangle((w[0][0], w[0][1]), w[1][0] - w[0][0], w[2][1] - w[0][1],
                               fill=False, ec="#445", lw=1.0, hatch="//"))
    ax.set_xlim(-300, 12300)
    ax.set_ylim(-300, 6800)
    ax.set_aspect("equal")
    ax.axis("off")
    s = res["summary"]
    ax.set_title("ATCLAD, керамогранит 1200×600, фронтон: раскладка по обёртке, плиты у ската режутся по "
                 "наклону\nплит %d: целых %d, подрезных %d, из них фигурных у ската %d (оранжевые — "
                 "в чертеже полилинии)" % (s["tiles"], s["full"], s["cut"], s["slope_pieces"]), fontsize=9)
    p = os.path.join(OUT, "atclad_gable.png")
    fig.savefig(p, dpi=130, bbox_inches="tight")
    plt.close(fig)
    return p, res


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    p1, r1 = pic_interfloor()
    p2, r2 = pic_gable()
    print(p1, len(r1["rails"]), "вертикалей")
    print(p2, r2["summary"])
