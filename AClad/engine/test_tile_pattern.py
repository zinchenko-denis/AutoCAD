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



# ══════════════ 23.09: универсальная разбежка (bond/anchor/axis/…) ══════════════

def run_u(**kw):
    """Универсальный вызов: керамогранит 600×1200 (по умолчанию), руст 8."""
    base = {"tile": {"w": 600, "h": 1200}, "gap": {"v": 8, "h": 8},
            "types": ["КГ"]}
    base.update(kw)
    return tp.tile_pattern(base)


def fulls(p):
    return [t for t in p["pieces"] if t["full"]]


def has_full_at(p, x, y, tol=1e-6):
    return any(abs(t["x"] - x) < tol and abs(t["y"] - y) < tol for t in fulls(p))


# ── T18: через ряд 1/2 от правого-нижнего ≡ образец 09.09 [0, ½] (регресс) ──
Zw = [{"id": "Z", "outer": rect(0, 0, 3000, 1500), "holes": [rect(900, 400, 800, 700)]}]
p_old = run(PAT_STACK, contours=Zw)
p_new = run(PAT_STACK, contours=Zw, bond={"kind": "none"},
            anchor={"h": "R", "v": "B"})
key = lambda p: sorted((round(t["x"], 3), round(t["y"], 3), round(t["w"], 3),
                        round(t["h"], 3), t["type"]) for t in p["pieces"])
ok(p_new["ok"] and key(p_old) == key(p_new),
   "T18a: bond none + anchor R,B ≡ стек 09.09 (%d vs %d кусков)"
   % (len(p_old["pieces"]), len(p_new["pieces"])))
P1 = {"rows": [["T1"], ["T1"]], "row_shifts": [0.0, 0.5]}
p_old = run(P1, contours=Zw)
p_new = run({"rows": [["T1"]], "row_shifts": [0.0]}, contours=Zw,
            bond={"kind": "alternate", "value": 0.5},
            anchor={"h": "R", "v": "B"})
ok(key(p_old) == key(p_new), "T18b: через ряд ½ ≡ образец [0, ½] 09.09")

# ── T19: сдвиги лесенкой 1/3: 0, ⅓, ⅔, 0 …; ниже базы — продолжение ──
fn, info = tp.resolve_bond({"kind": "step", "value": "1/3"}, [0.0], 608.0)
vals = [round(fn(j), 6) for j in (-1, 0, 1, 2, 3)]
ok(vals == [round(2 / 3.0, 6), 0.0, round(1 / 3.0, 6), round(2 / 3.0, 6), 0.0],
   "T19a: лесенка ⅓ (%s)" % vals)
ok(info["period"] == 3 and len(info["phases"]) == 3, "T19b: период 3, фаз 3 (%s)" % info)
fn, info = tp.resolve_bond({"kind": "step", "value": "1/3", "dir": "-"}, [0.0], 608.0)
ok(round(fn(1), 6) == round(2 / 3.0, 6), "T19c: влево ⅓ ≡ вправо ⅔ по модулю")
fn, info = tp.resolve_bond({"kind": "alternate", "value": 0.75, "dir": "left"}, [0.0], 608.0)
ok(round(fn(1), 6) == 0.25 and fn(2) == 0.0, "T19d: через ряд ¾ влево = ¼ вправо")

# ── T20: смещение в мм: 200 мм при модуле 608; у левой кромки ряда 1 кусок 192 ──
Zp = [{"id": "P", "outer": rect(0, 0, 3000, 2416)}]
p = run_u(contours=Zp, bond={"kind": "alternate", "value": 200, "units": "mm"},
          anchor={"h": "L", "v": "B"})
r1 = sorted((t for t in p["pieces"] if t["j"] == 1), key=lambda t: t["x"])
ok(abs(r1[0]["x"]) < 1e-6 and abs(r1[0]["w"] - 192) < 1e-6 and not r1[0]["full"],
   "T20a: ряд 1 начинается куском 192 мм (%s)" % r1[0])
