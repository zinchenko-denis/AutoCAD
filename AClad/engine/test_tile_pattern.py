# -*- coding: utf-8 -*-
"""Юниты мелкоштучной раскладки по образцу (tile_pattern + op в
clad_engine). Геометрия проверена руками посчитанными раскладками.
Запуск: PYTHONUTF8=1 python3 test_tile_pattern.py (из AClad/engine)."""
import json
import os
import subprocess
import sys
import tempfile

import tile_pattern as tp
import clad_engine as ce

_n = 0


def ok(cond, msg):
    global _n
    _n += 1
    if not cond:
        print("FAIL:", msg)
        sys.exit(1)


def rect(x0, y0, w, h):
    return [[x0, y0], [x0 + w, y0], [x0 + w, y0 + h], [x0, y0 + h]]


# образцы: PAT2 — running-bond (сдвиг полплиты), PAT_STACK — стек (без сдвига)
PAT2 = {"rows": [["T1", "T2"], ["T2", "T1"]], "row_shifts": [0.0, 0.5]}
PAT_STACK = {"rows": [["T1", "T2"]], "row_shifts": [0.0]}


def run(pattern=PAT2, **kw):
    base = {"tile": {"w": 290, "h": 82}, "gap": {"v": 7, "h": 7},
            "pattern": pattern, "datum": {"mode": "bbox"}}
    base.update(kw)
    return tp.tile_pattern(base)


# ── T1: ровная сетка (стек, без сдвига), целое число модулей → все целые ──
# 4 столбца (4·297−7 = 1181 ширина), 3 ряда (3·89−7 = 260 высота)
W = 4 * 297 - 7
H = 3 * 89 - 7
p = run(PAT_STACK, contours=[{"id": "Z1", "outer": rect(0, 0, W, H)}])
ok(p["ok"], "T1 ok")
ok(p["summary"]["full"] == 12 and p["summary"]["cut"] == 0,
   "T1: 4×3 = 12 целых, 0 подрезки (%s)" % p["summary"])
# датум bbox = правый-низ (W, 0); крайний правый столбец целый
xs = sorted({round(t["x"], 1) for t in p["pieces"] if t["j"] == 0})
ok(xs == [0.0, 297.0, 594.0, 891.0], "T1: столбцы ряда 0 (%s)" % xs)


# ── T2: датум-угол справа-внизу; целая плитка именно в правом-нижнем ──
p = run(PAT_STACK, contours=[{"id": "Z", "outer": rect(0, 0, W, H)}])
br = [t for t in p["pieces"] if t["j"] == 0 and t["full"]]
rightmost = max(br, key=lambda t: t["x"])
ok(abs((rightmost["x"] + rightmost["w"]) - W) < 1e-6,
   "T2: правый край нижнего ряда упирается в датум (%s)" % rightmost)


# ── T3: подрезка слева. Ширина = 4 модуля + 100 мм → левый столбец режется ──
p = run(PAT_STACK, contours=[{"id": "Z", "outer": rect(0, 0, W + 100, H)}])
row0 = sorted([t for t in p["pieces"] if t["j"] == 0], key=lambda t: t["x"])
ok(not row0[0]["full"] and row0[0]["x"] < 1e-6,
   "T3: левый кусок ряда 0 подрезной у x=0 (%s)" % row0[0])
# остаток слева = (W+100) − 4·297 = 1281 − 1188 = 93 мм (руст съедает 7)
ok(abs(row0[0]["w"] - 93.0) < 1e-6,
   "T3: ширина левой подрезки 93 мм (%.3f)" % row0[0]["w"])
ok(all(t["full"] for t in row0[1:]),
   "T3: остальные в ряду целые")


# ── T4: смещение нечётного ряда даёт половинку 141.5 у правого края ──
# ряд 1 сдвинут вправо на 0.5·297 = 148.5 → у правой кромки кусок
# шириной 290 − 148.5 = 141.5
p = run(contours=[{"id": "Z", "outer": rect(0, 0, W, H)}])
row1 = sorted([t for t in p["pieces"] if t["j"] == 1], key=lambda t: t["x"])
half = max(row1, key=lambda t: t["x"])
ok(not half["full"] and abs(half["w"] - 141.5) < 1e-6,
   "T4: половинка 141.5 у правой кромки нечётного ряда (%s)" % half)
