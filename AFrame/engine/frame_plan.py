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

Стойка по оси jx рвётся проёмами, ПЕРЕКРЫВАЮЩИМИ jx по X (bbox).
Фидбэк Германа 26.07 (первый прогон ATFRAME):
- ось БЛИЖЕ edge_offset (100, «не ближе 100 мм от края — бетон
  колется») к грани проёма → на высоте проёма стойка идёт ОТДЕЛЬНЫМ
  куском, смещённым на ось «грань − edge_offset» (слева) /
  «грань + edge_offset» (справа); кронштейны и кляммеры куска — на
  смещённой оси; под/над проёмом — по оси руста;
- floors_y пуст, но задан floor_step → отметки перекрытий
  генерируются от низа контура шагом floor_step (этаж ~3000):
  направляющие не бывают «бесконечными»;
- направляющая длиннее rail_stock — note (хлыст 6000).
Сегменты стойки режутся отметками перекрытий: направляющая = кусок
между стыками (зазор rail_gap центрован на отметке). Кронштейны:
несущий на каждой отметке внутри сегмента; рядовые — равномерно в
промежутках (первый — bracket_start_offset от низа куска), шаг ≤
bracket_step (в угловой зоне — bracket_step_corner). Только stdlib
(движок замораживается PyInstaller).
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