ok(abs(r1[1]["x"] - 200) < 1e-6 and r1[1]["full"], "T20b: первая целая ряда 1 с X=200")
fn, info = tp.resolve_bond({"kind": "step", "value": 200, "units": "mm"}, [0.0], 608.0)
ok(info["period"] == 76, "T20c: лесенка 200 мм на модуле 608 — период 76 (%s)" % info["period"])

# ── T21: своя последовательность; первое значение приводится к 0 ──
fn, info = tp.resolve_bond({"kind": "sequence", "sequence": ["1/4", "3/4"]}, [0.0], 608.0)
ok([round(fn(j), 6) for j in range(4)] == [0.0, 0.5, 0.0, 0.5] and info["notes"],
   "T21a: [¼, ¾] → [0, ½] + замечание")
fn, info = tp.resolve_bond({"kind": "sequence", "sequence": "0; 200; 400",
                            "units": "mm"}, [0.0], 608.0)
ok(abs(fn(2) * 608 - 400) < 1e-6 and info["period"] == 3, "T21b: строка «0; 200; 400» мм")

# ── T22: привязка — целая плитка в выбранной точке габарита ──
Zs = [{"id": "S", "outer": rect(0, 0, 6000, 6000)}]
cases = [({"h": "L", "v": "B"}, (0, 0)), ({"h": "R", "v": "B"}, (5400, 0)),
         ({"h": "L", "v": "T"}, (0, 4800)), ({"h": "R", "v": "T"}, (5400, 4800)),
         ({"h": "C", "v": "B"}, (2700, 0)), ({"h": "C", "v": "C"}, (2700, 2400))]
for an, (x, y) in cases:
    p = run_u(contours=Zs, bond={"kind": "none"}, anchor=an)
    ok(has_full_at(p, x, y), "T22: привязка %s → целая в (%s, %s)" % (an, x, y))
p = run_u(contours=Zs, bond={"kind": "none"}, anchor={"h": "C", "v": "B", "center": "joint"})
ok(has_full_at(p, 2396, 0) and has_full_at(p, 3004, 0), "T22j: шов по оси 3000 (2996…3004)")
p = run_u(contours=Zs, bond={"kind": "none"}, anchor={"h": "L", "v": "B", "point": {"x": 1000, "y": 500}})
ok(has_full_at(p, 1000, 500) and any(t["y"] < 500 and not t["full"] for t in p["pieces"]),
   "T22p: общая точка (1000, 500): целая там, ниже — подрезка")
p = run_u(contours=Zs, bond={"kind": "alternate", "value": 0.5}, anchor={"h": "R", "v": "T"})
top = [t for t in p["pieces"] if t["j"] == 0]
ok(any(t["full"] and abs(t["x"] + t["w"] - 6000) < 1e-6 for t in top),
   "T22t: сверху-справа — базовый ряд верхний, целая у правой кромки")
row_below = [t for t in p["pieces"] if t["j"] == -1]
ok(any(abs(t["x"] + t["w"] - 6000) < 1e-6 and abs(t["w"] - 296) < 1e-6 for t in row_below),
   "T22u: ряд под базовым сдвинут на ½ — у кромки кусок 296")

# ── T23: вертикальные столбцы (axis=cols), ручной счёт: 600×1200, ½ вверх ──
Zc = [{"id": "C", "outer": rect(0, 0, 2000, 3000)}]
p = run_u(contours=Zc, axis="cols", bond={"kind": "alternate", "value": 0.5, "dir": "+"},
          anchor={"h": "L", "v": "B"})
col = lambda x0: sorted((round(t["y"], 3), round(t["h"], 3), t["full"])
                        for t in p["pieces"] if abs(t["x"] - x0) < 1e-6)
ok(col(0) == [(0.0, 1200.0, True), (1208.0, 1200.0, True), (2416.0, 584.0, False)],
   "T23a: столбец 0 от низа: 1200, 1200, подрезка 584 (%s)" % col(0))
ok(col(608) == [(0.0, 596.0, False), (604.0, 1200.0, True), (1812.0, 1188.0, False)],
   "T23b: столбец 1 сдвинут на ½ вверх: 596, целая, 1188 (%s)" % col(608))
