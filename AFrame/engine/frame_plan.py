# -*- coding: utf-8 -*-
"""Расстановка подсистемы НВФ: направляющие (стойки) + кронштейны +
кляммеры (модуль AFrame, этап 3). Концепт — Facades/docs/STAGE3_FRAME.md.

Решения Дениса 24.07:
- порядок целевой «расчёт несущей способности → расстановка»; ПОКА шаги
  берутся из справочника систем (systems.json) и подтверждаются
  конструктором — модуль расчёта (этап 4) встанет ПЕРЕД этим движком;
- В16: рядовые кронштейны между несущими РАВНОМЕРНО («размазывая длину
  между перекрытиями на количество кронштейнов»);
- В18: зазор стыка направляющих — дефолт 10 (5–10 по системам);
- В17: угловая зона — типовой случай, ширина ≈ одной плитки; спецузлы
  углов (крест-накрест, короба жёсткости) НЕ моделируем.

Факты боевого DWG Ленпроспекта (эталон
atspec-testdata/dxf/facades/frame_lenprospekt/frame_ps.json):
стойки по осям с шагом 602.5–605; первый кронштейн ~300 от низа стойки,
дальше ~950 (3 шт на этаж ~3 м — сходится со словами Дениса); кляммеры:
стартовый на низе и над проёмами, рядовой на каждом горизонтальном
стыке (шаг 605), половинки на краях.

Модель (вход, JSON):
{
  "op": "frame",
  "system": "Standart" | {…переопределения…},  // имя из systems.json
  "contours": [{"outer": [[x,y]..], "holes": [[[x,y]..]..]}],
  "joints_x": [x, ...],   // ОСИ вертикальных рустов = оси стоек
                          // (из метки ATCLAD / кромок камней)
  "floors_y": [y, ...],   // отметки перекрытий (центр несущего — В15;
                          // стык направляющих центрован на отметке)
  "rows_y": [y, ...]      // низы горизонтальных швов раскладки —
                          // для кляммеров (опционально)
}

Выход: rails[{x,y0,y1,len}], brackets[{x,y,kind:"несущий"|"рядовой"}],
clamps[{x,y,kind:"стартовый"|"рядовой"}], summary, notes.

Стойка по оси jx рвётся проёмами, ПЕРЕКРЫВАЮЩИМИ jx по X (bbox);
оси у ГРАНЕЙ проёмов (грань ± полруста) в bbox не попадают — стойка
сбоку окна сплошная. Сегменты стойки режутся отметками перекрытий:
направляющая = кусок между стыками (зазор rail_gap центрован на
отметке). Кронштейны: несущий на каждой отметке внутри сегмента;
рядовые — равномерно в промежутках (первый — bracket_start_offset от
низа стойки), шаг ≤ bracket_step (в угловой зоне — bracket_step_corner).
Только stdlib (движок замораживается PyInstaller).
"""
import json
import math
import os
import sys

EPS = 1e-6


def _closed(pts):
    p = [(float(a), float(b)) for a, b in pts]
    if len(p) >= 2 and abs(p[0][0] - p[-1][0]) < EPS and \
       abs(p[0][1] - p[-1][1]) < EPS:
        p = p[:-1]
    return p


def _bbox(poly):
    xs = [p[0] for p in poly]
    ys = [p[1] for p in poly]
    return min(xs), min(ys), max(xs), max(ys)


def _sub_y(spans, lo, hi):
    """Вычесть [lo, hi] из списка Y-интервалов (копия cladding_plan —
    модули развязаны сознательно, конвенция репо)."""
    out = []
    for a0, a1 in spans:
        c0, c1 = max(a0, lo), min(a1, hi)
        if c1 - c0 <= EPS:
            out.append((a0, a1))
            continue
        if c0 - a0 > EPS:
            out.append((a0, c0))
        if a1 - c1 > EPS:
            out.append((c1, a1))
    return out


def _systems_path(base_dir=None):
    """systems.json: рядом с exe (бандл, PyInstaller-onefile: __file__
    уходит в temp!), в _MEIPASS, рядом с .py (разработка)."""
    cands = []
    if base_dir:
        cands.append(base_dir)
    if getattr(sys, "frozen", False):
        cands.append(os.path.dirname(sys.executable))
        if hasattr(sys, "_MEIPASS"):
            cands.append(sys._MEIPASS)
    cands.append(os.path.dirname(os.path.abspath(__file__)))
    for d in cands:
        p = os.path.join(d, "systems.json")
        if os.path.exists(p):
            return p
    raise OSError("systems.json не найден рядом с движком (%s)"
                  % "; ".join(cands))


def load_system(name_or_dict, base_dir=None):
    """Система по имени из systems.json рядом с движком; dict —
    переопределение поверх имени в ключе "name" (или чистый dict)."""
    with open(_systems_path(base_dir), "r", encoding="utf-8") as f:
        allsys = json.load(f)["systems"]
    if isinstance(name_or_dict, dict):
        name = name_or_dict.get("name")
        base = dict(allsys.get(name, {})) if name else {}
        base.update({k: v for k, v in name_or_dict.items()
                     if k != "name"})
        base.setdefault("_name", name or "своя")
        return base
    if name_or_dict not in allsys:
        raise KeyError("система %r не найдена в systems.json"
                       % name_or_dict)
    s = dict(allsys[name_or_dict])
    s["_name"] = name_or_dict
    return s


def _row_positions(lo, hi, step):
    """Рядовые кронштейны в промежутке (lo, hi), где на КОНЦАХ уже
    есть кронштейны (или верх без кронштейна): равномерно, шаг ≤ step
    (В16 — «размазывая длину»). Возвращает внутренние позиции."""
    L = hi - lo
    if step is None or L <= step + EPS:
        return []
    k = int(math.ceil((L - EPS) / step))
    return [lo + L * j / k for j in range(1, k)]