ok(p["summary"]["by_type"]["T1"]["half"] + p["summary"]["by_type"]["T2"]["half"] >= 1,
   "T4: половинки посчитаны в ведомости")


# ── T5: раскраска по образцу — цвет плитки (i,j) периодичен 2×2 ──
# нижний правый (i=0,j=0) = rows[0][last] = rows[0][1] = T2
p = run(contours=[{"id": "Z", "outer": rect(0, 0, W, H)}])
c00 = [t for t in p["pieces"] if t["i"] == 0 and t["j"] == 0][0]
ok(c00["type"] == "T2", "T5: правый-нижний тип по образцу = T2 (%s)" % c00["type"])
c10 = [t for t in p["pieces"] if t["i"] == 1 and t["j"] == 0][0]
ok(c10["type"] == "T1", "T5: сосед слева = T1 (%s)" % c10["type"])
c01 = [t for t in p["pieces"] if t["i"] == 0 and t["j"] == 1][0]
ok(c01["type"] == "T1", "T5: над правым-нижним ряд 1 = T1 (%s)" % c01["type"])


# ── T6: проём даёт фигурную подрезку у угла окна ──
# зона 6×6 модулей с центральным окном; углы окна дают Г-куски или прямые
outer = rect(0, 0, 6 * 297 - 7, 6 * 89 - 7)
hole = rect(500, 200, 300, 300)   # окно, границы не по сетке
p = run(contours=[{"id": "Z", "outer": outer, "holes": [hole]}])
ok(p["ok"], "T6 ok")
ok(p["summary"]["cut"] > 0, "T6: есть подрезка вокруг окна")
# ни одна плитка не залезает внутрь окна
for t in p["pieces"]:
    if "rings" in t:
        continue
    x0, y0, x1, y1 = t["x"], t["y"], t["x"] + t["w"], t["y"] + t["h"]
    inside = x0 >= 500 - 1e-6 and x1 <= 800 + 1e-6 and \
        y0 >= 200 - 1e-6 and y1 <= 500 + 1e-6
    ok(not inside, "T6: плитка внутри окна (%s)" % t)


# ── T7: площадь плиток = площадь зоны с окном (в пределах руста) ──
zone_area = (6 * 297 - 7) * (6 * 89 - 7) - 300 * 300
tile_area = sum(t["area"] for t in p["pieces"])
ok(tile_area <= zone_area + 1e-6,
   "T7: площадь плиток не больше площади зоны (%.0f <= %.0f)"
   % (tile_area, zone_area))
ok(tile_area > zone_area * 0.85,
   "T7: плитка покрывает > 85%% зоны (%.3f)" % (tile_area / zone_area))


# ── T8: раскрой — половинки парами, полосы по высоте стопкой, пропил ──
# зона высотой 1 ряд, ширина 2 модуля + сдвиг: в ряду 0 целые, а по бокам
# куски; проверяем nest_pieces напрямую на руками заданных кусках
def piece(w, h, rect=True):
    return {"full": False, "rect": rect, "w": w, "h": h, "area": w * h}


nest = tp.nest_pieces([piece(141.5, 82), piece(141.5, 82)], 290, 82, kerf=3.0)
ok(nest["blanks"] == 1, "T8: две половинки 141.5 + пропил 3 = 286 ≤ 290 → 1 заготовка (%s)" % nest)
nest = tp.nest_pieces([piece(150, 82), piece(150, 82)], 290, 82, kerf=3.0)
ok(nest["blanks"] == 2, "T8: 150+150+3 > 290 → 2 заготовки (%s)" % nest)
nest = tp.nest_pieces([piece(290, 24)] * 3, 290, 82, kerf=3.0)
ok(nest["blanks"] == 1, "T8: три полосы 24 по высоте: 24·3+3·2 = 78 ≤ 82 → 1 (%s)" % nest)
nest = tp.nest_pieces([piece(290, 24)] * 4, 290, 82, kerf=3.0)
ok(nest["blanks"] == 2, "T8: четыре полосы 24: 105 > 82 → 2 (%s)" % nest)
nest = tp.nest_pieces([piece(100, 50, rect=False)], 290, 82, kerf=3.0)
ok(nest["blanks"] == 1 and nest["by_kind"]["фигурные"] == 1, "T8: фигурный — плитка на кусок")
# в сводке: tiles_total = целые + заготовки раскроя, отходы = площадь заготовок − нетто
p = run(contours=[{"id": "Z", "outer": rect(0, 0, 2 * 297 - 7, 82)}])
bt = p["summary"]["by_type"]
for t, dd in bt.items():
    ok(dd["tiles_total"] == dd["full"] + dd["blanks_cut"], "T8: tiles_total = full + blanks_cut (%s)" % t)
    ok(abs(dd["waste_area"] - (dd["tiles_total"] * 290 * 82 - dd["area"])) < 1e-6, "T8: отходы (%s)" % t)
