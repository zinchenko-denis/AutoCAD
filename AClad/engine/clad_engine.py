#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""clad_engine.py — CLI движка раскладки облицовки (модуль AClad).

Вызов (как из C#): clad_engine.exe in.json out.json
Файл out.json пишется ВСЕГДА (при внутренней ошибке — {"ok": false}).

op="cladding" — раскладка облицовки универсальным блоком по зонам
ATFZONE (геометрия facade_zone/1 из <dwg>_fzones.json) и/или голым
замкнутым полилиниям (вложенность: контур в контуре = проём).
Правила раскладки — cladding_plan.py (ТЗ Германа, docs/CLADDING.md).

Выделено из facades_engine (Facades) 22.07: этап 1 (зоны/ведомости) —
стабильный бандл AFacades, этап 2 (раскладка) — свой бандл AClad,
чтобы переустановка раскладки не трогала боевой этап 1.

ТОЛЬКО stdlib (движок замораживается PyInstaller).
"""

import json
import sys
import traceback

import cladding_plan as cp

_ARC_EPS = 1e-9
_CLOSE_TOL = 0.5    # мм: дубль замыкающей вершины


def _zone_to_contour(zd, notes, zone_id):
    """facade_zone/1 → контур cladding_plan {outer, holes} в мм.

    Дуги (bulge != 0) кассетной раскладке не поддаются — зона
    пропускается с note (вопрос Герману в очереди)."""
    units = zd.get("units") or "mm"
    k = {"mm": 1.0, "m": 1000.0}.get(units)
    if k is None:
        notes.append("%s: неизвестные единицы %r — пропуск" % (zone_id, units))
        return None

    def _poly(obj):
        pts = [[float(p[0]) * k, float(p[1]) * k] for p in obj["pts"]]
        bulges = obj.get("bulges") or []
        arc = any(abs(float(b)) > _ARC_EPS for b in bulges)
        return pts, arc

    try:
        outer, arc = _poly(zd["outer"])
        holes = []
        for o in zd.get("openings") or []:
            hp, ha = _poly(o["poly"])
            arc = arc or ha
            holes.append(hp)
    except (KeyError, TypeError, ValueError, IndexError) as e:
        notes.append("%s: негодная геометрия зоны (%s) — пропуск"
                     % (zone_id, e))
        return None
    if arc:
        notes.append("%s: контур с дугами — раскладка кассетами не "
                     "выполняется, зона пропущена" % zone_id)
        return None
    return {"outer": outer, "holes": holes}


def _inside(outer, pts_probe):
    """Хотя бы одна probe-точка внутри полигона."""
    return any(cp._pt_in_poly(outer, x, y) for x, y in pts_probe)


def _group_contours(raw, notes):
    """Плоский список полилиний из выбора C# → контуры {outer, holes}
    по вложенности: top-level = участок, внутри — проём; глубже проёма
    — note-пропуск. Дуги (bulge != 0) — note-пропуск контура. Проба
    вложенности — центр bbox и центроид вершин (фасадные проёмы
    прямоугольные; касание границ допустимо)."""
    parsed = []   # (cid, pts, area, probe)
    for i, c in enumerate(raw):
        cid = str(c.get("id", i))
        pts = c.get("pts")
        if not isinstance(pts, list) or len(pts) < 3:
            notes.append("контур %s: меньше 3 вершин — пропуск" % cid)
            continue
        try:
            p = [(float(q[0]), float(q[1])) for q in pts]
        except (TypeError, ValueError, IndexError):
            notes.append("контур %s: негодные координаты — пропуск" % cid)
            continue
        bulges = c.get("bulges") or []
        try:
            arc = any(abs(float(b)) > _ARC_EPS for b in bulges)
        except (TypeError, ValueError):
            arc = False
        if arc:
            notes.append("контур %s: дуги не поддерживаются — пропуск"
                         % cid)
            continue
        if len(p) >= 2 and abs(p[0][0] - p[-1][0]) <= _CLOSE_TOL and \
           abs(p[0][1] - p[-1][1]) <= _CLOSE_TOL:
            p = p[:-1]
        if len(p) < 3:
            notes.append("контур %s: меньше 3 вершин — пропуск" % cid)
            continue
        area = abs(sum(p[j][0] * p[(j + 1) % len(p)][1] -
                       p[(j + 1) % len(p)][0] * p[j][1]
                       for j in range(len(p)))) / 2.0
        xs = [q[0] for q in p]
        ys = [q[1] for q in p]
        probe = [((min(xs) + max(xs)) / 2.0, (min(ys) + max(ys)) / 2.0),
                 (sum(xs) / len(xs), sum(ys) / len(ys))]
        parsed.append((cid, p, area, probe))

    n = len(parsed)
    cont = [-1] * n
    for i in range(n):
        best = -1
        for j in range(n):
            if i == j or parsed[j][2] <= parsed[i][2]:
                continue
            if _inside(parsed[j][1], parsed[i][3]):
                if best < 0 or parsed[j][2] < parsed[best][2]:
                    best = j
        cont[i] = best

    def depth(i):
        d, cur = 0, cont[i]
        while cur >= 0 and d <= n:
            d += 1
            cur = cont[cur]
        return d

    tops, kids = [], {}
    for i in range(n):
        d = depth(i)
        if d == 0:
            tops.append(i)
        elif d == 1:
            kids.setdefault(cont[i], []).append(i)
        else:
            notes.append("контур %s: вложен глубже проёма — пропуск"
                         % parsed[i][0])

    out = []
    for t in tops:
        cid, p, _a, _pr = parsed[t]
        out.append((cid, {
            "outer": [[x, y] for x, y in p],
            "holes": [[[x, y] for x, y in parsed[k][1]]
                      for k in kids.get(t, [])],
        }))
    return out


def op_cladding(req):
    """op="cladding" — раскладка (команда ATCLAD, бандл AClad).

    Вход:
    {
      "op": "cladding",
      "tile": {"w", "h"}, "gap": {"v", "h"},
      "origin": {"y": <низ первого ряда, мм>},
      "vjoints": [x, ...],   // точки вертикальных рустов (ТЗ 2.5)
      "min_cut"?: <мм, дефолт 150>,
      "zones":    [{"zone_id", "zone": {facade_zone/1}}, ...],
      "contours": [{"id", "pts", "bulges"?}, ...]
    }

    Выход: {"ok", "inserts": [{x,y,w,h,zone}], "notes",
            "per_zone": [{"zone_id","outer_id"?,"tiles","full","cut",
                          "rows"}],
            "summary": {"tiles","full","cut","zones"}}

    Раскладка позонная, горизонт общий: сетка рядов везде от origin.y —
    результат не зависит от разбиения на вызовы."""
    notes = []
    items = []   # (zone_id, outer_id|None, contour)

    for zrec in req.get("zones") or []:
        if not isinstance(zrec, dict):
            continue
        zone_id = str(zrec.get("zone_id") or "?")
        zd = zrec.get("zone")
        if not isinstance(zd, dict):
            notes.append("%s: нет геометрии зоны — пропуск" % zone_id)
            continue
        c = _zone_to_contour(zd, notes, zone_id)
        if c is not None:
            items.append((zone_id, None, c))

    for outer_id, c in _group_contours(req.get("contours") or [], notes):
        items.append(("контур %s" % outer_id, outer_id, c))

    if not items:
        return {"ok": False,
                "error": "нет пригодных зон/контуров для раскладки",
                "notes": notes}

    base = {
        "tile": req.get("tile"),
        "gap": req.get("gap"),
    }
    for key in ("origin", "datum", "mode", "min_cut", "vjoints"):
        if req.get(key) is not None:
            base[key] = req.get(key)

    inserts, per_zone = [], []
    tot_full = tot_cut = 0
    for zone_id, outer_id, contour in items:
        creq = dict(base)
        creq["contours"] = [contour]
        res = cp.cladding_plan(creq)
        if not res.get("ok"):
            return {"ok": False, "error": res.get("error"), "notes": notes}
        for t in res["inserts"]:
            t["zone"] = zone_id
            inserts.append(t)
        for n in res["notes"]:
            notes.append("%s: %s" % (zone_id, n))
        s = res["summary"]
        pz = {"zone_id": zone_id, "tiles": s["tiles"], "full": s["full"],
              "cut": s["cut"], "rows": s["rows"]}
        if outer_id is not None:
            pz["outer_id"] = outer_id
        per_zone.append(pz)
        tot_full += s["full"]
        tot_cut += s["cut"]

    return {
        "ok": True,
        "inserts": inserts,
        "notes": notes,
        "per_zone": per_zone,
        "summary": {"tiles": len(inserts), "full": tot_full,
                    "cut": tot_cut, "zones": len(per_zone)},
    }


def run(req):
    op = (req or {}).get("op")
    if op == "cladding":
        return op_cladding(req)
    return {"ok": False,
            "error": "неизвестный op: %r (ожидается cladding)" % op}


def main(argv):
    if len(argv) < 2:
        sys.stderr.write("usage: clad_engine in.json out.json\n")
        return 2
    out_path = argv[1]
    try:
        with open(argv[0], "r", encoding="utf-8") as f:
            req = json.load(f)
        res = run(req)
    except Exception:
        res = {"ok": False,
               "error": "внутренняя ошибка движка:\n" +
                        traceback.format_exc(limit=3)}
    try:
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False)
    except OSError as e:
        sys.stderr.write("не могу записать %s: %s\n" % (out_path, e))
        return 1
    return 0 if res.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