def frame_plan(req):
    try:
        system = load_system(req.get("system") or "Standart",
                             req.get("_systems_dir"))
    except (KeyError, OSError, ValueError) as e:
        return {"ok": False, "error": str(e)}
    step_main = system.get("bracket_step")
    step_corner = system.get("bracket_step_corner")
    start_off = system.get("bracket_start_offset")
    corner_zone = float(system.get("corner_zone") or 0.0)
    gap = float(system.get("rail_gap") or 0.0)

    joints = []
    for x in req.get("joints_x") or []:
        try:
            joints.append(float(x))
        except (TypeError, ValueError):
            continue
    joints = sorted(set(joints))
    floors = []
    for y in req.get("floors_y") or []:
        try:
            floors.append(float(y))
        except (TypeError, ValueError):
            continue
    floors = sorted(set(floors))
    rows = []
    for y in req.get("rows_y") or []:
        try:
            rows.append(float(y))
        except (TypeError, ValueError):
            continue
    rows = sorted(set(rows))

    notes, rails, brackets, clamps = [], [], [], []
    if not joints:
        return {"ok": False,
                "error": "нет осей стоек (joints_x) — раскладка "
                         "ATCLAD не выбрана и оси не заданы"}

    for ci, c in enumerate(req.get("contours") or []):
        outer = _closed(c.get("outer") or [])
        if len(outer) < 4:
            notes.append("контур %d: меньше 4 вершин — пропуск"
                         % (ci + 1))
            continue
        holes = [_closed(h) for h in (c.get("holes") or [])]
        x0, y0, x1, y1 = _bbox(outer)
        hole_boxes = [_bbox(h) for h in holes]

        for jx in joints:
            if jx < x0 - EPS or jx > x1 + EPS:
                continue
            # угловая зона (В17: типовой случай — полоса у краёв зоны)
            in_corner = corner_zone > EPS and (
                jx - x0 <= corner_zone + EPS or
                x1 - jx <= corner_zone + EPS)
            step = step_corner if in_corner else step_main

            # стойка рвётся проёмами, накрывающими ось по X
            spans = [(y0, y1)]
            for bx0, by0, bx1, by1 in hole_boxes:
                if bx0 - EPS < jx < bx1 + EPS:
                    spans = _sub_y(spans, by0, by1)

            for s_lo, s_hi in spans:
                if s_hi - s_lo <= EPS:
                    continue
                fl_in = [f for f in floors
                         if s_lo + EPS < f < s_hi - EPS]
                # направляющие: куски между стыками (стык центрован
                # на отметке перекрытия, зазор gap — В15/В18)
                cuts = [s_lo] + fl_in + [s_hi]
                for i in range(len(cuts) - 1):
                    a = cuts[i] + (gap / 2.0 if i > 0 else 0.0)
                    b = cuts[i + 1] - (gap / 2.0
                                       if i + 1 < len(cuts) - 1
                                       else 0.0)
                    if b - a <= EPS:
                        continue
                    rails.append({"x": round(jx, 4),
                                  "y0": round(a, 4),
                                  "y1": round(b, 4),
                                  "len": round(b - a, 4)})
                # кронштейны: несущий на каждой отметке (В15 — центр)
                for f in fl_in:
                    brackets.append({"x": round(jx, 4),
                                     "y": round(f, 4),
                                     "kind": "несущий"})
                # рядовые: якорь снизу — start_offset от низа стойки
                # (факт DWG: ~300), дальше равномерно между несущими
                if step is not None and start_off is not None:
                    first = s_lo + float(start_off)
                    if first < s_hi - EPS:
                        anchors = [first] + fl_in
                        tops = fl_in + [s_hi]
                        if not fl_in or first < fl_in[0] - EPS:
                            brackets.append({"x": round(jx, 4),
                                             "y": round(first, 4),
                                             "kind": "рядовой"})
                        else:
                            anchors = fl_in
                            tops = fl_in[1:] + [s_hi]
                        for a, b in zip(anchors, tops):
                            for y in _row_positions(a, b, float(step)):
                                brackets.append({"x": round(jx, 4),
                                                 "y": round(y, 4),
                                                 "kind": "рядовой"})
                # кляммеры (по горизонтальным швам раскладки):
                # стартовый на низе сегмента (низ зоны / над проёмом),
                # рядовой — на каждом шве внутри сегмента
                if rows:
                    clamps.append({"x": round(jx, 4),
                                   "y": round(s_lo, 4),
                                   "kind": "стартовый"})
                    for ry in rows:
                        if s_lo + EPS < ry < s_hi - EPS:
                            clamps.append({"x": round(jx, 4),
                                           "y": round(ry, 4),
                                           "kind": "рядовой"})

    lm = sum(r["len"] for r in rails) / 1000.0
    stock = float(system.get("rail_stock") or 0.0)
    system_used = {k: v for k, v in system.items()
                   if not k.startswith("_src")}
    summary = {
        "system": system.get("_name", "?"),
        "rails": len(rails),
        "rails_lm": round(lm, 2),
        "rail_stock_est": (int(math.ceil(lm * 1000.0 / stock))
                           if stock > EPS else None),
        "brackets_main": sum(1 for b in brackets
                             if b["kind"] == "несущий"),
        "brackets_row": sum(1 for b in brackets
                            if b["kind"] == "рядовой"),
        "clamps_start": sum(1 for cl in clamps
                            if cl["kind"] == "стартовый"),
        "clamps_row": sum(1 for cl in clamps
                          if cl["kind"] == "рядовой"),
    }
    return {"ok": True, "rails": rails, "brackets": brackets,
            "clamps": clamps, "summary": summary, "notes": notes,
            "system_used": system_used}