ok(p["summary"]["waste_pct"] >= 0.0, "T8: отходы в процентах")


# ── T9: pattern_from_sample восстанавливает раппорт из прямоугольников ──
# два ряда по 3 плитки 290×82, руст 7, ряд 1 сдвинут на 148.5
sample = []
for j, row in enumerate([["A", "B", "C"], ["B", "C", "A"]]):
    sh = 148.5 if j % 2 else 0.0
    for i, t in enumerate(row):
        x0 = sh + i * 297.0
        y0 = j * 89.0
        sample.append({"x0": x0, "y0": y0, "x1": x0 + 290, "y1": y0 + 82, "type": t})
pat = tp.pattern_from_sample(sample)
ok(pat["cols"] == 3 and pat["nrows"] == 2, "T9: 3×2 (%s)" % pat)
ok(pat["rows"] == [["A", "B", "C"], ["B", "C", "A"]], "T9: ряды (%s)" % pat["rows"])
ok(abs(pat["row_shifts"][1] - 0.5) < 1e-3, "T9: сдвиг ряда 0.5 (%s)" % pat["row_shifts"])
ok(abs(pat["tile_w"] - 290) < 1e-6 and abs(pat["tile_h"] - 82) < 1e-6,
   "T9: размер плитки из образца")


# ── T10: датум wall vs bbox на зоне-«ножке» (стена на опоре) ──
# основная стена 0..1000 по низу y=100 (длина 1000); ножка 0..300 ниже до y=0
foot = [[0, 0], [300, 0], [300, 100], [1000, 100], [1000, 600], [0, 600]]
pw = run(contours=[{"id": "Z", "outer": foot, "datum": {"mode": "wall"}}])
zb = pw["per_zone"][0]["datum"]
ok(abs(zb["y"] - 100.0) < 1e-6,
   "T10: wall-датум на низу основной стены y=100 (%s)" % zb)
pb = run(contours=[{"id": "Z", "outer": foot, "datum": {"mode": "bbox"}}])
zb2 = pb["per_zone"][0]["datum"]
ok(abs(zb2["y"] - 0.0) < 1e-6,
   "T10: bbox-датум на низу габарита y=0 (%s)" % zb2)
# дефолт (ответ Германа 09.09, В1) — угол габарита
pd = tp.tile_pattern({"tile": {"w": 290, "h": 82}, "gap": {"v": 7, "h": 7},
                      "pattern": PAT2, "contours": [{"id": "Z", "outer": foot}]})
ok(abs(pd["per_zone"][0]["datum"]["y"] - 0.0) < 1e-6 and pd["summary"]["datum_mode"] == "bbox",
   "T10: датум по умолчанию = габарит (bbox)")


# ── T11: общий горизонт (mode=point) — ряды двух зон на одних отметках ──
p = run(datum={"mode": "point", "x": 1000.0, "y": 0.0},
        contours=[{"id": "A", "outer": rect(0, 0, W, H)},
                  {"id": "B", "outer": rect(0, 500, W, H)}])
# у ЦЕЛЫХ плиток низ ряда = датум + j·89; общий горизонт → одинаковый
# y при одинаковом j в обеих зонах (подрезные у кромок зон не в счёт —
# они прижаты к границе)
ya = {t["j"]: round(t["y"], 3) for t in p["pieces"] if t["zone"] == "A" and t["full"]}
yb = {t["j"]: round(t["y"], 3) for t in p["pieces"] if t["zone"] == "B" and t["full"]}
ok(ya and all(abs(ya[j] - j * 89.0) < 1e-6 for j in ya),
   "T11: ряды зоны A на сетке j·89 от общего датума y=0")