def _rail_brackets(a, b, start_off, step):
    """Кронштейны ОДНОЙ направляющей [a, b] — ТЗ Германа 26.07:
    первый 300 от НИЗА, последний 300 от ВЕРХА, между ними равномерно
    с шагом ≤ расчётного (3000 → 300-800-800-800-300 = 4 шт);
    нестандартная длина — крайние 300/300, между ними ≤ шага;
    короче 2×300 — один кронштейн в центре."""
    L = b - a
    if L <= 2 * start_off + EPS:
        return [a + L / 2.0]
    lo, hi = a + start_off, b - start_off
    k = max(1, int(math.ceil((hi - lo - EPS) / step))) if step else 1
    return [lo + (hi - lo) * j / k for j in range(k + 1)]


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
    edge_off = float(system.get("edge_offset") or 0.0)
    overhang = float(system.get("edge_overhang") or 0.0)
    mid_over = system.get("mid_rail_over")
    mid_over = float(mid_over) if mid_over else None
    stock = float(system.get("rail_stock") or 0.0)
    floor_step = None
    try:
        fs = req.get("floor_step")
        if fs is not None and float(fs) > EPS:
            floor_step = float(fs)
    except (TypeError, ValueError):
        floor_step = None

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
        # отметки перекрытий: заданные, либо автогенерация шагом
        # этажа от низа контура (фидбэк Германа 26.07)
        floors_c = floors
        if not floors and floor_step:
            floors_c = []
            f = y0 + floor_step
            while f < y1 - EPS:
                floors_c.append(f)
                f += floor_step
            if floors_c:
                notes.append(
                    "контур %d: перекрытия автоматически шагом %.0f "
                    "(%d шт)" % (ci + 1, floor_step, len(floors_c)))

        # доп. направляющая по центру плиты шире 600 (ТЗ 26.07):
        # пролёт между соседними осями больше порога → ось в середине
        jx_all = [j for j in joints if x0 - EPS <= j <= x1 + EPS]
        if mid_over and len(jx_all) >= 2:
            mids = []
            for i in range(len(jx_all) - 1):
                if jx_all[i + 1] - jx_all[i] > mid_over + EPS:
                    mids.append((jx_all[i] + jx_all[i + 1]) / 2.0)
            jx_all = sorted(jx_all + mids)

        for jx in jx_all:
            # угловая зона (В17: типовой случай — полоса у краёв зоны)
            in_corner = corner_zone > EPS and (
                jx - x0 <= corner_zone + EPS or
                x1 - jx <= corner_zone + EPS)
            step = step_corner if in_corner else step_main

            # куски стойки (lo, hi, ось): проём, накрывающий ось по X,
            # ВЫРЕЗАЕТ диапазон; ось ближе edge_offset к грани проёма —
            # на высоте проёма кусок СМЕЩАЕТСЯ от грани (26.07: крепёж
            # не ближе 100 мм от края проёма)
            pieces = [(y0, y1, jx)]
            for bx0, by0, bx1, by1 in hole_boxes:
                inside = bx0 - EPS < jx < bx1 + EPS
                sx = None
                if not inside and edge_off > EPS:
                    if jx <= bx0 + EPS and bx0 - jx < edge_off - EPS:
                        sx = bx0 - edge_off      # слева от проёма
                    elif jx >= bx1 - EPS and jx - bx1 < edge_off - EPS:
                        sx = bx1 + edge_off      # справа от проёма
                if not inside and sx is None:
                    continue
                nxt = []
                # смещённый оконный кусок выступает за проём на
                # overhang сверху и снизу (ТЗ 26.07: длина = сторона
                # окна + 100, выступ 50/50)
                sy0 = by0 - (overhang if sx is not None else 0.0)
                sy1 = by1 + (overhang if sx is not None else 0.0)
                for lo, hi, xe in pieces:
                    c0, c1 = max(lo, sy0 if sx is not None else by0), \
                             min(hi, sy1 if sx is not None else by1)
                    if c1 - c0 <= EPS or (xe != jx):
                        # не пересекает по высоте / кусок уже смещён
                        nxt.append((lo, hi, xe))
                        continue
                    if c0 - lo > EPS:
                        nxt.append((lo, c0, xe))
                    if inside:
                        pass                     # вырез
                    else:
                        nxt.append((c0, c1, sx))
                    if hi - c1 > EPS:
                        nxt.append((c1, hi, xe))
                pieces = nxt

            for s_lo, s_hi, s_x in pieces:
                if s_hi - s_lo <= EPS:
                    continue
                side = abs(s_x - jx) > EPS       # оконный (смещённый)
                fl_in = [f for f in floors_c
                         if s_lo + EPS < f < s_hi - EPS]
                # направляющие: куски между стыками (стык центрован
                # на отметке перекрытия, зазор gap — В15/В18);
                # кронштейны — НА КАЖДУЮ направляющую: 300 от низа,
                # 300 от верха, между ними равномерно ≤ шага (ТЗ
                # Германа 26.07); первый кронштейн направляющей,
                # начавшейся стыком на перекрытии, — «несущий» (В-е)
                cuts = [s_lo] + fl_in + [s_hi]
                for i in range(len(cuts) - 1):
                    a = cuts[i] + (gap / 2.0 if i > 0 else 0.0)
                    b = cuts[i + 1] - (gap / 2.0
                                       if i + 1 < len(cuts) - 1
                                       else 0.0)
                    if b - a <= EPS:
                        continue
                    if stock > EPS and b - a > stock + EPS:
                        notes.append(
                            "направляющая X=%.0f длиной %.0f > хлыста "
                            "%.0f — задайте перекрытия" % (s_x, b - a,
                                                           stock))
                    rails.append({"x": round(s_x, 4),
                                  "y0": round(a, 4),
                                  "y1": round(b, 4),
                                  "len": round(b - a, 4)})
                    if step is not None and start_off is not None:
                        pos = _rail_brackets(a, b, float(start_off),
                                             float(step))
                        for pi, y in enumerate(pos):
                            kind = ("несущий" if i > 0 and pi == 0
                                    else "рядовой")
                            brackets.append({"x": round(s_x, 4),
                                             "y": round(y, 4),
                                             "kind": kind})
                # заглушка межэтажной (bracket_step null): несущие
                # на отметках — до своего генератора (FRAME_TZ §2)
                if step is None or start_off is None:
                    for f in fl_in:
                        brackets.append({"x": round(s_x, 4),
                                         "y": round(f, 4),
                                         "kind": "несущий"})
                # кляммеры по швам раскладки (ТЗ 26.07): на оконных
                # (смещённых) стойках — БОКОВЫЕ (примыкание к окну/
                # отливу; закрыт и В-а); первый шов над стыком
                # направляющих (термошов) — КОМБИНИРОВАННЫЙ; старт
                # зоны и над верхним откосом — стартовый
                if rows:
                    clamps.append({"x": round(s_x, 4),
                                   "y": round(s_lo, 4),
                                   "kind": "боковой" if side
                                   else "стартовый"})
                    for ry in rows:
                        if not (s_lo + EPS < ry < s_hi - EPS):
                            continue
                        kind = "боковой" if side else "рядовой"
                        if not side and any(
                                f < ry and not any(
                                    f < r2 < ry for r2 in rows)
                                for f in fl_in):
                            kind = "комбинированный"
                        clamps.append({"x": round(s_x, 4),
                                       "y": round(ry, 4),
                                       "kind": kind})

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
        "clamps_side": sum(1 for cl in clamps
                           if cl["kind"] == "боковой"),
        "clamps_combo": sum(1 for cl in clamps
                            if cl["kind"] == "комбинированный"),
    }
    return {"ok": True, "rails": rails, "brackets": brackets,
            "clamps": clamps, "summary": summary, "notes": notes,
            "system_used": system_used}