xs = sorted({round(t["x"], 3) for t in p["pieces"]})
ok(xs == [0.0, 608.0, 1216.0, 1824.0] and
   all(abs(t["w"] - 176) < 1e-6 for t in p["pieces"] if abs(t["x"] - 1824) < 1e-6),
   "T23c: столбцы 0/608/1216/1824, крайний шириной 176 (%s)" % xs)
ok(p["per_zone"][0]["joints_x"] == [604.0, 1212.0, 1820.0],
   "T23d: вертикальные швы столбцов сплошные (%s)" % p["per_zone"][0]["joints_x"])
ok(p["summary"]["bond"]["text"].startswith("через ряд: каждый второй столбец сдвинут вверх"),
   "T23e: текст смещения для столбцов (%s)" % p["summary"]["bond"]["text"])
# транспонированная проверка: cols на Z ≡ rows на Zᵀ с переставленными размерами
Zt = [{"id": "C", "outer": [[y, x] for x, y in rect(0, 0, 2000, 3000)]}]
q = tp.tile_pattern({"tile": {"w": 1200, "h": 600}, "gap": {"v": 8, "h": 8}, "types": ["КГ"],
                     "contours": Zt, "bond": {"kind": "alternate", "value": 0.5},
                     "anchor": {"h": "L", "v": "B"}})
ka = sorted((round(t["x"], 3), round(t["y"], 3), round(t["w"], 3), round(t["h"], 3)) for t in p["pieces"])
kb = sorted((round(t["y"], 3), round(t["x"], 3), round(t["h"], 3), round(t["w"], 3)) for t in q["pieces"])
ok(ka == kb, "T23f: столбцы ≡ транспонированные ряды")

# ── T24: руст вокруг проёма — ни один кусок не ближе руста к окну ──
Zg = [{"id": "G", "outer": rect(0, 0, 3000, 3000), "holes": [rect(1000, 1000, 1000, 1000)]}]
p = tp.tile_pattern({"tile": {"w": 600, "h": 600}, "gap": {"v": 8, "h": 8}, "types": ["КГ"],
                     "contours": Zg, "bond": {"kind": "none"}, "anchor": {"h": "L", "v": "B"},
                     "gap_around": True})
def _over(t, x0, y0, x1, y1):
    """Площадь куска (по фактическому контуру, Г — по кольцу) в окне."""
    if t.get("rings"):
        reg = tp.OrthoRegion(t["rings"][0], t["rings"][1:])
        return sum((c - a) * (d - b) for a, b, c, d in reg.clip_rect(x0, y0, x1, y1))
    return max(0.0, min(t["x"] + t["w"], x1) - max(t["x"], x0)) * \
        max(0.0, min(t["y"] + t["h"], y1) - max(t["y"], y0))
bad = [t for t in p["pieces"] if _over(t, 992, 992, 2008, 2008) > 1e-6]
ok(p["ok"] and not bad, "T24a: руст 8 вокруг окна соблюдён (нарушителей %d)" % len(bad))
ok(any(abs(t["x"] + t["w"] - 992) < 1e-6 for t in p["pieces"]) and
   any(abs(t["x"] - 2008) < 1e-6 for t in p["pieces"]),
   "T24b: камни встают к шву окна (кромки 992 и 2008)")
jx = p["per_zone"][0]["joints_x"]
ok(996.0 in jx and 2004.0 in jx, "T24c: боковые швы окна — оси стоек (%s)" % jx)
p0 = tp.tile_pattern({"tile": {"w": 600, "h": 600}, "gap": {"v": 8, "h": 8}, "types": ["КГ"],
                      "contours": Zg, "bond": {"kind": "none"}, "anchor": {"h": "L", "v": "B"}})
ok(p0["summary"]["area_tiles"] > p["summary"]["area_tiles"],
   "T24d: без руста вокруг проёма облицовки больше (к кромке окна)")

# ── T25: Г-кусок у угла окна: оставить / разрезать / разрезать с рустом ──
Zl = [{"id": "L", "outer": rect(0, 0, 3000, 3000), "holes": [rect(700, 700, 600, 600)]}]
def lay(shaped):
    return tp.tile_pattern({"tile": {"w": 600, "h": 600}, "gap": {"v": 8, "h": 8},
                            "types": ["КГ"], "contours": Zl, "bond": {"kind": "none"},
                            "anchor": {"h": "L", "v": "B"}, "gap_around": True,
                            "shaped": shaped})
