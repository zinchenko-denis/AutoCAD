# -*- coding: utf-8 -*-
"""Картинка для PDF №27: исполнительная схема ATFZONE по настоящему ответу движка зон
(Facades/engine/facades_engine, op=zones): стена в два этажа — окна, витраж, дверь в пол,
балконная дверь, окно у края зоны, парапет над стеной. Цвета — как у слоёв, которые создаёт
ATFZONE (откосы — красный, отливы — синий, примыкания витражей — пурпурный, контуры окон —
голубой, витражей — зелёный, дверей — оранжевый, парапет — жёлтый; на белом фоне жёлтый
нарисован тёмно-жёлтым). Заодно печатает строку ведомости — для таблицы в PDF.
Запуск: python3 make_pic27_zone.py  → build27/zone_openings.png (+ build27/zone_openings.json)"""
import json
import os
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt                 # noqa: E402
from matplotlib.patches import Polygon as MPoly  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "..", "Facades", "engine")))
import facades_engine as fe                     # noqa: E402


def R(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


contours = [
    {"id": "Z", "pts": R(0, 0, 14000, 7000)},
    {"id": "W1", "pts": R(700, 900, 2200, 2400)},        # окно 1 этажа
    {"id": "V1", "pts": R(3000, 300, 6000, 3100)},       # витраж
    {"id": "D1", "pts": R(7000, 0, 8200, 2300)},         # дверь в пол
    {"id": "W2", "pts": R(9200, 900, 10700, 2400)},
    {"id": "W3", "pts": R(700, 4400, 2200, 5900)},       # окна 2 этажа
    {"id": "D2", "pts": R(3500, 3900, 4400, 6200)},      # балконная дверь
    {"id": "W4", "pts": R(5200, 4400, 6700, 5900)},
    {"id": "W5", "pts": R(12900, 4400, 14000, 5900)},    # окно у края зоны: правый бок на краю
]
kinds = {"V1": "vitrage", "D1": "door", "D2": "door"}
parapets = [{"id": "P1", "pts": R(0, 7000, 14000, 7700)}]
res = fe.run({"op": "zones", "cladding": "клинкер", "zone_prefix": "Ф-", "start_index": 1, "units": "mm",
              "contours": contours, "opening_kinds": kinds, "parapets": parapets})
assert res["ok"], res
z = res["zones"][0]
rep = z["report"]
pp = res["parapets"][0]

COL = {"window_slope": "#d62728", "door_slope": "#d62728", "window_sill": "#1f5fd6",
       "vitrage_side": "#b000b0", "vitrage_top": "#b000b0", "vitrage_bottom": "#b000b0"}
KC = {"window": "#1fb7c9", "vitrage": "#2a9d2a", "door": "#e07b00"}
fig, ax = plt.subplots(figsize=(11.5, 6.9), dpi=140)
wall = [c for c in contours if c["id"] == "Z"][0]["pts"]
ax.add_patch(MPoly(wall, closed=True, facecolor="#f3efe6", edgecolor="#333", lw=1.2))
for c in contours[1:]:
    kd = z["opening_kinds"][c["id"]]
    ax.add_patch(MPoly(c["pts"], closed=True, facecolor="white", edgecolor=KC[kd], lw=1.0, ls="--"))
ax.add_patch(MPoly(parapets[0]["pts"], closed=True, facecolor="#fbf3c9", edgecolor="#b89b00", lw=1.6))
for ln in z["lines"]:
    xs = [p[0] for p in ln["pts"]]
    ys = [p[1] for p in ln["pts"]]
    ax.plot(xs, ys, color=COL[ln["cat"]], lw=3.2 if ln["cat"] != "window_sill" else 3.8,
            solid_capstyle="butt", zorder=5)
names = {"W1": "окно", "V1": "витраж", "D1": "дверь в пол", "W2": "окно", "W3": "окно",
         "D2": "балконная\nдверь", "W4": "окно", "W5": "окно\nу края"}
for c in contours[1:]:
    xs = [p[0] for p in c["pts"]]
    ys = [p[1] for p in c["pts"]]
    ax.text((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, names[c["id"]], ha="center", va="center",
            fontsize=8.5 if max(xs) - min(xs) >= 1400 else 6.8, color="#444")
ax.text(13900, 6900, "%s\nS = %.3f м²" % (z["zone_id"], rep["area_net_m2"]), ha="right", va="top", fontsize=9)
ax.text(13900, 7650, "Парапет П-1   S = %.3f м²" % pp["area_m2"], ha="right", va="top", fontsize=9)
leg = [("Оконные откосы (окна и двери)", "#d62728", "-"), ("Оконные отливы", "#1f5fd6", "-"),
       ("Примыкание к витражам", "#b000b0", "-"), ("Контур окон", KC["window"], "--"),
       ("Контур витражей", KC["vitrage"], "--"), ("Контур дверей", KC["door"], "--"),
       ("Парапет", "#b89b00", "-")]
for lab, col, ls in leg:
    ax.plot([], [], color=col, ls=ls, lw=3 if ls == "-" else 1.2, label=lab)
ax.legend(loc="upper center", bbox_to_anchor=(0.5, -0.02), ncol=4, fontsize=8.5, frameon=False)
ax.set_xlim(-300, 14300)
ax.set_ylim(-300, 8000)
ax.set_aspect("equal")
ax.axis("off")
fig.tight_layout()
os.makedirs(os.path.join(HERE, "build27"), exist_ok=True)
out = os.path.join(HERE, "build27", "zone_openings.png")
fig.savefig(out, bbox_inches="tight")
keys = ("area_outer_m2", "window_count", "window_area_m2", "vitrage_count", "vitrage_area_m2",
        "door_count", "door_area_m2", "area_net_m2", "window_sills_m", "window_slopes_m",
        "door_slopes_m", "vitrage_side_m", "vitrage_top_m", "vitrage_bottom_m")
data = {"zone_id": z["zone_id"], "report": dict((k, rep[k]) for k in keys),
        "parapet": dict((k, pp[k]) for k in ("area_m2", "top_m", "width_m")),
        "lines": len(z["lines"])}
with open(os.path.join(HERE, "build27", "zone_openings.json"), "w", encoding="utf-8") as f:
    json.dump(data, f, ensure_ascii=False, indent=1)
print(out)
print(json.dumps(data, ensure_ascii=False))