ok(yb and all(abs(yb[j] - j * 89.0) < 1e-6 for j in yb),
   "T11: ряды зоны B на той же сетке j·89 (общий горизонт)")


# ── T12: CLI op=tile_pattern через clad_engine (zones + contours) ──
def zone_dict(outer_pts, openings=()):
    return {"schema": "facade_zone/1", "id": "Z", "units": "mm",
            "outer": {"pts": outer_pts}, "openings": list(openings)}


req = {"op": "tile_pattern", "tile": {"w": 290, "h": 82},
       "gap": {"v": 7, "h": 7}, "pattern": PAT_STACK, "datum": {"mode": "bbox"},
       "zones": [{"zone_id": "Ф-1", "zone": zone_dict(rect(0, 0, W, H))}]}
r = ce.run(req)
ok(r["ok"] and r["summary"]["full"] == 12, "T12: op через run() (%s)" % r["summary"])
ok(r["per_zone"][0]["zone_id"] == "Ф-1", "T12: zone_id проброшен")

# CLI через файлы (как зовёт C#)
with tempfile.TemporaryDirectory() as d:
    ip = os.path.join(d, "in.json")
    op = os.path.join(d, "out.json")
    with open(ip, "w", encoding="utf-8") as f:
        json.dump(req, f)
    rc = subprocess.call([sys.executable, "clad_engine.py", ip, op],
                         cwd=os.path.dirname(os.path.abspath(__file__)))
    ok(rc == 0, "T12: CLI код возврата 0")
    with open(op, encoding="utf-8") as f:
        out = json.load(f)
    ok(out["ok"] and out["summary"]["full"] == 12, "T12: CLI результат")


# ── T13: единицы зоны в метрах ──
req_m = {"op": "tile_pattern", "tile": {"w": 290, "h": 82},
         "gap": {"v": 7, "h": 7}, "pattern": PAT_STACK, "datum": {"mode": "bbox"},
         "zones": [{"zone_id": "M", "zone": {
             "schema": "facade_zone/1", "id": "M", "units": "m",
             "outer": {"pts": rect(0, 0, W / 1000.0, H / 1000.0)},
             "openings": []}}]}
r = ce.run(req_m)
ok(r["ok"] and r["summary"]["full"] == 12, "T13: зона в метрах = та же раскладка")


# ── T14: наклонный контур отклоняется с note, ортогональный проходит ──
skew = [[0, 0], [1000, 5], [1000, 600], [0, 600]]   # верх-низ наклонены на 5 мм > tol по одному ребру
p = run(contours=[{"id": "S", "outer": skew}], ortho_tol=1.0)
ok(not p["ok"] or all(pz["zone_id"] != "S" for pz in p["per_zone"]),
   "T14: наклонный контур (5 мм > допуска 1 мм) пропущен")
ok(any("не ортогонален" in n for n in p["notes"]),
   "T14: note про неортогональность (%s)" % p["notes"])
# тот же контур с допуском 6 мм — выпрямляется и раскладывается
p2 = run(contours=[{"id": "S", "outer": skew}], ortho_tol=6.0)
ok(p2["ok"] and p2["per_zone"][0]["zone_id"] == "S",
   "T14: с допуском 6 мм контур выпрямлен и разложен")



# ── T15: смежные контуры одной плоскости объединяются (ответ Германа, В2) ──
# два прямоугольника встык по вертикали x≈W (стык со щелью 0.05 мм из АР);
# B шире A на руст (7), чтобы 8 столбцов сетки легли целыми через шов:
# общая ширина 2W+7 = 8·297−7 (остаток 0.05 мм слева — численный мусор)
A = rect(0, 0, W, H)
Bz = [[W + 0.05, 0], [W + 0.05 + W + 7, 0], [W + 0.05 + W + 7, H], [W + 0.05, H]]
p = run(PAT_STACK, contours=[{"id": "A", "outer": A}, {"id": "B", "outer": Bz}])
ok(len(p["per_zone"]) == 1 and p["per_zone"][0]["members"] == ["A", "B"],
   "T15: контуры A и B объединены в одну зону (%s)" % [z["zone_id"] for z in p["per_zone"]])