cell = lambda p: [t for t in p["pieces"] if t["i"] == -2 and t["j"] == 2]
pk, ps, pj = lay("keep"), lay("split"), lay("split_joint")
ok(len(cell(pk)) == 1 and not cell(pk)[0]["rect"], "T25a: keep — один фигурный Г-кусок")
a_l = cell(pk)[0]["area"]
ok(abs(a_l - (508 * 600 + 92 * 508)) < 1e-6, "T25b: площадь Г = 508·600 + 92·508 (%s)" % a_l)
sp = sorted(cell(ps), key=lambda t: t["x"])
ok(len(sp) == 2 and all(t["rect"] and t.get("split") for t in sp) and
   abs(sum(t["area"] for t in sp) - a_l) < 1e-6, "T25c: split — два прямоугольника той же площади")
sj = sorted(cell(pj), key=lambda t: t["x"])
ok(len(sj) == 2 and abs(sj[0]["x"] + sj[0]["w"] - 1300) < 1e-6 and
   abs(sj[1]["x"] - 1308) < 1e-6 and abs(sj[1]["h"] - 600) < 1e-6,
   "T25d: split_joint — верхний кусок до грани окна 1300, боковой от 1308 (%s)" % sj)
# низ окна 700 (с рустом 692) против ряда 608 → у нижних углов полоса 84,
# у верхних (верх 1300+8 против ряда 1216) — 508: 2·8·84 + 2·8·508
ok(abs(pj["summary"]["trimmed"]["area"] - (2 * 8 * 84 + 2 * 8 * 508)) < 1e-6 and
   pj["summary"]["trimmed"]["count"] == 4,
   "T25e: снято рустом 2·8·84 + 2·8·508 (%s)" % pj["summary"]["trimmed"])

# ── T26: малая подрезка — только РЕЗАНЫЙ размер меньше порога ──
Zm = [{"id": "M", "outer": rect(0, 0, 4 * 608 + 100, 1200)}]
p = run_u(contours=Zm, bond={"kind": "none"}, anchor={"h": "L", "v": "B"}, warn_cut=150)
sm = [t for t in p["pieces"] if t.get("small")]
ok(len(sm) == 1 and abs(sm[0]["w"] - 100) < 1e-6 and p["summary"]["small"] == 1,
   "T26a: кусок 100 мм при пороге 150 — малая подрезка")
p = run_u(contours=Zm, bond={"kind": "none"}, anchor={"h": "L", "v": "B"}, warn_cut=100)
ok(p["summary"]["small"] == 0, "T26b: порог 100 — кусок 100 не малый")
pb = run(PAT_STACK, contours=[{"id": "B", "outer": rect(0, 0, 4 * 297 + 120, 4 * 89 - 7)}],
         warn_cut=100, bond={"kind": "none"}, anchor={"h": "L", "v": "B"})
ok(pb["summary"]["small"] == 0, "T26c: кирпич 82 мм высотой — не «малый» при пороге 100")

# ── T27: мост к ATFRAME — оси швов по фактическим стыкам ──
Zb = [{"id": "J", "outer": rect(0, 0, 2424, 1816)}]
p = tp.tile_pattern({"tile": {"w": 600, "h": 600}, "gap": {"v": 8, "h": 8}, "types": ["КГ"],
                     "contours": Zb, "bond": {"kind": "none"}, "anchor": {"h": "L", "v": "B"}})
ok(p["per_zone"][0]["joints_x"] == [604.0, 1212.0, 1820.0] and
   p["per_zone"][0]["rows_y"] == [604.0, 1212.0], "T27a: стек — швы %s / %s"
   % (p["per_zone"][0]["joints_x"], p["per_zone"][0]["rows_y"]))
p = tp.tile_pattern({"tile": {"w": 600, "h": 600}, "gap": {"v": 8, "h": 8}, "types": ["КГ"],
                     "contours": Zb, "bond": {"kind": "alternate", "value": 0.5},
                     "anchor": {"h": "L", "v": "B"}})
