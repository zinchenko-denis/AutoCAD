#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""facades_engine.py — единый CLI фасадного движка (паттерн vitrage_engine).

Вызов (как из C#): facades_engine.exe in.json out.json
Файл out.json пишется ВСЕГДА (при внутренней ошибке — {"ok": false, ...}).

op="zones" — ручной режим Германа (этап 1): плоский список контуров из
выбора в AutoCAD -> зоны по вложенности -> валидация -> площади/погонажи.

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

import facade_zones as fz


def _issue_list(issues):
    return [i.as_dict() for i in issues]


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

    zone_dicts, global_issues = fz.build_zones_from_contours(
        contours, cladding=cladding, zone_prefix=prefix,
        start_index=start, units=units,
        source={"method": "manual", "tool": "ATFZONE"})

    zones_ok, zones_full, failed = [], [], []
    tot_net = tot_sills = tot_jambs = 0.0
    tot_ops = 0
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
        k = z.to_mm()
        label = fz._centroid([(p[0], p[1]) for p in z.outer.polygonized()])
        zones_ok.append({
            "zone_id": z.id,
            "cladding": cladding,
            "outer_id": zd["meta"]["outer_contour_id"],
            "opening_ids": [o["id"] for o in zd["openings"]],
            "label_pt": [label[0], label[1]],
            "report": rep,
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


def run(req):
    op = (req or {}).get("op")
    if op == "zones":
        return op_zones(req)
    return {"ok": False, "error": "неизвестный op: %r (ожидается zones)" % op}


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
