#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""facades_engine.py — единый CLI фасадного движка (паттерн vitrage_engine).

Вызов (как из C#): facades_engine.exe in.json out.json
Файл out.json пишется ВСЕГДА (при внутренней ошибке — {"ok": false, ...}).

op="zones" — ручной режим Германа (этап 1): плоский список контуров из
выбора в AutoCAD -> зоны по вложенности -> валидация -> площади/погонажи.

op="cladding" — этап 2 (ATCLAD, отдельная команда по требованию
Германа): раскладка облицовки универсальным блоком по зонам ATFZONE
и/или голым полилиниям — см. op_cladding.__doc__ и docs/CLADDING.md.

Вход:
{
  "op": "zones",
  "contours": [{"id": "хэндл", "pts": [[x,y],...], "bulges": [...]?}, ...],
  "cladding": "керамогранит идальго 600х600",   // имя облицовки = имя слоя
  "zone_prefix": "Ф-",                            // префикс марок зон
  "start_index": 1,
  "units": "mm"
}

Выход:
{
  "ok": true,
  "zones": [ { "zone_id", "cladding", "outer_id", "opening_ids": [...],
               "label_pt": [x, y],                // центроид, исходные единицы
               "report": {facade_zone_report/1} } ],
  "zones_full": [ {facade_zone/1} ],              // для *_fzones.json
  "failed": [ { "zone_id", "outer_id", "issues": [...] } ],
  "issues": [ ... ],                              // глобальные (разбор контуров)
  "summary": { "count", "area_net_total_m2", "sills_total_m",
               "jambs_total_m", "openings_total" }
}

ТОЛЬКО stdlib (движок замораживается PyInstaller).
"""

import json
import sys
import traceback

import cladding_plan as cp
import facade_zones as fz


def _issue_list(issues):
    return [i.as_dict() for i in issues]


_SUM_KEYS = ("area_outer_m2", "openings_total_m2", "area_net_m2",
             "perimeter_outer_m", "openings_perimeter_total_m",
             "sills_total_m", "jambs_total_m", "on_boundary_total_m")


def _merge_reports(reps):
    """Сводный отчёт объединённой зоны = суммы частей (фидбэк №2 п.1)."""
    out = dict(reps[0])
    for key in _SUM_KEYS:
        out[key] = sum(r.get(key, 0.0) for r in reps)
    out["openings_count"] = sum(r.get("openings_count", 0) for r in reps)
    ops = []
    for r in reps:
        ops.extend(r.get("openings", []))
    out["openings"] = ops
    warns = []
    for r in reps:
        warns.extend(r.get("warnings", []))
    out["warnings"] = warns
    return out


def op_zones(req):
    contours = req.get("contours") or []
    if not isinstance(contours, list) or not contours:
        return {"ok": False, "error": "contours: пустой список"}
    cladding = str(req.get("cladding") or "").strip()
    prefix = str(req.get("zone_prefix") or "Ф-")
    try:
        start = int(req.get("start_index") or 1)
    except (TypeError, ValueError):
        start = 1
    units = req.get("units") or "mm"
    merge = bool(req.get("merge"))

    zone_dicts, global_issues = fz.build_zones_from_contours(
        contours, cladding=cladding, zone_prefix=prefix,
        start_index=start, units=units,
        source={"method": "manual", "tool": "ATFZONE"})

    parts = []   # (zd, Zone, issues, report, dims, label)
    failed = []
    for zd in zone_dicts:
        try:
            z = fz.load_zone(zd)
        except fz.ZoneFormatError as e:
            failed.append({"zone_id": zd.get("id", "?"),
                           "outer_id": zd.get("meta", {}).get(
                               "outer_contour_id", "?"),
                           "issues": [{"code": "E_BAD_FORMAT",
                                       "level": "error", "where": "zone",
                                       "msg": str(e)}]})
            continue
        issues = fz.validate_zone(z)
        if fz._has_errors(issues):
            failed.append({"zone_id": z.id,
                           "outer_id": zd["meta"]["outer_contour_id"],
                           "issues": _issue_list(issues)})
            continue
        rep = fz.zone_report(z, issues)
        label = fz._centroid([(p[0], p[1]) for p in z.outer.polygonized()])
        parts.append((zd, z, issues, rep, fz.zone_dims(z), label))

    zones_ok, zones_full = [], []
    tot_net = tot_sills = tot_jambs = 0.0
    tot_ops = 0
    if merge and parts:
        # одна зона из всех валидных частей: сводный отчёт, марка у
        # крупнейшей части, размеры по каждой части
        zid = "%s%d" % (prefix, start)
        big = max(parts, key=lambda p: p[3]["area_outer_m2"])
        rep = _merge_reports([p[3] for p in parts])
        bb = [min(p[0]["meta"]["bbox"][0] for p in parts),
              min(p[0]["meta"]["bbox"][1] for p in parts),
              max(p[0]["meta"]["bbox"][2] for p in parts),
              max(p[0]["meta"]["bbox"][3] for p in parts)]
        op_ids = []
        for p in parts:
            op_ids.extend(o["id"] for o in p[0]["openings"])
        zones_ok.append({
            "zone_id": zid,
            "cladding": cladding,
            "merged": True,
            "part_count": len(parts),
            "outer_ids": [p[0]["meta"]["outer_contour_id"] for p in parts],
            "opening_ids": op_ids,
            "label_pt": [big[5][0], big[5][1]],
            "bbox": bb,
            "report": rep,
            "dims": [p[4] for p in parts],
        })
        for i, p in enumerate(parts):
            zd = p[0]
            zd["id"] = "%s.%d" % (zid, i + 1)
            zd["meta"]["group"] = zid
            zones_full.append(zd)
        tot_net = rep["area_net_m2"]
        tot_sills = rep["sills_total_m"]
        tot_jambs = rep["jambs_total_m"]
        tot_ops = rep["openings_count"]
    else:
        for zd, z, issues, rep, dims, label in parts:
            zones_ok.append({
                "zone_id": z.id,
                "cladding": cladding,
                "outer_id": zd["meta"]["outer_contour_id"],
                "outer_ids": [zd["meta"]["outer_contour_id"]],
                "opening_ids": [o["id"] for o in zd["openings"]],
                "label_pt": [label[0], label[1]],
                "bbox": zd["meta"]["bbox"],
                "report": rep,
                "dims": [dims],
            })
            zones_full.append(zd)
            tot_net += rep["area_net_m2"]
            tot_sills += rep["sills_total_m"]
            tot_jambs += rep["jambs_total_m"]
            tot_ops += rep["openings_count"]

    return {
        "ok": True,
        "zones": zones_ok,
        "zones_full": zones_full,
        "failed": failed,
        "issues": _issue_list(global_issues),
        "summary": {
            "count": len(zones_ok),
            "area_net_total_m2": tot_net,
            "sills_total_m": tot_sills,
            "jambs_total_m": tot_jambs,
            "openings_total": tot_ops,
        },
    }


_ARC_EPS = 1e-9


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


def op_cladding(req):
    """op="cladding" — этап 2 (ATCLAD): раскладка облицовки по зонам
    ATFZONE и/или голым замкнутым полилиниям.

    Вход:
    {
      "op": "cladding",
      "tile": {"w", "h"}, "gap": {"v", "h"}, "datum": <мм>,
      "mode": "openings"|"edge", "min_cut"?: <мм, дефолт 150>,
      "zones":    [{"zone_id", "zone": {facade_zone/1}}, ...],
      "contours": [{"id", "pts", "bulges"?}, ...]   // голые полилинии
    }
    Голые контуры группируются по вложенности (контур в контуре =
    проём) — тем же кодом, что ATFZONE.

    Выход: {"ok", "inserts": [{x,y,w,h,zone}], "notes",
            "per_zone": [{"zone_id","outer_id"?,"tiles","full","cut",
                          "rows"}],
            "summary": {"tiles","full","cut","zones"}}

    Раскладка позонная, горизонт общий: сетка рядов везде от datum —
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

    raw = req.get("contours") or []
    if raw:
        zone_dicts, issues = fz.build_zones_from_contours(
            raw, zone_prefix="контур-", start_index=1, units="mm",
            source={"method": "manual", "tool": "ATCLAD"})
        for i in issues:
            d = i.as_dict()
            notes.append("%s: %s" % (d.get("where"), d.get("msg")))
        for zd in zone_dicts:
            outer_id = zd.get("meta", {}).get("outer_contour_id", "?")
            zone_id = "контур %s" % outer_id
            c = _zone_to_contour(zd, notes, zone_id)
            if c is not None:
                items.append((zone_id, outer_id, c))

    if not items:
        return {"ok": False,
                "error": "нет пригодных зон/контуров для раскладки",
                "notes": notes}

    base = {
        "tile": req.get("tile"),
        "gap": req.get("gap"),
        "datum": req.get("datum", 0.0),
        "mode": req.get("mode"),
    }
    if req.get("min_cut") is not None:
        base["min_cut"] = req.get("min_cut")

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
    if op == "zones":
        return op_zones(req)
    if op == "cladding":
        return op_cladding(req)
    return {"ok": False,
            "error": "неизвестный op: %r (ожидается zones|cladding)" % op}


def main(argv):
    if len(argv) < 2:
        sys.stderr.write("usage: facades_engine in.json out.json\n")
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