ok(p["per_zone"][0]["joints_x"] == [300.0, 604.0, 908.0, 1212.0, 1516.0, 1820.0, 2124.0],
   "T27b: разбежка ½ — объединение осей рядов (%s)" % p["per_zone"][0]["joints_x"])
ok(p["summary"]["joint_axes_per_module"] == 2 and
   any("оси стоек" in n for n in p["notes"]), "T27c: 2 оси на модуль + замечание для НВФ")

# ── T28–29: образец с привязкой слева/сверху — в базовой плитке свой элемент ──
p = run(PAT2, contours=[{"id": "Q", "outer": rect(0, 0, 4 * 297 - 7, 4 * 89 - 7)}],
        bond={"kind": "none"}, anchor={"h": "L", "v": "B"})
t00 = [t for t in p["pieces"] if t["i"] == 0 and t["j"] == 0][0]
t10 = [t for t in p["pieces"] if t["i"] == -1 and t["j"] == 0][0]
ok(abs(t00["x"]) < 1e-6 and t00["type"] == "T1" and t10["type"] == "T2",
   "T28: слева-снизу — образец с левого элемента (T1, T2 …)")
p = run(PAT2, contours=[{"id": "Q", "outer": rect(0, 0, 4 * 297 - 7, 4 * 89 - 7)}],
        bond={"kind": "none"}, anchor={"h": "R", "v": "T"})
t00 = [t for t in p["pieces"] if t["i"] == 0 and t["j"] == 0][0]
ok(t00["type"] == PAT2["rows"][-1][-1] and abs(t00["y"] + t00["h"] - (4 * 89 - 7)) < 1e-6,
   "T29: справа-сверху — верхний правый элемент образца")

# ── T30–31: отказы с понятным текстом ──
for bad_kw, what in ((dict(bond={"kind": "zigzag"}), "вид смещения"),
                     (dict(bond={"kind": "sequence", "sequence": []}), "пустая"),
                     (dict(anchor={"h": "X"}), "anchor"),
                     (dict(axis="diag"), "axis"),
                     (dict(shaped="cut"), "shaped"),
                     (dict(bond={"kind": "step", "value": "1/0"}), "ноль")):
    p = run_u(contours=Zs, **bad_kw)
    ok(not p["ok"] and what in p["error"], "T30: отказ %s (%s)" % (bad_kw, p.get("error")))
p = run_u(contours=Zs, bond={"kind": "alternate", "value": 1.5})
ok(p["ok"] and any("остаток" in n for n in p["notes"]), "T31: смещение ≥ модуля → остаток + note")

# ── T32: CLI clad_engine: столбцы + образец (транспонируется) + новые ключи ──
sample = []
for jj, row in enumerate([["A", "B"], ["B", "A"]]):
    for ii, t in enumerate(row):
        sample.append({"x0": jj * 608, "y0": ii * 1208, "x1": jj * 608 + 600,
                       "y1": ii * 1208 + 1200, "type": t})
req = {"op": "tile_pattern", "tile": {"w": 600, "h": 1200}, "gap": {"v": 8, "h": 8},
       "sample": sample, "axis": "cols", "bond": {"kind": "pattern"},
       "anchor": {"h": "L", "v": "B"}, "gap_around": True, "shaped": "split_joint",
       "warn_cut": 150, "contours": [{"id": "K", "pts": rect(0, 0, 2424, 3624)}]}
d = tempfile.mkdtemp()
fi, fo = os.path.join(d, "in.json"), os.path.join(d, "out.json")
json.dump(req, open(fi, "w", encoding="utf-8"), ensure_ascii=False)
rc = subprocess.call([sys.executable, "clad_engine.py", fi, fo])
out = json.load(open(fo, encoding="utf-8"))
ok(rc == 0 and out["ok"] and out["summary"]["axis"] == "cols" and
   {t["type"] for t in out["pieces"]} == {"A", "B"}, "T32a: CLI cols + образец")