# общая сетка от правого края B: 8 столбцов × 3 ряда целых; шов закрыт,
# плитка через шов — целая, а не два куска
ok(p["summary"]["full"] == 24 and p["summary"]["cut"] == 0,
   "T15: 8×3 = 24 целых, стык не даёт подрезки (%s)" % {k: p["summary"][k] for k in ("full", "cut")})
seam_tiles = [t for t in p["pieces"] if t["x"] < W < t["x"] + t["w"]]
ok(seam_tiles and all(t["full"] for t in seam_tiles),
   "T15: плитки через шов x=W целые (%d шт.)" % len(seam_tiles))
ok(any("объединены" in n for n in p["notes"]), "T15: note об объединении")
# с merge_touching=False — две зоны, каждая со своей сеткой
p2 = run(PAT_STACK, merge_touching=False,
         contours=[{"id": "A", "outer": A}, {"id": "B", "outer": Bz}])
ok(len(p2["per_zone"]) == 2, "T15: без объединения — две зоны")
# не касающиеся контуры не объединяются
C = rect(3 * W, 0, W, H)
p3 = run(PAT_STACK, contours=[{"id": "A", "outer": A}, {"id": "C", "outer": C}])
ok(len(p3["per_zone"]) == 2, "T15: далёкие контуры остаются раздельными")

# ── T16: полоски поглощаются рустами (ответ Германа, В3) ──
# зона высотой 3 ряда + руст + 5 мм: 4-й ряд начинается на 267 и режется
# верхом зоны на 272 → четыре полоски 290×5 < 10 мм — не кладутся
H5 = 3 * 89 + 5
p = run(PAT_STACK, contours=[{"id": "Z", "outer": rect(0, 0, W, H5)}])
ok(p["summary"]["full"] == 12 and p["summary"]["cut"] == 0,
   "T16: полоска 5 мм не в раскладке (%s)" % {k: p["summary"][k] for k in ("full", "cut")})
ok(p["summary"]["absorbed"]["count"] == 4 and abs(p["summary"]["absorbed"]["area"] - 4 * 290 * 5) < 1e-6,
   "T16: поглощено 4 полоски по 290×5 (%s)" % p["summary"]["absorbed"])
ok(any("поглощены рустами" in n for n in p["notes"]), "T16: note о поглощении")
# tiny_mode=layer — прежнее поведение: кусок остаётся с флагом tiny
p2 = run(PAT_STACK, tiny_mode="layer", contours=[{"id": "Z", "outer": rect(0, 0, W, H5)}])
ok(p2["summary"]["cut"] == 4 and p2["summary"]["tiny"] == 4, "T16: tiny_mode=layer оставляет полоски")
# порог настраиваемый: 4 мм — полоска 5 мм уже полноценный кусок
p3 = run(PAT_STACK, min_piece=4.0, contours=[{"id": "Z", "outer": rect(0, 0, W, H5)}])
ok(p3["summary"]["cut"] == 4 and p3["summary"]["absorbed"]["count"] == 0, "T16: порог 4 мм → полоска остаётся")
# зона, кончающаяся ВНУТРИ руста (3 ряда + 5 мм без нового ряда) — просто нет плиток
p4 = run(PAT_STACK, contours=[{"id": "Z", "outer": rect(0, 0, W, H + 5)}])
ok(p4["summary"]["full"] == 12 and p4["summary"]["cut"] == 0 and p4["summary"]["absorbed"]["count"] == 0,
   "T16: верх зоны в русте — ни кусков, ни полосок")

# ── T17: датум «стена» для объединённой зоны — внутренний шов не датум ──
# стена A (низкая) + стена B справа (высокая): правая кромка = правая B
foot2 = [[0, 0], [1000, 0], [1000, 600], [0, 600]]
tall = [[1000, 0], [1500, 0], [1500, 1200], [1000, 1200]]
R, Bd = tp.datum_wall([foot2, tall])
ok(abs(R - 1500) < 1e-6 and abs(Bd - 0) < 1e-6,
   "T17: датум объединённой зоны — наружная правая кромка 1500, низ 0 (%s, %s)" % (R, Bd))


print("tile_pattern: OK, %d проверок" % _n)