c0 = sorted((t for t in out["pieces"] if abs(t["x"]) < 1e-6), key=lambda t: t["y"])
ok([t["type"] for t in c0[:2]] == ["A", "B"], "T32b: столбец 0 снизу вверх A, B (%s)"
   % [t["type"] for t in c0[:2]])
ok(all(k in out["per_zone"][0] for k in ("joints_x", "rows_y", "small", "trimmed", "members")),
   "T32c: мост ATFRAME и счётчики в per_zone")


# ── T33–T36: грабли, найденные синтетикой 23.09 (регресс) ──
base_s = {"tile": {"w": 600, "h": 600}, "gap": {"v": 8, "h": 8}, "types": ["T"],
          "bond": {"kind": "none"}, "anchor": {"h": "L", "v": "B"}}
# T33: подрезанная плитка через ГОРИЗОНТАЛЬНЫЙ шов смежных зон — один кусок
p = tp.tile_pattern(dict(base_s, contours=[{"id": "A", "outer": rect(0, 0, 3000, 2967)},
                                           {"id": "B", "outer": rect(0, 2967, 3000, 2000)}]))
seam = [t for t in p["pieces"] if t["x"] > 2400 and 2400 < t["y"] < 3000]
ok(len(seam) == 1 and abs(seam[0]["h"] - 600) < 1e-6 and abs(seam[0]["w"] - 568) < 1e-6,
   "T33a: кусок 568×600 через шов y=2967 не делится (%s)" % seam)
q = tp.tile_pattern(dict(base_s, axis="cols",
                         contours=[{"id": "A", "outer": rect(0, 0, 2967, 3000)},
                                   {"id": "B", "outer": rect(2967, 0, 2000, 3000)}]))
ok(not any(abs(t["x"] - 2967) < 1e-6 or abs(t["x"] + t["w"] - 2967) < 1e-6
           for t in q["pieces"]), "T33b: столбцы — вертикальный шов стен не режет плитку")
# T34: перекрывающиеся зоны НЕ сливаются (нижние рёбра на одной линии) + замечание
p = tp.tile_pattern(dict(base_s, contours=[{"id": "A", "outer": rect(0, 0, 3000, 3000)},
                                           {"id": "B", "outer": rect(2000, 0, 3000, 3000)}]))
ok(p["summary"]["zones"] == 2 and any("перекрываются" in n for n in p["notes"]),
   "T34: перекрытие зон — две зоны и предупреждение (%s)" % p["notes"])
# T35: два перекрывающихся проёма — внутри объединения облицовки нет
p = tp.tile_pattern(dict(base_s, contours=[{"id": "W", "outer": rect(0, 0, 3000, 3000),
                         "holes": [rect(200, 200, 800, 800), rect(700, 700, 800, 800)]}]))
inside = sum(_over(t, 200, 200, 1000, 1000) + _over(t, 700, 700, 1500, 1500) -
             _over(t, 700, 700, 1000, 1000) for t in p["pieces"])
ok(inside < 1e-6, "T35: наложение проёмов — облицовки внутри нет (%.3f мм²)" % inside)
# T36: проём, вылезающий за верх стены, — облицовки снаружи стены нет
p = tp.tile_pattern(dict(base_s, contours=[{"id": "W", "outer": rect(0, 0, 3000, 3000),
                         "holes": [rect(1000, 2500, 800, 700)]}]))
ok(not any(t["y"] + t["h"] > 3000 + 1e-6 for t in p["pieces"]),
   "T36: проём за контуром стены не рождает облицовку снаружи")

# T37: запрос без gap/tile через clad_engine — чистый отказ, не падение
r = ce.run({"op": "tile_pattern", "tile": {"w": 600, "h": 600},
            "contours": [{"id": "A", "pts": rect(0, 0, 1000, 1000)}]})
ok(r["ok"], "T37a: без gap — руст 0, раскладка идёт")
r = ce.run({"op": "tile_pattern", "contours": [{"id": "A", "pts": rect(0, 0, 1000, 1000)}]})
ok(r["ok"] is False and "размер" in r["error"], "T37b: без tile — отказ текстом (%s)" % r.get("error"))


print("tile_pattern: OK, %d проверок" % _n)
