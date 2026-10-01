# -*- coding: utf-8 -*-
"""Расстановка подсистемы НВФ: направляющие (стойки) + кронштейны +
кляммеры (модуль AFrame, этап 3). Концепт — Facades/docs/STAGE3_FRAME.md.

Текущее состояние (24.09): три схемы (вертикальная, межэтажная,
ортогональная), расчёт несущей способности (frame_calc, этап 4) подбирает
шаги ДО расстановки — грузовая ширина не меньше фактической по итоговым
осям стоек (24.09); режимы «что раскладывать» (подсистема и кляммеры /
только подсистема / только кляммеры по существующим направляющим);
контур стены — полигон (Г, ступени, П, фронтон, треугольник), проёмы —
по габариту (непрямоугольный — нота).

Решения Дениса 24.07 (история):
- порядок целевой «расчёт несущей способности → расстановка» (реализован
  этапом 4, frame_calc); шаги справочника систем (systems.json) — когда
  расчёт не запрошен (ручные шаги);
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

from frame_rules import resolve_layout_contract, declared_scope
from frame_topology import geometric_members, screen_layout, refusal as topology_refusal
from frame_supports import (rail_cuts as _rail_cuts, rail_brackets as _rail_brackets,
                            refine_vertical_supports)
from frame_connections import build_connection_passport
from frame_solution_selection import prepare_solution, _UNPREPARED

EPS = 1e-6
CLAMP_MERGE = 100.0  # мм: ближе — один кляммер (кромка откоса
                     # и шов раскладки почти совпали, 30.07 п.5)
WIN_NEAR = 200.0     # мм: стойка «принадлежит» грани проёма
NSP_MIN_PIER = 60.0  # мм: простенок уже самого узкого С-профиля (АТР: a = 60) — НСП вдоль откоса
                     # там не помещается (окна практически вплотную), 30.09b


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


def _vspans(poly, x):
    """Y-интервалы, где вертикаль x проходит ВНУТРИ полигона стены (чёт-нечёт,
    полуоткрытое правило по X). 23.09 (ревью): раньше всё ставилось по
    ГАБАРИТУ контура — у Г-образной, ступенчатой, П-образной стены и фронтона
    стойки/кляммеры висели там, где стены нет. Ось на самой кромке габарита
    сдвигается внутрь на 1e-6, чтобы не потерять стойку у края."""
    xs = [q[0] for q in poly]
    x = min(max(x, min(xs) + 1e-6), max(xs) - 1e-6)
    ys = []
    n = len(poly)
    for i in range(n):
        (xa, ya), (xb, yb) = poly[i], poly[(i + 1) % n]
        if (xa <= x < xb) or (xb <= x < xa):
            ys.append(ya + (x - xa) * (yb - ya) / (xb - xa))
    ys.sort()
    return [(ys[k], ys[k + 1]) for k in range(0, len(ys) - 1, 2)
            if ys[k + 1] - ys[k] > EPS]


def _hspans(poly, y):
    """X-интервалы горизонтали y внутри полигона стены (как _vspans)."""
    ys = [q[1] for q in poly]
    y = min(max(y, min(ys) + 1e-6), max(ys) - 1e-6)
    xs = []
    n = len(poly)
    for i in range(n):
        (xa, ya), (xb, yb) = poly[i], poly[(i + 1) % n]
        if (ya <= y < yb) or (yb <= y < ya):
            xs.append(xa + (y - ya) * (xb - xa) / (yb - ya))
    xs.sort()
    return [(xs[k], xs[k + 1]) for k in range(0, len(xs) - 1, 2)
            if xs[k + 1] - xs[k] > EPS]


def _inside_pt(poly, x, y, tol=1.0):
    return any(lo - tol <= y <= hi + tol for lo, hi in _vspans(poly, x))


def _clip_pieces(poly, pieces):
    """Куски (lo, hi, x) ∩ стена по вертикали x (смещённая оконная стойка
    у края зоны могла выйти за контур)."""
    xs = [q[0] for q in poly]
    out = []
    for lo, hi, xe in pieces:
        if xe < min(xs) - EPS or xe > max(xs) + EPS:
            continue                     # ось вне габарита стены
        for a, b in _vspans(poly, xe):
            c0, c1 = max(lo, a), min(hi, b)
            if c1 - c0 > EPS:
                out.append((c0, c1, xe))
    return out


def _ytop(poly, x, y, default):
    """Верх стены над точкой (x, y): кромка интервала, в котором лежит y —
    «последний ряд по высоте» у Г/ступеней/фронтона не на верху габарита."""
    for a, b in _vspans(poly, x):
        if a - 1.0 <= y <= b + 1.0:
            return b
    return default


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


def _dedup_pieces(staged, min_frag=100.0):
    """Куски стоек, сведённые по ОСИ X (04.08, D-сверка полигона).

    Смещённая оконная стойка одной оси и СОБСТВЕННАЯ ось руста могут
    дать одну и ту же координату X: ось, стоящая ровно в edge_offset
    от грани проёма, — цель смещения соседней оси (с ЛЮБОЙ стороны
    окна, поэтому порядком обхода не лечится). Раньше оба куска
    ложились в rails → два профиля в одном месте, дубли кронштейнов
    и кляммеров (полигон Германа: 4 оси × 16 позиций = 64 дубля).

    Приоритет — собственная ось (x == jx), затем смещённые в порядке
    появления; от смещённого остаётся только незанятая часть, осколки
    короче min_frag не ставим (порог фикса 03.08c).
    """
    by, order = {}, []
    for it in staged:
        k = round(it[2], 4)
        if k not in by:
            by[k] = []
            order.append(k)
        by[k].append(it)
    out = []
    for k in order:
        # стабильная сортировка: False (собственные) раньше True
        items = sorted(by[k], key=lambda t: abs(t[2] - t[3]) > EPS)
        taken = []
        for lo, hi, x, jx, step in items:
            spans = [(lo, hi)]
            for a, b in taken:
                spans = _sub_y(spans, a, b)
            moved = abs(x - jx) > EPS
            for a, b in spans:
                if b - a <= (min_frag if moved else EPS):
                    continue
                out.append((a, b, x, jx, step))
                taken.append((a, b))
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


def _span_points(a, b, step):
    """Точки от a до b ВКЛЮЧИТЕЛЬНО, равномерно с шагом ≤ step."""
    L = b - a
    if L <= EPS:
        return [a]
    k = max(1, int(math.ceil((L - EPS) / step)))
    return [a + L * j / k for j in range(k + 1)]


def _tile_axes(x0, x1, step, edge=100.0, tol=50.0, corners=None, czone=0.0,
               step_corner=None):
    """Оси вертикальных направляющих под бетонную/клинкерную плитку (Денис
    26.09, Герман 29.09): НЕ по швам облицовки, а заданным шагом по
    горизонтали. Первая — в edge (100 мм) от левого края зоны (как краевая и
    оконная стойка), дальше РОВНО шагом (ручной шаг ставится буквально —
    Герман 04.08 п.1), последняя — в edge от правого края, если до неё больше
    tol; короче шага выходит только последний пролёт. Зона уже 2×edge — одна
    ось посередине.

    29.09c (Герман, ответ по №27 п.5: «шаг углов здания также может
    меняться»): step_corner — шаг в УГЛОВОЙ зоне (0/None — как рядовой).
    Угловая зона — czone от указанного угла внутрь (углы не указаны — от обоих
    краёв зоны, как у кронштейнов). В угловой зоне оси идут ОТ УГЛА ровно
    step_corner, в рядовой части — от её левой границы ровно step; граница
    угловой зоны — общая ось, короче шага выходит только пролёт у границы."""
    a, b = x0 + edge, x1 - edge
    if b - a <= EPS:
        return [round((x0 + x1) / 2.0, 4)]
    sc = float(step_corner or 0.0)
    segs = [(a, b, float(step), 1)]
    if sc > EPS and abs(sc - step) > EPS and czone > EPS:
        cz = []
        if corners is None:
            cz = [(x0, x0 + czone, 1), (x1 - czone, x1, -1)]
        else:
            for c in corners:
                if c - x0 <= x1 - c:
                    cz.append((c, c + czone, 1))
                else:
                    cz.append((c - czone, c, -1))
        cz = sorted((max(lo, a), min(hi, b), d) for lo, hi, d in cz
                    if min(hi, b) - max(lo, a) > EPS)
        merged = []
        for lo, hi, d in cz:
            if merged and lo <= merged[-1][1] + EPS:
                merged[-1] = (merged[-1][0], max(merged[-1][1], hi), merged[-1][2])
            else:
                merged.append((lo, hi, d))
        segs, cur = [], a
        for lo, hi, d in merged:
            if lo - cur > EPS:
                segs.append((cur, lo, float(step), 1))
            segs.append((lo, hi, sc, d))
            cur = hi
        if b - cur > EPS:
            segs.append((cur, b, float(step), 1))
    pts = []                                # (x, приоритет: угловая зона выше)
    for lo, hi, st, d in segs:
        pr = 1 if abs(st - step) > EPS else 0
        q = []
        if d > 0:
            x = lo
            while x <= hi + EPS:
                q.append(x)
                x += st
            if hi - q[-1] > tol:
                q.append(hi)
        else:
            x = hi
            while x >= lo - EPS:
                q.append(x)
                x -= st
            if q[-1] - lo > tol:
                q.append(lo)
        pts += [(v, pr) for v in q]
    out = []                                # общая граница / почти совпали —
    for v, pr in sorted(pts):               # одна ось, угловая важнее
        if out and v - out[-1][0] <= tol:
            if pr > out[-1][1]:
                out[-1] = (v, pr)
            continue
        out.append((v, pr))
    return [round(float(v), 4) for v, _pr in out]


SHINA_KINDS = ("шина стартовая", "шина рядовая", "шина концевая")


def _pip_strict(poly, x, y):
    """Точка строго внутри полигона (по вертикали x, без допуска)."""
    return any(lo < y < hi for lo, hi in _vspans(poly, x))


def _cut_boxes_h(segs, y, boxes):
    """Горизонтальные интервалы на отметке y минус проёмы (по габариту)."""
    for bx0, by0, bx1, by1 in boxes:
        if by0 - EPS < y < by1 + EPS:
            segs = [(sa2, sb2) for sa, sb in segs
                    for sa2, sb2 in ((sa, min(sb, bx0)), (max(sa, bx1), sb))
                    if sb2 - sa2 > EPS]
    return segs


TILE_SUP_TOL = 20.0      # шина держится на профиле, если её высота в пределах профиля ±20 мм
TILE_MIN_WHIP = 300.0    # кусок шины короче 300 мм стыком не режем (тот же минимум, что у хлыста)
TILE_MIN_RUN = 5.0       # прогон шины короче 5 мм — шум контура (вершины с разницей в доли мм), не шина


def _tile_rails(outer, boxes, rows, merge=20.0):
    """Шины под бетонную/клинкерную плитку (Герман 29.09, ответ по №27 пп.1–2):
    «Шины бывают трех видов: стартовая, рядовая и концевая. Стартовая шина
    ставится внизу зоны, рядовая — по центру горизонтального шва, концевая —
    по верхней границе зоны».
    29.09l (Герман, ответ на 9б и 9д PDF №27): «стартовая, концевая ставится
    также над окнами и под окнами»; «концевая по наклону не нужна».

    - стартовая — по каждой ГОРИЗОНТАЛЬНОЙ кромке контура, над которой стена
      (низ зоны; у ступенчатой/Г-образной — на каждой ступени), и по ВЕРХУ
      каждого проёма, над которым стена; концевая — по каждой горизонтальной
      кромке, под которой стена (верх зоны), и по НИЗУ каждого проёма, под
      которым стена. У проёма — по ширине его габарита, по контуру стены и
      мимо соседних проёмов; наклонные кромки (фронтон) шину не получают;
    - рядовая — по центру каждого горизонтального шва (rows), по контуру стены;
      ряд ближе merge к стартовой/концевой линии на её участке не ставится;
    - окна режут шину по габариту проёма;
    - прогон (кусок между краями стены и окнами) возвращается ЦЕЛИКОМ: на хлысты
      его режет frame_plan по вертикальным профилям этой зоны, когда они
      расставлены (_whip_cuts: стык — на направляющей).

    Возвращает (runs, sloped_top_mm) — прогоны {y, x0, x1, len, kind, run} и
    длину наклонных кромок верха (концевой там нет)."""
    edges = []                               # (y, [(lo, hi)], kind)
    sloped = 0.0
    n = len(outer)
    for i in range(n):
        (xa, ya), (xb, yb) = outer[i], outer[(i + 1) % n]
        lo, hi = min(xa, xb), max(xa, xb)
        if hi - lo <= EPS:
            continue                         # вертикальная кромка
        xm, ym = (xa + xb) / 2.0, (ya + yb) / 2.0
        if abs(yb - ya) > 0.5:
            # наклонная: верх зоны, если стена под ней
            if _pip_strict(outer, xm, ym - 2.0) and not _pip_strict(outer, xm, ym + 2.0):
                sloped += math.hypot(xb - xa, yb - ya)
            continue
        up = _pip_strict(outer, xm, ym + 2.0)
        dn = _pip_strict(outer, xm, ym - 2.0)
        if up and not dn:
            edges.append((ym, _cut_boxes_h([(lo, hi)], ym, boxes), "шина стартовая"))
        elif dn and not up:
            edges.append((ym, _cut_boxes_h([(lo, hi)], ym, boxes), "шина концевая"))
    # 29.09l: над проёмом — стартовая, под проёмом — концевая (где за кромкой стена;
    # _hspans прижимает отметку к габариту стены — за верхом/низом стены проверяем сами)
    ys_o = [q[1] for q in outer]
    for bi, (bx0, by0, bx1, by1) in enumerate(boxes):
        others = boxes[:bi] + boxes[bi + 1:]
        for yy, kind, pr in ((by1, "шина стартовая", 2.0), (by0, "шина концевая", -2.0)):
            if not (min(ys_o) + EPS < yy + pr < max(ys_o) - EPS):
                continue                     # за кромкой проёма — не стена (верх/низ зоны)
            segs = [(max(sa, bx0), min(sb, bx1)) for sa, sb in _hspans(outer, yy + pr)]
            segs = _cut_boxes_h([(sa, sb) for sa, sb in segs if sb - sa > EPS], yy + pr, others)
            if segs:
                edges.append((yy, segs, kind))
    lines = list(edges)                      # (y, [(sa, sb)], kind)
    for yy in rows:
        segs = list(_hspans(outer, yy))
        for ey, esegs, _k in edges:
            if abs(ey - yy) < merge:
                # место занято стартовой/концевой на этом участке
                for lo, hi in esegs:
                    segs = [(sa2, sb2) for sa, sb in segs
                            for sa2, sb2 in ((sa, min(sb, lo)), (max(sa, hi), sb))
                            if sb2 - sa2 > EPS]
        lines.append((yy, _cut_boxes_h(segs, yy, boxes), "шина рядовая"))
    out, run = [], 0
    for yy, segs, kind in lines:
        for sa, sb in sorted(segs):
            # 29.09l (прогон на эталоне 290×82): у контуров с вершинами, разнесёнными на
            # доли миллиметра, выходили «шины» длиной 0,0001–1 мм — 524 штуки в счёте
            # хлыстов и пустые прямоугольники на чертеже; такие прогоны отбрасываем
            if sb - sa < TILE_MIN_RUN:
                continue
            run += 1
            out.append({"y": round(yy, 4), "x0": round(sa, 4), "x1": round(sb, 4),
                        "len": round(sb - sa, 4), "kind": kind, "run": run})
    return out, sloped


def _whip_cuts(sa, sb, sup, whip, min_piece=TILE_MIN_WHIP):
    """Стыки хлыстов прогона шины [sa, sb] — на направляющих (29.09l, Герман, ответ на
    9в PDF №27: «лучше делать стык хлыстов на направляющей; если шаг 600 — длина хлыста
    2400, стандартная 2500 подходит для шага 500»). sup — X вертикальных профилей, через
    которые шина проходит на своей высоте.

    От начала прогона (после окна — от грани проёма: «хлысты после окна считаются
    заново», 9а) стык ставится на САМУЮ ДАЛЬНЮЮ направляющую не дальше whip от
    предыдущего стыка — хлыст кратен шагу (600 → 2400, 500 → 2500); не на последнюю
    направляющую прогона (за ней — огрызок на одной опоре) и не ближе min_piece.
    Прогон не длиннее whip — один хлыст. Направляющей в пределах хлыста нет — стык на
    длине whip, без опоры (считается).
    Возвращает (стыки, стыков без направляющей)."""
    sup = sorted(x for x in sup if sa + EPS < x < sb - EPS)
    cuts, air, cur = [], 0, sa
    while sb - cur > whip + EPS:
        cand = [x for x in sup if cur + min_piece - EPS <= x <= cur + whip + EPS]
        good = [x for x in cand if x < sup[-1] - EPS]
        if good:
            nx = good[-1]
        elif cand:
            nx = cand[-1]
        else:
            nx, air = cur + whip, air + 1
        cuts.append(nx)
        cur = nx
    return cuts, air


def _zone_rail_axes(x0, x1, corners, czone, step_corner, step_main,
                    off=100.0, tol=50.0):
    """Оси стоек в УГЛОВЫХ и КРАЕВЫХ зонах (фидбэк Германа 04.08 п.2:
    «программа не дорисовывает профиль и кронштейны в угловых и
    краевых зонах»).

    Причина пропуска: стойки ставились ТОЛЬКО по осям рустов раскладки
    (rail_rule=every_joint), а у внешнего угла и у края облицовки
    рустов может не быть вовсе — там оставалась пустота.

    Правило Германа:
    - от указанного внешнего угла идёт угловая зона шириной czone
      ВНУТРЬ области (07.08, В-аз: «только во внутрь»); ПЕРВАЯ стойка
      в `off` (100 мм) от угла, дальше с шагом угловой зоны до конца
      зоны;
    - в КРАЕВОЙ зоне (внутренний угол здания или конец облицовки —
      то есть край области, где угол не указан) ПОСЛЕДНЯЯ стойка в
      `off` от границы, остальное как в рядовой.

    Возвращает список осей; вызывающий код сливает их с осями рустов,
    отбрасывая совпавшие ближе tol (иначе у угла вышел бы сдвоенный
    профиль).
    """
    out = []
    st_c = float(step_corner or step_main or 0.0)
    for c in (corners or []):
        sgn = 1.0 if c - x0 <= x1 - c else -1.0
        a = c + sgn * off
        if a < x0 - EPS or a > x1 + EPS:
            continue
        out.append(a)
        if st_c <= EPS:
            continue
        lim = c + sgn * czone
        x = a + sgn * st_c
        while (x <= lim + EPS if sgn > 0 else x >= lim - EPS) and \
                x0 - EPS <= x <= x1 + EPS:
            out.append(x)
            x += sgn * st_c
    # краевые зоны: край области, у которого нет указанного угла
    for edge, sgn in ((x0, 1.0), (x1, -1.0)):
        if any(abs(edge - c) <= czone + EPS for c in (corners or [])):
            continue
        out.append(edge + sgn * off)
    return sorted(set(round(v, 4) for v in out))


def _corner_ivals(x0, x1, czone, corners):
    """Интервалы угловых зон на [x0, x1]. corners=None — прежнее
    поведение (оба края контура); corners=[] — углов НЕТ (фидбэк
    Германа 27.07: угловая зона отсчитывается от УКАЗАННЫХ внешних
    углов здания, а не от краёв зоны); corners=[x..] — полоса czone
    от угла ВНУТРЬ области (Герман 07.08, В-аз: «угловая зона только
    во внутрь»). Внутрь = в сторону дальнего края области; для угла
    на краю это единственная сторона, для угла в середине берём
    направление от ближайшего края."""
    if czone <= EPS:
        return []
    if corners is None:
        return [(x0, x0 + czone), (x1 - czone, x1)]
    out = []
    for c in corners:
        if c - x0 <= x1 - c:
            a, b = max(x0, c), min(x1, c + czone)
        else:
            a, b = max(x0, c - czone), min(x1, c)
        if b - a > EPS:
            out.append((a, b))
    out.sort()
    merged = []
    for a, b in out:
        if merged and a <= merged[-1][1] + EPS:
            merged[-1] = (merged[-1][0], max(merged[-1][1], b))
        else:
            merged.append((a, b))
    return merged


def _in_corner(x, x0, x1, czone, corners):
    return any(a - EPS <= x <= b + EPS
               for a, b in _corner_ivals(x0, x1, czone, corners))


def _hpos(x0, x1, margin, czone, step_main, step_corner,
          corners=None):
    """Горизонтальные позиции кронштейнов (межэтажная/ортогональная,
    ТЗ 26.07): от margin до margin от краёв; в угловых зонах шаг ≤
    step_corner, вне — ≤ step_main; равномерно по интервалам, границы
    зон — общие точки."""
    a, b = x0 + margin, x1 - margin
    if b - a <= EPS:
        return [(a + b) / 2.0]
    ivals = [(max(a, ia), min(b, ib))
             for ia, ib in _corner_ivals(x0, x1, czone, corners)
             if min(b, ib) - max(a, ia) > EPS]
    bounds = [a]
    for ia, ib in ivals:
        for v in (ia, ib):
            if a + EPS < v < b - EPS:
                bounds.append(v)
    bounds.append(b)
    bounds = sorted(set(bounds))
    pts = []
    for i in range(len(bounds) - 1):
        sa, sb = bounds[i], bounds[i + 1]
        mid = (sa + sb) / 2.0
        in_c = any(ia - EPS <= mid <= ib + EPS for ia, ib in ivals)
        seg = _span_points(sa, sb, step_corner if in_c else step_main)
        pts += seg if not pts else seg[1:]
    return pts


def _in_boxes(boxes, x, y):
    return any(bx0 - EPS < x < bx1 + EPS and by0 - EPS < y < by1 + EPS
               for bx0, by0, bx1, by1 in boxes)



def _win_edges(holes, axes, edge_off=0.0):
    """Оконные стойки — где разрешён БОКОВОЙ кляммер (Герман 30.07 п.4:
    только в пределах высоты окна). Берём ось у самой грани (ближе
    WIN_NEAR) и смещённые позиции грань∓edge_off вертикальной.
    В межэтажной ось руста не смещается (ТЗ §2); с 30.09b у каждой грани
    проёма там своя НСП вдоль откоса (грань∓edge_off) — она в cand."""
    out = []
    for bx0, by0, bx1, by1 in holes:
        cand = []
        left = [a for a in axes if a <= bx0 + EPS and bx0 - a < WIN_NEAR]
        right = [a for a in axes if a >= bx1 - EPS and a - bx1 < WIN_NEAR]
        if left:
            cand.append(max(left))
        if right:
            cand.append(min(right))
        if edge_off > EPS:
            cand += [bx0 - edge_off, bx1 + edge_off]
        for cx in cand:
            out.append((cx, by0, by1))
    return out


def near_pt(b, x, y, tol=1.0):
    return abs(b["x"] - x) < tol and abs(b["y"] - y) < tol


def _at_window(wedges, x, y):
    return any(abs(wx - x) < EPS and wy0 - EPS <= y <= wy1 + EPS
               for wx, wy0, wy1 in wedges)


_CL_RANK = {"боковой": 3, "стартовый": 2, "комбинированный": 1,
            "рядовой": 0}


def _merge_clamps(clamps, tol=CLAMP_MERGE):
    """Слияние ЗАДВОЕННЫХ кляммеров (Герман 30.07: «и стартовый стоит,
    и рядовой»). Причина найдена прогоном: верх откоса проёма лежит
    чуть НИЖЕ ближайшего шва раскладки — стартовый садится на кромку,
    рядовой на шов в нескольких см выше. Ближе tol по одной оси —
    один кляммер, приоритет боковой > стартовый > комбинированный >
    рядовой (координата приоритетного = низ ряда облицовки)."""
    by_x = {}
    for cl in clamps:
        by_x.setdefault(round(cl["x"], 1), []).append(cl)
    out = []
    for x in sorted(by_x):
        cur = []
        for cl in sorted(by_x[x], key=lambda c: c["y"]):
            if cur and cl["y"] - cur[-1]["y"] < tol - EPS:
                cur.append(cl)
                continue
            if cur:
                out.append(max(cur, key=lambda c:
                               _CL_RANK.get(c["kind"], 0)))
            cur = [cl]
        if cur:
            out.append(max(cur, key=lambda c: _CL_RANK.get(c["kind"], 0)))
    return out


def _cut_by_len(lo, hi, std, gap):
    """Резка направляющей по СТАНДАРТНОЙ ДЛИНЕ (Герман 30.07 п.11:
    профиль 3000, стык ПОСЕРЕДИНЕ между кронштейнами — при сетке 600
    середина попадает ровно на кратное 3000, свес остаётся 300)."""
    out = []
    n = 0
    while True:
        a = lo + n * std + (gap / 2.0 if n > 0 else 0.0)
        if hi - a <= EPS:
            break
        if lo + (n + 1) * std >= hi - EPS:      # последний кусок
            out.append((a, hi))
            break
        out.append((a, lo + (n + 1) * std - gap / 2.0))
        n += 1
    return out


def _piece_clamps(clamps, rows, s_lo, s_hi, s_x, side, fl_in, wedges=(),
                  on_seam=True):
    """Кляммеры куска стойки (ТЗ 26.07 + ответ Германа 07.08, В-аа):
    тип определяется положением ОСИ относительно ПЛИТ облицовки.

    - боковой (угловой КЛУ-1.1): оконные смещённые/Z стойки (side) и
      ЛЮБАЯ ось ВНЕ вертикального шва — середина плитки шире 600,
      стойка угловой/краевой зоны (on_seam=False). Установка
      ВЕРТИКАЛЬНО (orient="v"), «вдоль оконных боковых откосов и
      боковых границ замкнутой области»;
    - рядовой: ось НА вертикальном шве (руст, плиты с обеих сторон);
    - стартовый: низ обычного куска (низ зоны / над откосом);
    - комбинированный: первый шов выше стыка-термошва (fl_in) — только
      на рустах; на осях в поле плиты ряд над стыком по полигону
      Германа ПУСТ (столбики КЛУ с пропусками 1216).
    Держатели верхних кромок (под отливом / последний ряд) ставит
    вызывающий код — им нужен контекст проёмов и верха зоны."""
    if not rows:
        return
    k0 = "боковой" if side else "стартовый"
    if k0 == "стартовый" and _at_window(wedges, s_x, s_lo):
        k0 = "боковой"                     # п.4: кромка в пределах окна
    c0 = {"x": round(s_x, 4), "y": round(s_lo, 4), "kind": k0}
    if k0 == "боковой":
        c0["orient"] = "v"
    clamps.append(c0)
    for ry in rows:
        if not (s_lo + EPS < ry < s_hi - EPS):
            continue
        over_seam = any(
            f < ry and not any(f < r2 < ry for r2 in rows)
            for f in fl_in)
        if side:
            kind = "боковой"
        elif not on_seam:
            if over_seam:
                continue       # полигон: над стыком вне руста — пусто
            kind = "боковой"
        else:
            kind = "рядовой"
            if _at_window(wedges, s_x, ry):
                kind = "боковой"           # п.4: у грани окна, в высоту
            elif over_seam:
                kind = "комбинированный"
        cl = {"x": round(s_x, 4), "y": round(ry, 4), "kind": kind}
        if kind == "боковой":
            cl["orient"] = "v"
        clamps.append(cl)


def _edge_top_clamps(clamps, rows, s_lo, s_hi, s_x, y_top, hole_boxes):
    """Боковые кляммеры ГОРИЗОНТАЛЬНОЙ установки (Герман 07.08,
    ответ 1): «под отливом» (верх куска стойки упёрся в низ проёма)
    и «последним рядом по высоте» (верх куска = верх замкнутой
    области). На полигоне — КЛУ-1.1 с rot=0 на верхних кромках
    подоконных плит (орто: 4 X × 20 = 80) и верхнего ряда (27)."""
    if not rows:
        return
    at_top = abs(s_hi - y_top) <= 1.0
    at_sill = any(abs(s_hi - by0) <= 1.0 and
                  bx0 - EPS <= s_x <= bx1 + EPS
                  for bx0, by0, bx1, by1 in hole_boxes)
    if at_top or at_sill:
        clamps.append({"x": round(s_x, 4), "y": round(s_hi, 4),
                       "kind": "боковой", "orient": "h"})


def _poly_area(poly):
    ox, oy = poly[0]
    a = 0.0
    for i in range(len(poly)):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % len(poly)]
        a += (x1 - ox) * (y2 - oy) - (x2 - ox) * (y1 - oy)
    return a / 2.0


def _max_tributary(contours, joints, mid_over, sub):
    """Максимальная грузовая ширина стойки по ИТОГОВЫМ осям: оси раскладки
    в пределах контура + серединные стойки (вертикальная/ортогональная, как
    в ветках расстановки). Ширина стойки — полусумма соседних пролётов;
    крайняя берёт пролёт до соседа (кромка зоны — отдельная консоль, её
    правило задаёт система)."""
    best = 0.0
    for c in contours:
        outer = _closed(c.get("outer") or [])
        if len(outer) < 3:
            continue
        x0, _y0, x1, _y1 = _bbox(outer)
        ax = sorted(j for j in joints if x0 - EPS <= j <= x1 + EPS)
        if sub in ("vertical", "ortho") and mid_over and len(ax) >= 2:
            ax = sorted(ax + [(ax[i] + ax[i + 1]) / 2.0 for i in range(len(ax) - 1)
                              if ax[i + 1] - ax[i] > mid_over + EPS])
        for i in range(len(ax)):
            left = ax[i] - ax[i - 1] if i > 0 else None
            right = ax[i + 1] - ax[i] if i + 1 < len(ax) else None
            if left is not None and right is not None:
                w = 0.5 * (left + right)
            else:
                w = left if left is not None else (right or 0.0)
            best = max(best, w)
    return best


def _median_gap(vals, default):
    """Медиана интервалов сортированного списка (грузовая ширина из
    осей рустов / шаг перекрытий из отметок)."""
    if len(vals) < 2:
        return default
    iv = sorted(vals[i + 1] - vals[i] for i in range(len(vals) - 1))
    return iv[len(iv) // 2]


def _max_floor_span(contours, floors, floor_step):
    """Longest actual floor interval of the selected walls (mm).

    Include wall ends so the first/last partial storey cannot be hidden by
    a median. Floors of other selected facades outside this wall are ignored.
    This does not introduce a calculation model for cantilevers or connections.
    """
    longest = 0.0
    for c in contours or []:
        outer = _closed(c.get("outer") or [])
        if len(outer) < 3:
            continue
        _x0, y0, _x1, y1 = _bbox(outer)
        levels = [y for y in floors if y0 + EPS < y < y1 - EPS]
        if not floors and floor_step and floor_step > EPS:
            y = y0 + floor_step
            while y < y1 - EPS:
                levels.append(y)
                y += floor_step
        levels = sorted(set([y0] + levels + [y1]))
        longest = max(longest, max((b - a for a, b in zip(levels, levels[1:])), default=0.0))
    return longest


def _resolve_calc_scheme(calc_req, system, sub):
    """One geometry has one supported calculation scheme; a preset cannot change it."""
    scheme = calc_req.get("scheme") or (system.get("calc") or {}).get("scheme") or sub
    if sub not in ("vertical", "interfloor", "ortho") or scheme != sub:
        raise ValueError("Расчётная схема «%s» не соответствует подсистеме «%s». "
                         "Выберите согласованный пресет и схему. Тип 5 (interfloor_direct) "
                         "не реализован как самостоятельная геометрия." % (scheme, sub))
    return scheme


def _apply_calc(calc_req, system, sub, joints, floors, floor_step,
                corners=None, contours=None):
    """Подбор шагов кронштейнов расчётом frame_calc (этап 4).

    Возвращает (report, err, (step_main, step_corner)). Пресет
    системы (systems.json "calc") перекрывается полями calc_req;
    обязательные от конструктора: terrain, height, q_clad, offset,
    na_max, wind_region|w0.
    """
    import frame_calc
    p = dict(system.get("calc") or {})
    p.update({k: v for k, v in calc_req.items() if v is not None})
    for f in ("terrain", "height", "q_clad", "offset", "na_max"):
        if p.get(f) in (None, ""):
            return None, "расчёт: не задано поле «%s»" % f, None
    if not p.get("w0") and not p.get("wind_region"):
        return None, "расчёт: не задан ветровой район", None
    if not p.get("bracket") or not p.get("profile"):
        return None, ("расчёт: для системы нет расчётного пресета "
                      "(кронштейн/профиль) — systems.json calc"), None
    # Explicit invalid values are errors, never requests for an implicit default.
    for key in ("b", "b_corner", "rail_len", "max_step"):
        if p.get(key) is not None:
            try:
                value = float(p[key])
                if not math.isfinite(value) or value <= 0:
                    raise ValueError()
            except (TypeError, ValueError):
                return None, "расчёт: %s должно быть конечным числом больше нуля" % key, None
    b = float(p["b"] if p.get("b") is not None else _median_gap(joints, 600.0))
    rail_len = float(p["rail_len"] if p.get("rail_len") is not None else
                     (floor_step or _median_gap(floors, 3000.0)))
    if sub == "interfloor":
        # A shorter manually supplied value must not understate generated spans.
        rail_len = max(rail_len, _max_floor_span(contours, floors, floor_step))
    try:
        scheme = _resolve_calc_scheme(calc_req, system, sub)
    except ValueError as e:
        return None, str(e), None
    inp = dict(scheme=scheme, terrain=str(p["terrain"]),
               height=float(p["height"]), q_clad=float(p["q_clad"]),
               gamma_clad=float(p["gamma_clad"] if p.get("gamma_clad") is not None else 1.1),
               q_rails=float(p.get("q_rails") or 0.0),
               offset=float(p["offset"]), na_max=float(p["na_max"]),
               bracket=str(p["bracket"]),
               extender=p.get("extender") or None,
               profile=p["profile"], b_row=b,
               b_corner=float(p["b_corner"]) if p.get("b_corner") is not None else None,
               rail_len=rail_len,
               max_step=float(p["max_step"] if p.get("max_step") is not None else 800.0),
               n_rivets=p["n_rivets"] if p.get("n_rivets") is not None else 2,
               # п.2 (30.07): подбирать профиль вместе с шагом
               auto_profile=bool(p.get("auto_profile", True)),
               profile_candidates=p.get("profile_candidates"))
    # Dead load belongs to the emitted assembly, not just the weaker
    # section used for its strength check. Keep project loads above these
    # known minima. The horizontal member remains included when candidates
    # change; orthogonal output contains ШП even though Z bounds strength.
    try:
        extra = frame_calc._number("q_rails_extra", p.get("q_rails_extra", 0), zero=True)
        vertical_min = frame_calc._number("q_rails_profile_min", p.get("q_rails_profile_min", 0), zero=True)
        if sub == "interfloor":
            extra = max(extra, frame_calc.PROFILES["НГП-60-50-1,5"]["q"])
        elif sub == "ortho":
            extra = max(extra, frame_calc.PROFILES["ГП-40-40-1,2"]["q"])
            vertical_min = max(vertical_min, frame_calc.PROFILES["ШП-60-20-1,2"]["q"])
        inp["q_rails_extra"] = extra
        inp["q_rails_profile_min"] = vertical_min
    except ValueError as e:
        return None, "расчёт: %s" % e, None
    # В-ш (ЗАКРЫТ письмом Германа 01.08): допустимые сечения
    # ВЕРТИКАЛЬНЫХ направляющих по типу системы — подбор больше не
    # гуляет по всему справочнику (НСП/НШП в вертикальной).
    _VERT_ALLOWED = {
        "vertical": ["ГП-40-40-1,2", "ШП-60-20-1,2",
                     "ШП-60-20-20-1,2"],
        "ortho": ["ШП-60-20-1,2", "ШП-60-20-20-1,2",
                  "ЗП-40-20-1,2"],
    }
    if inp["auto_profile"] and not inp.get("profile_candidates"):
        inp["profile_candidates"] = _VERT_ALLOWED.get(scheme)
    bc_note = None
    if not inp["b_corner"]:
        if scheme == "vertical":
            inp["b_corner"] = b
        else:
            bc = None
            czone = float(system.get("corner_zone") or 1500.0)
            if corners:
                cj = [j for j in joints
                      if any(abs(j - c) <= czone for c in corners)]
                if len(cj) >= 3:
                    iv = sorted(cj[i + 1] - cj[i]
                                for i in range(len(cj) - 1))
                    bc = iv[len(iv) // 2]
            if bc:
                inp["b_corner"] = bc
                bc_note = ("шаг направляющих в угловой зоне взят из "
                           "осей раскладки: %.0f" % bc)
            else:
                inp["b_corner"] = min(b, 450.0)
                bc_note = ("шаг направляющих в угловой зоне принят "
                           "%.0f (осей в угловой полосе нет) — "
                           "проверьте по раскладке"
                           % inp["b_corner"])
    if p.get("wind_region"):
        inp["wind_region"] = str(p["wind_region"])
    if p.get("w0"):
        inp["w0"] = float(p["w0"])
    if p.get("ice_region"):
        inp["ice_region"] = str(p["ice_region"])
    for k in ("e1", "e2", "e3", "e4", "ry"):
        if p.get(k) is not None:
            inp[k] = float(p[k])
    if scheme == "ortho":
        inp["v_step"] = float(p.get("v_step") or
                              system.get("ortho_v_step") or 600.0)
    try:
        rep = frame_calc.report(inp)
        # A single vertical mark is emitted for the facade. If independent
        # optimisation chose different row/corner sections, select one section
        # that passes both zones rather than drawing only the row winner.
        if sub == "vertical" and inp["auto_profile"] and \
                rep["row"].get("profile") != rep["corner"].get("profile"):
            common = []
            for profile in inp.get("profile_candidates") or []:
                trial = dict(inp, profile=profile, auto_profile=False)
                both = frame_calc.report(trial)
                if all(both[z].get("step") for z in ("row", "corner")):
                    mass = sum(frame_calc.PROFILES[profile]["q"] / (inp["b_" + z] / 1000.0) +
                               frame_calc.M_BRACKET / ((inp["b_" + z] / 1000.0) *
                                                      both[z]["step"] / 1000.0)
                               for z in ("row", "corner"))
                    common.append((mass, profile, both))
            if not common:
                return None, ("расчёт: нет единого профиля, проходящего в рядовой и угловой зонах; "
                              "уточните сечение или исходные нагрузки"), None
            _mass, profile, rep = min(common, key=lambda v: (v[0], v[1]))
            inp["profile"] = profile
            inp["auto_profile"] = False
    except (KeyError, TypeError, ValueError) as e:
        return None, "расчёт: %s" % e, None
    for zone, name in (("row", "рядовой"), ("corner", "угловой")):
        if not rep[zone]["step"]:
            fails = []
            tried = rep[zone].get("tried") or []
            if tried:
                # падающие проверки на минимальном кандидате
                last = frame_calc.calc_chain(inp, tried[0][0],
                                             zone)
                fails = [c["name"] for c in last["checks"]
                         if not c["ok"]]
            hint = ("уменьшите шаг направляющих (b_corner) или "
                    "усильте профиль" if any("профиль" in f
                                             for f in fails)
                    else "усильте систему (кронштейн/профиль/анкер)")
            return None, ("расчёт: в %s зоне ни один шаг до %.0f мм "
                          "не проходит (%s) — %s"
                          % (name, inp["max_step"],
                             ", ".join(fails) or "все проверки",
                             hint)), None
    out = {"row": rep["row"]["chain"], "corner": rep["corner"]["chain"],
           "steps": {"main": rep["row"]["step"],
                     "corner": rep["corner"]["step"]},
           "scheme": scheme,
           "inputs": frame_calc.resolved_inputs(dict(inp, profile=rep["row"]["profile"])),
           # п.2 (30.07): подобранный профиль, ограничивающий узел и
           # таблица вариантов — чтобы конструктор видел, ЧТО режет шаг
           # (на боевых числах это анкер, а не сечение профиля)
           "profile": {"row": rep["row"].get("profile"),
                       "corner": rep["corner"].get("profile")},
           "binding": {"row": rep["row"].get("binding"),
                       "corner": rep["corner"].get("binding")},
           "variants": rep["row"].get("variants"),
           "bc_note": bc_note}
    return out, None, (float(rep["row"]["step"]),
                       float(rep["corner"]["step"]))



def _verify_calc_spacing(report, req, sub, rails, hrails, brackets, corners, corner_zone):
    """Recheck emitted adjacent support intervals within each continuous member.

    This verifies the spacing/longest-storey contract of the existing chain;
    it is not a new analysis of cantilevers, connection nodes or horizontal beams.
    """
    import frame_calc
    intervals = {"row": [], "corner": []}
    if sub == "vertical":
        # Геометрический индекс уже построен по каждому куску. Не сканируем
        # все кронштейны заново для каждой направляющей и не применяем общую
        # многопролётную строку вместо только что проверенной схемы куска.
        for member in report["static_model"]["members"]:
            intervals[member["zone"]].extend(member["intervals"])
    bounds = [_bbox(_closed(c.get("outer") or []))
              for c in req.get("contours") or [] if len(c.get("outer") or []) >= 3] if sub != "vertical" else []
    members = [] if sub == "vertical" else [h for h in hrails if h["kind"] not in SHINA_KINDS]
    for member in members:
        points = sorted(set(b["x"] for b in brackets if abs(b["y"] - member["y"]) <= 0.5
                            and member["x0"] - 0.5 <= b["x"] <= member["x1"] + 0.5))
        for a, b in zip(points, points[1:]):
            x, y = ((member["x"], (a + b) / 2.0) if sub == "vertical" else
                    ((a + b) / 2.0, member["y"]))
            boxes = [box for box in bounds if box[0] - 0.5 <= x <= box[2] + 0.5 and
                     box[1] - 0.5 <= y <= box[3] + 0.5]
            corner = any(_in_corner(x, box[0], box[2], corner_zone, corners) for box in boxes)
            intervals["corner" if corner else "row"].append(b - a)
    checked = {}
    for zone in ("row", "corner"):
        if not intervals[zone]:
            checked[zone] = {"intervals": 0, "max_step": None}
            continue
        step = max(intervals[zone])
        limit = report["steps"]["corner" if zone == "corner" else "main"]
        chain = None
        if sub != "vertical":
            inp = dict(report["inputs"], profile=report["profile"][zone])
            chain = frame_calc.calc_chain(inp, step, zone)
        if step > limit + 0.5 or (chain is not None and not chain["passed"]):
            return None, ("Расчёт выданной геометрии не проходит: %s зона, фактический шаг %.1f мм "
                          "(расчётный предел %.1f мм). Подсистема не выдана; прежняя сохранена." %
                          ("угловая" if zone == "corner" else "рядовая", step, limit))
        checked[zone] = {"intervals": len(intervals[zone]), "max_step": round(step, 4)}
        if chain is not None:
            checked[zone]["chain"] = chain
    return {"passed": True, "scope": "Интервалы между соседними кронштейнами на непрерывных элементах; "
            "для межэтажной цепочки — максимальный фактический интервал отметок/границ стены.",
            "rail_len": report["inputs"]["rail_len"], "zones": checked}, None


def _clamps_on_rails(req, sub, system, joints, rows, floors, seam_tol=2.0):
    """ТОЛЬКО КЛЯММЕРЫ по СУЩЕСТВУЮЩИМ направляющим и ТЕКУЩЕЙ облицовке
    (Герман 23.09: «разложить только кляммеры по существующей облицовке»).

    rails_fixed — куски вертикальных направляющих с чертежа {x, y0, y1}
    (прошлый ATFRAME или ручные). Правила — те же, что при полной
    расстановке (_piece_clamps / _edge_top_clamps / _merge_clamps):
    куски на одной оси с зазором ≤ 50 мм — одна стойка, стыки внутри —
    «термошвы» (комбинированный на первом шве выше); стойка на смещённой
    позиции у грани окна (грань ∓ edge_offset, в пределах высоты окна ±
    выступ) — оконная (все кляммеры боковые); тип на оси — по осям швов
    ТЕКУЩЕЙ раскладки (joints_x): на шве — рядовой, в поле плиты — боковой."""
    notes = []
    edge_off = float(system.get("edge_offset") or 0.0)
    overhang = float(system.get("edge_overhang") or 0.0)
    fixed = []
    for r in req.get("rails_fixed") or []:
        try:
            x, a, b = float(r["x"]), float(r["y0"]), float(r["y1"])
        except (KeyError, TypeError, ValueError):
            continue
        if abs(b - a) > EPS:
            role = str(r.get("clamp_role") or "")
            if role not in ("", "regular", "window", "flank"):
                return {"ok": False, "error": "неизвестная роль направляющей — перестройте подсистему"}
            fixed.append((round(x, 4), min(a, b), max(a, b), role))
    if not fixed:
        return {"ok": False, "error": "нет направляющих для кляммеров (rails_fixed пуст)"}
    clamps, used = [], set()
    for ci, c in enumerate(req.get("contours") or []):
        outer = _closed(c.get("outer") or [])
        if len(outer) < 3:
            continue
        holes = [_closed(h) for h in (c.get("holes") or [])]
        x0, y0, x1, y1 = _bbox(outer)
        hole_boxes = [_bbox(h) for h in holes]
        # 24.09 (рецензия): существующую направляющую — по КОНТУРУ стены и
        # мимо проёмов, а не по габариту (ручная стойка через «пустоту»
        # Г-стены давала кляммеры вне стены)
        mine = []
        for x, a, b, role in fixed:
            if not (x0 - EPS <= x <= x1 + EPS and a < y1 - EPS and b > y0 + EPS):
                continue
            spans = [(c0, c1) for c0, c1, _xe in _clip_pieces(outer, [(a, b, x)])]
            for bx0, by0, bx1, by1 in hole_boxes:
                if bx0 + EPS < x < bx1 - EPS:
                    spans = _sub_y(spans, by0, by1)
            mine.extend((x, c0, c1, role) for c0, c1 in spans if c1 - c0 > EPS)
        mine.sort()
        if not mine:
            continue
        used.update((x, a) for x, a, _b, _role in mine)
        # межэтажная без отметок: перекрытия шагом этажа от низа контура —
        # ровно как при полной расстановке (кляммер над стыком — комбинированный)
        floors_c = floors
        if not floors and sub == "interfloor":
            try:
                fs = float(req.get("floor_step") or 0.0)
            except (TypeError, ValueError):
                fs = 0.0
            if fs > EPS:
                floors_c, f = [], y0 + fs
                while f < y1 - EPS:
                    floors_c.append(f)
                    f += fs
        zj = [j for j in joints if x0 - EPS <= j <= x1 + EPS]
        wedges = _win_edges(hole_boxes, zj or sorted({x for x, _a, _b, _role in mine}), edge_off)
        # стойки: куски одной оси с малым зазором
        stands = []
        for x, a, b, role in mine:
            if stands and abs(stands[-1][0] - x) <= EPS and a - stands[-1][2] <= 50.0 + EPS and \
                    role == stands[-1][4]:
                st = stands[-1]
                # стык: межэтажная и с отметками — центр зазора (= отметка),
                # хлысты/ортогональная — верх нижнего куска (как движок)
                st[3].append(0.5 * (st[2] + a)
                             if (sub == "interfloor" or (floors_c and sub == "vertical"))
                             else st[2])
                st[2] = max(st[2], b)
            else:
                stands.append([x, a, b, [], role])
        # хлыст, не дошедший до ВЕРХА стены на зазор стыка (остаток ≤ зазора
        # хлыстом не закрывается) — стойка, как при расстановке, до верха
        tail = float(system.get("rail_gap") or 0.0) + 1.0
        for x, a, b, seams, role in stands:
            # Determine the window role BEFORE extending a small stock tail to
            # the wall top: that extension used to turn a window profile into
            # a regular one (F16, including vertical systems).
            side = role == "window"
            if not role and sub != "interfloor" and edge_off > EPS:
                for bx0, by0, bx1, by1 in hole_boxes:
                    if (abs(x - (bx0 - edge_off)) <= 0.5 or abs(x - (bx1 + edge_off)) <= 0.5) and \
                            a >= by0 - overhang - 1.0 and b <= by1 + overhang + 1.0:
                        side = True
                        break
            if not side:
                yt = _ytop(outer, x, b, y1)
                if 0.0 < yt - b <= tail:
                    b = yt
            # отметки перекрытий — стыки для вертикальной и межэтажной; у
            # ортогональной комбинированный только над стыками кусков
            fl_in = seams + [f for f in (floors_c if sub != "ortho" else [])
                             if a + EPS < f < b - EPS and
                             not any(abs(f - q) <= 50.0 for q in seams)]
            n0c = len(clamps)
            _piece_clamps(clamps, rows, a, b, x, side, sorted(fl_in), wedges,
                          on_seam=any(abs(x - j) <= seam_tol for j in joints))
            # 30.09b: у межэтажной НСП вдоль откоса низ — отметка перекрытия (облицовка идёт ниже):
            # стартовый — только у настоящего низа (под куском нет стены или там проём)
            if sub == "interfloor" and len(clamps) > n0c and clamps[n0c]["kind"] == "стартовый" and \
                    _inside_pt(outer, x, a - 30.0, tol=0.0) and not _in_boxes(hole_boxes, x, a - 30.0):
                del clamps[n0c]
            if not side or sub == "interfloor":
                _edge_top_clamps(clamps, rows, a, b, x, _ytop(outer, x, b, y1), hole_boxes)
    clamps = _merge_clamps(clamps)
    skipped = len(fixed) - len({(x, a) for x, a, _b, _role in fixed if (x, a) in used or
                                any(abs(x - ux) <= EPS and a <= ua + EPS for ux, ua in used)})
    notes.append("кляммеры по существующим направляющим: %d кусков, кляммеров %d"
                 % (len(fixed), len(clamps)))
    if skipped > 0:
        notes.append("направляющих вне выбранных зон: %d — без кляммеров" % skipped)
    if not rows:
        notes.append("нет горизонтальных швов (rows_y) — только стартовые кляммеры")
    return {"ok": True, "rails": [], "hrails": [], "brackets": [], "fittings": [],
            "clamps": clamps, "notes": notes,
            "system_used": {k: v for k, v in system.items() if not k.startswith("_src")},
            "summary": {"system": system.get("_name", "?"), "sub_type": sub, "parts": "clamps",
                        "rails": 0, "rails_lm": 0.0, "hrails": 0, "hrails_lm": 0.0,
                        "fittings": 0, "rail_stock_est": None,
                        "brackets_main": 0, "brackets_row": 0,
                        "clamps_start": sum(1 for q in clamps if q["kind"] == "стартовый"),
                        "clamps_row": sum(1 for q in clamps if q["kind"] == "рядовой"),
                        "clamps_side": sum(1 for q in clamps if q["kind"] == "боковой"),
                        "clamps_combo": sum(1 for q in clamps if q["kind"] == "комбинированный")}}


def shina_summary(hrails):
    """Счёт шин по видам (куски = хлысты) и горизонтальных направляющих
    отдельно — у ортогональной под плиткой в hrails лежат и ГП, и шины."""
    out = {}
    for key, kind in (("shina_start", "шина стартовая"), ("shina_row", "шина рядовая"),
                      ("shina_end", "шина концевая")):
        hs = [h for h in hrails if h["kind"] == kind]
        out[key] = len(hs)
        out[key + "_lm"] = round(sum(h["len"] for h in hs) / 1000.0, 2)
    sh = [h for h in hrails if h["kind"] in SHINA_KINDS]
    gd = [h for h in hrails if h["kind"] not in SHINA_KINDS]
    out["shina_pieces"] = len(sh)
    out["shina_lm"] = round(sum(h["len"] for h in sh) / 1000.0, 2)
    out["hguides"] = len(gd)
    out["hguides_lm"] = round(sum(h["len"] for h in gd) / 1000.0, 2)
    return out


def frame_plan(req, prepared_solution=_UNPREPARED, include_solution_report=True):
    """Validate source identity before geometry; retain scope for every mode."""
    if prepared_solution is _UNPREPARED:
        prepared_solution, error = prepare_solution(req)
        if error is not None:
            return error
    sub = (req.get("sub_type") or "vertical").strip().lower()
    resolved = resolve_layout_contract(req, {}, sub)
    if not resolved["ok"]:
        return resolved
    out = _frame_plan(req)
    if out.get("ok"):
        scope = declared_scope(resolved["contract"], any(
            c.get("holes") for c in req.get("contours") or []))
        out["design_scope"] = scope["metadata"]
        out.setdefault("notes", []).extend(scope["notes"])
        model = (out.get("calc_report") or {}).get("static_model") or {}
        out["connection_passport"] = build_connection_passport(req, out,
            prepared_members=model.get("members"))
        if prepared_solution is not None and include_solution_report:
            out["solution_report"] = prepared_solution.report()
            out.setdefault("notes", []).extend(prepared_solution.notes())
    return out


def _frame_plan(req):
    try:
        system = load_system(req.get("system") or "Standart",
                             req.get("_systems_dir"))
    except (KeyError, OSError, ValueError) as e:
        return {"ok": False, "error": str(e)}
    # 04.08 (Герман п.1): шаг, ЗАДАННЫЙ РУКАМИ в диалоге, ставится
    # буквально (без «размазывания» В16) — C# шлёт exact_step=true
    exact_step = bool(req.get("exact_step"))
    step_main = system.get("bracket_step")
    step_corner = system.get("bracket_step_corner")
    start_off = system.get("bracket_start_offset")
    corner_zone = float(system.get("corner_zone") or 0.0)
    # 04.08 (Герман п.2): горизонтальный шаг СТОЕК в угловой зоне
    # и отступ первой/последней стойки от угла и края области
    rail_step_corner = req.get("rail_step_corner") or \
        system.get("rail_step_corner")
    rail_step_corner = float(rail_step_corner) if rail_step_corner \
        else None
    rail_step_main = req.get("rail_step_main") or \
        system.get("rail_step_main")
    rail_step_main = float(rail_step_main) if rail_step_main else None
    edge_rail_off = float(req.get("edge_rail_off") or
                          system.get("edge_rail_off") or 100.0)
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
    # Герман 07.08 (ответ 2, В-ад ЗАКРЫТ): обе схемы верны. Точки
    # перекрытий УКАЗАНЫ → стык направляющих центрован на отметке,
    # несущий кронштейн на центре перекрытия (В15). НЕ указаны →
    # «установка идёт снизу вверх с заданным шагом»: хлысты rail_std
    # от низа зоны с зазором, кронштейны «300 от торцов + ≤ шага»,
    # ВСЕ ОДНОГО ТИПА («у меня разложено для случая, когда везде
    # монолит»). Прежняя автогенерация виртуальных отметок шагом
    # floor_step (26.07) остаётся ТОЛЬКО у межэтажной — ей отметки
    # нужны конструктивно.
    lash = not floors
    corners = None
    if req.get("corners_x") is not None:
        corners = []
        for x in req.get("corners_x") or []:
            try:
                corners.append(float(x))
            except (TypeError, ValueError):
                continue
        corners = sorted(set(corners))
    rows = []
    for y in req.get("rows_y") or []:
        try:
            rows.append(float(y))
        except (TypeError, ValueError):
            continue
    rows = sorted(set(rows))
    # снапшот ОСЕЙ-РУСТОВ до всех добавок (mid/угловые/краевые):
    # тип кляммера на оси зависит от того, руст это или ось в поле
    # плиты (В-аа, ответ Германа 07.08)
    seam_axes = list(joints)

    def _on_seam(jx, tol=2.0):
        return any(abs(jx - j0) <= tol for j0 in seam_axes)

    # тип подсистемы (ТЗ Германа 26.07): вертикальная (дефолт) /
    # межэтажная / ортогональная; комбинации — разными запусками
    sub = (req.get("sub_type") or "vertical").strip().lower()
    min_corner = float(system.get("min_from_corner") or 150.0)
    ortho_v = float(system.get("ortho_v_step") or 600.0)
    ortho_oh = float(system.get("ortho_max_overhang") or 300.0)
    rail_std = float(system.get("rail_std") or 3000.0)
    edge_rail = float(system.get("edge_rail_off") or 100.0)

    notes, rails, brackets, clamps = [], [], [], []
    rebalanced_cuts = 0
    hrails, fittings = [], []
    # 26.09 (Денис): облицовка. Керамогранит/композит — как было (стойки по
    # швам раскладки, кляммеры). Бетонная/клинкерная плитка — подсистема БЕЗ
    # привязки к вертикальным швам: вертикальные направляющие заданным шагом
    # по горизонтали (tile_step_x), на них — горизонтальные ШИНЫ, на которые
    # вешается облицовка. Кляммеров у плитки нет.
    # 29.09c (Герман, ответ по №27): у плитки ТЕ ЖЕ три типа подсистемы
    # (вертикальная / межэтажная / ортогональная) — «разница только в том»,
    # что вертикальные направляющие заданным шагом по горизонтали; кронштейны
    # — по расчёту (или вручную), как у остальных облицовок; углы здания —
    # как у всех (шаг угловой зоны — из расчёта; шаг направляющих в угловой
    # зоне — tile_step_x_corner, 0 — как рядовой). Шины трёх видов (стартовая
    # по низу, рядовые по центрам швов, концевая по верху), хлысты tile_whip
    # (2500) от левого края прогона; марка шины — tile_rail_brand (от проекта).
    cladding = str(req.get("cladding") or "porcelain").strip().lower()
    if cladding not in ("porcelain", "composite", "concrete", "clinker"):
        return {"ok": False,
                "error": "cladding: porcelain|composite|concrete|clinker "
                         "(получено %r)" % cladding}
    tile = cladding in ("concrete", "clinker")
    tile_step, tile_rows, tile_row_step = 0.0, [], 0.0
    tile_step_c, tile_whip, tile_brand = 0.0, 2500.0, ""
    if tile:
        def _fnum(key, dflt):
            try:
                v = req.get(key)
                return float(v) if v not in (None, "") else dflt
            except (TypeError, ValueError):
                return dflt
        tile_step = _fnum("tile_step_x", 0.0)
        if tile_step < 100.0 - EPS:
            return {"ok": False,
                    "error": "плитка: шаг вертикальных направляющих по горизонтали "
                             "(tile_step_x) — не меньше 100 мм"}
        tile_step_c = _fnum("tile_step_x_corner", 0.0)
        if EPS < tile_step_c < 100.0 - EPS:
            return {"ok": False,
                    "error": "плитка: шаг направляющих в угловой зоне "
                             "(tile_step_x_corner) — 0 (как рядовой) или от 100 мм"}
        tile_whip = _fnum("tile_whip", 2500.0)
        if tile_whip < 300.0 - EPS:
            return {"ok": False,
                    "error": "плитка: длина хлыста шины (tile_whip) — не меньше 300 мм"}
        tile_brand = str(req.get("tile_rail_brand") or "").strip()
        tile_row_step = _fnum("tile_row_step", 0.0)
        tile_rows = list(rows)
        rows = []            # кляммеров у плитки нет — ряды идут в шины
        mid_over = None      # стойки ровно шагом, без серединных
    # 23.09b (Герман): что раскладывать — all | frame (без кляммеров) |
    # clamps (только кляммеры; по rails_fixed — существующим направляющим)
    parts = str(req.get("parts") or "all").strip().lower()
    if parts not in ("all", "frame", "clamps"):
        return {"ok": False, "error": "parts: all|frame|clamps (получено %r)" % parts}
    if cladding == "composite" and parts != "frame":
        return {"ok": False, "error_code": "E_COMPOSITE_FASTENERS_UNSUPPORTED",
                "error": "Крепления композитных кассет не реализованы. Выберите «Только подсистема»; "
                         "кляммеры керамогранита для композита не применяются."}
    if tile and parts == "clamps":
        return {"ok": False, "error": "у бетонной/клинкерной плитки кляммеров нет — "
                                      "«только кляммеры» не применяется"}
    if parts == "clamps" and req.get("rails_fixed") is not None:
        return _clamps_on_rails(req, sub, system, joints, rows, floors)
    if parts == "frame":
        rows = []
    if not joints and not tile:
        return {"ok": False,
                "error": "нет осей стоек (joints_x) — раскладка "
                         "ATCLAD не выбрана и оси не заданы"}

    # ── ЭТАП 4: шаги ПО РАСЧЁТУ (frame_calc), а не из справочника.
    # req["calc"] = исходные от конструктора (район/местность/высота/
    # вес облицовки/вынос/анкер); пресет системы systems.json["calc"]
    # даёт кронштейн/удлинитель/профиль/вес направляющих. Целевой
    # порядок Дениса 24.07: расчёт → шаги → расстановка.
    calc_rep = None
    # 01.08 (письмо Германа, п.2): явный выбор профиля вертикальной
    # направляющей сужает подбор до подтверждённого сечения.
    # ГП-60-40 без Wx/Jx/A/q не заменяется сечением ГП-40-40:
    # меньшая масса такого заместителя не является расчётом «в запас».
    _USER_PROF = {
        "ГП-40-40": ["ГП-40-40-1,2"],
        "ШП-60-20": ["ШП-60-20-1,2", "ШП-60-20-20-1,2"],
        "ГП-40-40-1,2": ["ГП-40-40-1,2"],
        "ШП-60-20-1,2": ["ШП-60-20-1,2"],
        "ШП-60-20-20-1,2": ["ШП-60-20-20-1,2"],
    }
    rail_prof_req = (str(req.get("rail_profile") or "").strip()
                     or None)
    calc_in = req.get("calc")
    if calc_in is not None:
        import frame_calc
        calc_in = dict(calc_in or {})
        try:
            _resolve_calc_scheme(calc_in, system, sub)
        except ValueError as e:
            return {"ok": False, "error_code": "E_CALC_SCHEME_MISMATCH", "error": str(e)}
        if sub == "vertical":
            allowed = {"ГП-40-40-1,2", "ШП-60-20-1,2", "ШП-60-20-20-1,2"}
            candidates = calc_in.get("profile_candidates")
            explicit = calc_in.get("profile")
            unknown_mark = rail_prof_req and rail_prof_req not in _USER_PROF
            bad_candidates = candidates is not None and (not isinstance(candidates, (list, tuple)) or
                              not candidates or any(not isinstance(c, str) or c not in allowed for c in candidates))
            bad_profile = explicit is not None and (not isinstance(explicit, str) or explicit not in allowed)
            if unknown_mark or bad_candidates or bad_profile:
                return {"ok": False, "error_code": "E_PROFILE_SECTION_UNCONFIRMED",
                        "error": "Для выбранной марки вертикального профиля нет подтверждённых "
                                 "характеристик сечения Wx/Jx/A и массы q в этой схеме. "
                                 "Выберите ГП-40-40/ШП-60-20 или ручной режим по отдельному инженерному расчёту."}
        calc_in.setdefault("gamma_clad", frame_calc.cladding_gamma(cladding))
        if sub == "interfloor":
            if str(req.get("nsp_type") or "НСП-1") != "НСП-1":
                return {"ok": False, "error_code": "E_NSP_SECTION_UNCONFIRMED",
                        "error": "Для расчёта НСП-2 нет подтверждённого сечения. Выберите НСП-1 "
                                 "или ручной режим по проверенному инженерному расчёту."}
            # METHOD_CALC: NSP-1 = НСП-69-60. The emitted mark must be the
            # same section whose strength the calculation checks.
            calc_in["profile"] = "НСП-69-60-1,2"
            calc_in["profile_candidates"] = ["НСП-69-60-1,2"]
            calc_in["auto_profile"] = False
        elif sub == "ortho":
            # Both ШП-60-20 and window ZП-40-20 are emitted. Z has the smaller
            # Wx, Jx and A, so checking it bounds both actual vertical sections.
            calc_in["profile"] = "ЗП-40-20-1,2"
            calc_in["profile_candidates"] = ["ЗП-40-20-1,2"]
            calc_in["auto_profile"] = False
    if tile and calc_in is not None:
        # 29.09c (Герман): у плитки кронштейны — по расчёту. Грузовая ширина
        # направляющей = заданный шаг по горизонтали (оси ровные, крайний
        # пролёт короче), в угловой зоне — свой шаг, если задан
        calc_in = dict(calc_in or {})
        if calc_in.get("b") is None:
            calc_in["b"] = tile_step
        if calc_in.get("b_corner") is None:
            calc_in["b_corner"] = tile_step_c if tile_step_c > EPS else tile_step
    if sub == "vertical" and rail_prof_req and calc_in is not None:
        cands = _USER_PROF.get(rail_prof_req)
        if cands:
            calc_in = dict(calc_in or {})
            calc_in["profile_candidates"] = cands
            if not calc_in.get("auto_profile", True):
                calc_in["profile"] = cands[0]
    if calc_in is not None and not tile:
        # 24.09 (независимая рецензия): грузовая ширина бралась медианой
        # ИСХОДНЫХ осей раскладки, а стойки потом достраивались (серединные
        # при пролёте > mid_rail_over). Оси 100/200/300/400/1600/2800 →
        # медиана 100, а у достроенной стойки 2200 фактически 600 мм —
        # «всё проходит», хотя кронштейн по той же формуле 3207 > 2250.
        # Теперь b — НЕ МЕНЬШЕ максимальной фактической ширины по итоговым
        # осям каждого контура (медиана остаётся нижней границей: в запас).
        b_fact = _max_tributary(req.get("contours") or [], joints, mid_over, sub)
        if b_fact:
            calc_in = dict(calc_in or {})
            b_med = _median_gap(joints, 600.0)
            if calc_in.get("b") is None and b_fact > b_med + 0.5:
                calc_in["b"] = b_fact
                notes.append("грузовая ширина для расчёта %.0f мм — максимальная по "
                             "итоговым осям стоек (медиана исходных осей %.0f)"
                             % (b_fact, b_med))
    if calc_in is not None:
        calc_rep, cerr, csteps = _apply_calc(
            calc_in or {}, system, sub, joints, floors,
            floor_step, corners, req.get("contours"))
        if cerr:
            return {"ok": False, "error_code": "E_CALC_NOT_PASSED", "error": cerr}
        step_main, step_corner = csteps
        notes.append(
            "шаги кронштейнов ПО РАСЧЁТУ: рядовая %.0f / угловая %.0f "
            "(W_p=%.1f/%.1f кг/м²)"
            % (step_main, step_corner, calc_rep["row"]["w_p"],
               calc_rep["corner"]["w_p"]))
        if calc_rep.get("bc_note"):
            notes.append(calc_rep["bc_note"])

    rail_from = []       # 29.09l: (контур, индекс его первой стойки в rails) — для стыков шин
    tile_geo = {}        # 29.09q: контур → (стена, проёмы) — для удлинения шин без направляющей
    for ci, c in enumerate(req.get("contours") or []):
        rail_from.append((ci, len(rails)))
        outer = _closed(c.get("outer") or [])
        if len(outer) < 3:
            # 24.09 (рецензия): треугольный фасад (≥3 вершины) раньше
            # отбрасывался «меньше 4 вершин» с ok=true и пустым ответом
            notes.append("контур %d: меньше 3 вершин — пропуск"
                         % (ci + 1))
            continue
        holes = [_closed(h) for h in (c.get("holes") or [])]
        x0, y0, x1, y1 = _bbox(outer)
        hole_boxes = [_bbox(h) for h in holes]
        # 24.09 (рецензия): проёмы везде — по габариту; у непрямоугольного
        # (трапеция, треугольник, скос) подсистема обходит весь габарит
        for hi, h in enumerate(holes):
            hb = _bbox(h)
            ha = abs(_poly_area(h))
            if ha < (hb[2] - hb[0]) * (hb[3] - hb[1]) * (1.0 - 1e-6):
                notes.append("проём %d не прямоугольный — подсистема обходит его по "
                             "габариту %.0f×%.0f" % (hi + 1, hb[2] - hb[0], hb[3] - hb[1]))
        tile_hr, tile_notes = [], []
        if tile:
            # вертикальные направляющие — заданным шагом от края зоны (оси
            # раскладки не нужны), в угловых зонах — свой шаг; у окон ветки
            # типов сами ставят направляющие у граней, как у керамогранита
            joints = _tile_axes(x0, x1, tile_step, edge_rail, corners=corners,
                                czone=corner_zone, step_corner=tile_step_c)
            rows_c = [r for r in tile_rows if y0 + EPS < r < y1 - EPS]
            if not rows_c and tile_row_step >= 50.0:
                # раскладки у зоны нет — ряды шагом швов от низа зоны
                k = 1
                while y0 + k * tile_row_step < y1 - EPS:
                    rows_c.append(y0 + k * tile_row_step)
                    k += 1
            tile_hr, _sloped = _tile_rails(outer, hole_boxes, rows_c)
            for h in tile_hr:
                h["_c"] = ci         # хлысты режутся после расстановки (стык — на направляющей)
            tile_geo[ci] = (outer, hole_boxes)
            def _rl(kind):
                hs = [h for h in tile_hr if h["kind"] == kind]
                return len(set(h["run"] for h in hs)), sum(h["len"] for h in hs) / 1000.0
            (rs, ls), (rr, lr), (re_, le) = [_rl(k2) for k2 in SHINA_KINDS]
            tile_notes.append(
                "контур %d: плитка — вертикальных направляющих %d шагом %.0f мм от края "
                "зоны (не по швам)%s; шины: стартовых прогонов %d (%.1f м), рядовых %d "
                "(%.1f м), концевых %d (%.1f м)" %
                (ci + 1, len(joints), tile_step,
                 (", в угловых зонах %.0f" % tile_step_c) if tile_step_c > EPS else "",
                 rs, ls, rr, lr, re_, le))
            # 29.09l (Герман, 9д): концевая по наклону фронтона не нужна — нота о длине
            # наклона снята (вопрос закрыт)
            if not rows_c:
                tile_notes.append("контур %d: у плитки нет рядов (нет раскладки ATTILE и шага "
                                  "швов) — только стартовая и концевая шины" % (ci + 1))
        wedges = _win_edges(hole_boxes,
                            [j for j in joints if x0 - EPS <= j <= x1 + EPS],
                            edge_off)
        # отметки перекрытий: заданные, либо (ТОЛЬКО межэтажная —
        # ей отметки нужны конструктивно) автогенерация шагом этажа
        # от низа контура (26.07). Вертикальная без отметок работает
        # ХЛЫСТАМИ от низа (Герман 07.08, ответ 2).
        floors_c = floors
        if not floors and floor_step and sub == "interfloor":
            floors_c = []
            f = y0 + floor_step
            while f < y1 - EPS:
                floors_c.append(f)
                f += floor_step
            if floors_c:
                notes.append(
                    "контур %d: перекрытия автоматически шагом %.0f "
                    "(%d шт)" % (ci + 1, floor_step, len(floors_c)))

        if tile and not (sub == "interfloor" and not floors_c):
            # межэтажная без отметок пропускает контур целиком (нота ниже) —
            # шины без направляющих не выдаём
            hrails.extend(tile_hr)
            notes.extend(tile_notes)

        # ── МЕЖЭТАЖНАЯ (ТЗ 26.07 §2) ──
        if sub == "interfloor":
            # п.10 (Герман 30.07): в УГЛОВОЙ и КРАЕВОЙ зоне ставим
            # межэтажный профиль в 100 мм от края замкнутой области
            # (краевая = край захватки, углом не помеченный)
            for ex in (x0 + edge_rail, x1 - edge_rail):
                if x0 - EPS < ex < x1 + EPS and \
                        not any(abs(ex - j) < edge_rail - EPS
                                for j in joints):
                    joints = sorted(joints + [ex])
            if not floors_c:
                notes.append("контур %d: межэтажная без отметок "
                             "перекрытий — задайте точки или шаг "
                             "этажа" % (ci + 1))
                continue
            step_m = float(step_main or 800.0)
            step_c = float(step_corner or step_m)
            for f in floors_c:
                # НГП — горизонтальный несущий профиль (рвётся окнами и
                # уступами стены; 23.09 — по контуру, не по габариту)
                segs = list(_hspans(outer, f))
                for bx0, by0, bx1, by1 in hole_boxes:
                    if by0 - EPS < f < by1 + EPS:
                        segs = [(sa2, sb2) for sa, sb in segs
                                for sa2, sb2 in
                                (((sa, min(sb, bx0)),
                                  (max(sa, bx1), sb)))
                                if sb2 - sa2 > EPS]
                # кронштейны по центру перекрытия, шаг по горизонтали —
                # только там, где есть НГП (иначе кронштейн «в воздухе»)
                for px in _hpos(x0, x1, min_corner, corner_zone,
                                step_m, step_c, corners):
                    if _in_boxes(hole_boxes, px, f) or \
                            not any(sa - EPS <= px <= sb + EPS for sa, sb in segs):
                        continue          # в проёме / вне стены / уступ
                    brackets.append({"x": round(px, 4),
                                     "y": round(f, 4),
                                     "kind": "несущий"})
                for sa, sb in segs:
                    if stock > EPS and sb - sa > stock + EPS:
                        notes.append("НГП на отм. %.0f длиной %.0f > "
                                     "хлыста %.0f" % (f, sb - sa,
                                                      stock))
                    hrails.append({"y": round(f, 4),
                                   "x0": round(sa, 4),
                                   "x1": round(sb, 4),
                                   "len": round(sb - sa, 4),
                                   "kind": "НГП"})
            # вертикальные профили по рустам (центр руста, БЕЗ
            # смещения у окон): между перекрытиями НСП; куски над/под
            # окнами — ШП-60-20
            def _if_line(jx, lim=None, flank=False):
                """Вертикаль межэтажной по оси jx: между перекрытиями НСП, куски над/под окнами —
                ШП-60-20, вставки (ВС-300/В-70) на узлах НСП×НГП, кляммеры. lim=(низ, верх) — только
                в этих пределах; flank — НСП вдоль бокового откоса или кромки выреза (30.09b): узел
                с НГП и на концах, упёртых в перекрытие; стартовый кляммер — только у настоящего
                низа облицовки (под низом куска нет стены или там проём)."""
                spans = list(_vspans(outer, jx))
                if lim is not None:
                    # до конца стены осталось не больше зазора стыка (перекрытие у самого верха
                    # фронтона) — НСП до конца стены, как стойка «только кляммеров» (_clamps_on_rails)
                    tl = gap + 1.0
                    sp9 = []
                    for a9, b9 in spans:
                        lo9, hi9 = max(a9, lim[0]), min(b9, lim[1])
                        if hi9 - lo9 <= EPS:
                            continue
                        sp9.append((a9 if lo9 - a9 <= tl else lo9, b9 if b9 - hi9 <= tl else hi9))
                    spans = sp9
                touch = []
                for bx0, by0, bx1, by1 in hole_boxes:
                    if bx0 - EPS < jx < bx1 + EPS:
                        spans = _sub_y(spans, by0, by1)
                        touch += [by0, by1]
                made = 0
                for s_lo, s_hi in spans:
                    if s_hi - s_lo <= EPS:
                        continue
                    is_shp = any(abs(s_lo - t) < 1 or abs(s_hi - t) < 1
                                 for t in touch)
                    fl_in = [f for f in floors_c
                             if s_lo + EPS < f < s_hi - EPS]
                    cuts = ([s_lo, s_hi] if s_hi - s_lo <= rail_std + EPS
                            else [s_lo] + fl_in + [s_hi])
                    for i in range(len(cuts) - 1):
                        a2 = cuts[i] + (gap / 2.0 if i > 0 else 0.0)
                        b2 = cuts[i + 1] - (gap / 2.0
                                            if i + 1 < len(cuts) - 1
                                            else 0.0)
                        if b2 - a2 <= EPS:
                            continue
                        rails.append({"x": round(jx, 4),
                                      "y0": round(a2, 4),
                                      "y1": round(b2, 4),
                                      "len": round(b2 - a2, 4),
                                      "clamp_role": "flank" if flank else "regular",
                                      "kind": "ШП-60-20" if is_shp
                                      else "НСП"})
                        made += 1
                    if not is_shp:
                        # вставки (ВС-300/В-70) на стыках НСП с НГП; у НСП вдоль откоса —
                        # и на концах, упёртых в перекрытие
                        nodes = list(fl_in)
                        if flank:
                            nodes += [f for f in floors_c
                                      if (abs(f - s_lo) <= 1.0 or abs(f - s_hi) <= 1.0)
                                      and not _in_boxes(hole_boxes, jx, f)]
                        for f in nodes:
                            fittings.append({"x": round(jx, 4),
                                             "y": round(f, 4),
                                             "kind": "вставка"})
                    n0c = len(clamps)
                    _piece_clamps(clamps, rows, s_lo, s_hi, jx,
                                  False, fl_in, wedges,
                                  on_seam=_on_seam(jx))
                    if flank and len(clamps) > n0c and clamps[n0c]["kind"] == "стартовый" and \
                            _inside_pt(outer, jx, s_lo - 30.0, tol=0.0) and \
                            not _in_boxes(hole_boxes, jx, s_lo - 30.0):
                        del clamps[n0c]       # низ на перекрытии — облицовка идёт и ниже
                    _edge_top_clamps(clamps, rows, s_lo, s_hi, jx,
                                     _ytop(outer, jx, s_hi, y1), hole_boxes)
                return made

            for jx in joints:
                if jx < x0 - EPS or jx > x1 + EPS:
                    continue
                _if_line(jx)
            # 30.09b (Герман, ответ на (а) PDF №29): «в простенках между окон не может не быть
            # вертикальных направляющих – они там обязательно идут. Идут вдоль боковых откосов». Так и
            # в АТР «Вектор-1», тип 4 (лист 7, узел 9.19): НСП по обоим бокам проёма от пояса до пояса;
            # СП-60-40 под окном крепится к ним («к крайним межэтажным профилям», ТЗ 26.07). У каждой
            # боковой грани проёма, где за ней стена, — НСП вдоль откоса: в edge_offset от грани (как у
            # окна в остальных подсистемах), в простенке уже 2×edge_offset — посередине простенка; по
            # высоте — от перекрытия под низом проёма до перекрытия над верхом (нет перекрытия — до
            # низа/верха стены), стыки и вставки на перекрытиях, как у НСП по осям. Ось руста/сетки уже
            # у грани (от грани до edge_offset+50 от неё) — она и есть направляющая у откоса (ТЗ: «у окон
            # по центру руста, без смещения»). Плитка — так же у краёв вырезов и уступов зоны (29.09t
            # ставил там кусок «кромка +50/50»: в межэтажной он висел, не доходя до НГП; общий блок
            # 29.09t межэтажную теперь пропускает).
            off_w = edge_off if edge_off > EPS else edge_rail
            base_ax = [j for j in joints if x0 - EPS <= j <= x1 + EPS]
            reqs = []                   # (x, (полоса «своей» оси), низ, верх нужного)
            narrow = []                 # простенки уже NSP_MIN_PIER у граней проёмов

            def _need(lo_e, hi_e):
                return (max([f for f in floors_c if f <= lo_e + EPS], default=-1e18),
                        min([f for f in floors_c if f >= hi_e - EPS], default=1e18))
            for bi, (bx0, by0, bx1, by1) in enumerate(hole_boxes):
                oth = [ob for k9, ob in enumerate(hole_boxes)
                       if k9 != bi and ob[1] < by1 - EPS and ob[3] > by0 + EPS]
                # простенок у грани меняется по высоте (соседний проём только на части высоты, уступ
                # стены): делим высоту проёма отметками соседей и вершин контура и на каждом участке
                # берём ширину стены за гранью; ставим по самому узкому (от NSP_MIN_PIER) — НСП в нём
                # попадает и во все более широкие
                cuts9 = sorted({by0, by1} |
                               {v for ob in oth for v in (ob[1], ob[3]) if by0 + EPS < v < by1 - EPS} |
                               {q[1] for q in outer if by0 + EPS < q[1] < by1 - EPS})
                nlo, nhi = _need(by0, by1)
                for sd, edge in ((-1.0, bx0), (1.0, bx1)):
                    widths = []
                    for ya9, yb9 in zip(cuts9, cuts9[1:]):
                        if yb9 - ya9 <= EPS:
                            continue
                        ym9 = (ya9 + yb9) / 2.0
                        wiv = [(a9, b9) for a9, b9 in _hspans(outer, ym9) if a9 - EPS <= edge <= b9 + EPS]
                        if not wiv:
                            continue
                        on9 = [ob for ob in oth if ob[1] < ym9 < ob[3]]
                        if sd < 0:
                            far = max([wiv[0][0]] + [ob[2] for ob in on9 if ob[2] <= edge + EPS])
                        else:
                            far = min([wiv[-1][1]] + [ob[0] for ob in on9 if ob[0] >= edge - EPS])
                        wp = sd * (far - edge)            # ширина простенка на этом участке
                        if wp > EPS:                      # иначе за гранью не стена (край, вплотную)
                            widths.append((wp, far))
                    narrow += [w9 for w9, _f9 in widths if TILE_MIN_RUN <= w9 < NSP_MIN_PIER - EPS]
                    fit = [(w9, f9) for w9, f9 in widths if w9 >= NSP_MIN_PIER - EPS]
                    if not fit:
                        continue                          # стены нет или профиль не помещается
                    wp, far = min(fit)
                    # ось на самой грани проёма режется этим проёмом (по высоте окна её нет) —
                    # «своей» не считается: полоса не доходит до грани на 1e-3
                    if wp <= 2.0 * off_w + EPS:
                        zx = (edge + far) / 2.0
                        band = (min(edge, far) + 1e-3, max(edge, far) - 1e-3)
                    else:
                        zx = edge + sd * off_w
                        bl = edge + sd * (off_w + 50.0)
                        band = (bl, edge - 1e-3) if sd < 0 else (edge + 1e-3, bl)
                    reqs.append((zx, band, nlo, nhi))
            if tile:
                nv = len(outer)
                for i in range(nv):
                    (xa, ya), (xb, yb) = outer[i], outer[(i + 1) % nv]
                    if abs(xb - xa) > 0.5 or abs(yb - ya) < 100.0:
                        continue                      # только вертикальные кромки от 100 мм
                    lo_e, hi_e = min(ya, yb), max(ya, yb)
                    ym = (lo_e + hi_e) / 2.0
                    if _pip_strict(outer, xa + 2.0, ym) and not _pip_strict(outer, xa - 2.0, ym):
                        zx = xa + edge_rail
                    elif _pip_strict(outer, xa - 2.0, ym) and not _pip_strict(outer, xa + 2.0, ym):
                        zx = xa - edge_rail
                    else:
                        continue
                    reqs.append((zx, (zx - 50.0, zx + 50.0)) + _need(lo_e, hi_e))
            flanks = []                 # [x, [(низ, верх), …]] — одна ось на полосу
            for zx, (b0, b1), nlo, nhi in reqs:
                if any(b0 - EPS <= j <= b1 + EPS for j in base_ax):
                    continue
                hit = [fk for fk in flanks if b0 - EPS <= fk[0] <= b1 + EPS]
                if hit:
                    hit[0][1].append((nlo, nhi))
                else:
                    flanks.append([zx, [(nlo, nhi)]])
            flank_iv, n_fl = [], 0
            for zx, ivs in flanks:
                mg = []
                for lo, hi in sorted(ivs):
                    if mg and lo <= mg[-1][1] + 1.0:
                        mg[-1][1] = max(mg[-1][1], hi)
                    else:
                        mg.append([lo, hi])
                for lo, hi in mg:
                    n_fl += _if_line(zx, (lo, hi), flank=True)
                flank_iv.append((zx, mg))
            if n_fl:
                notes.append("контур %d: межэтажная — вдоль боковых откосов%s добавлено НСП %d "
                             "(от перекрытия до перекрытия)"
                             % (ci + 1, " и у краёв вырезов" if tile else "", n_fl))
            if narrow:
                notes.append("контур %d: у граней проёмов простенков уже %.0f мм — %d (самый узкий "
                             "%.0f мм): НСП вдоль откоса не помещается — проверьте"
                             % (ci + 1, NSP_MIN_PIER, len(narrow), min(narrow)))
            # СП-60-40 в подоконной зоне на скобах С1 к крайним
            # межэтажным профилям
            jset = [j for j in joints if x0 - EPS <= j <= x1 + EPS]
            for bx0, by0, bx1, by1 in hole_boxes:
                # п.7 (Герман 30.07): «крепится к ближайшим/крайним
                # профилям около окна». Ось, СОВПАВШАЯ с гранью проёма,
                # раньше отбрасывалась (строгое <) и СП тянулся до
                # следующей — «соединяет СП со следующими направляющими»
                # 30.09b: крайние профили у окна — и НСП вдоль откосов (где они есть на высоте
                # низа окна)
                ax_here = jset + [fx for fx, mg in flank_iv
                                  if any(lo - 1.0 <= by0 <= hi + 1.0 for lo, hi in mg)]
                left = [j for j in ax_here if j <= bx0 + EPS]
                right = [j for j in ax_here if j >= bx1 - EPS]
                if not left or not right:
                    continue
                la2, ra2 = max(left), min(right)
                # 23.09 (ревью): СП тянулся до ближайших осей и мог пройти
                # СКВОЗЬ соседнее окно на той же высоте — режем соседними
                # проёмами и стеной, оставляем кусок под своим окном
                segs = [(max(a6, la2), min(b6, ra2)) for a6, b6 in _hspans(outer, by0)]
                for ox0, oy0, ox1, oy1 in hole_boxes:
                    if (ox0, oy0, ox1, oy1) == (bx0, by0, bx1, by1):
                        continue
                    # соседний проём на этой высоте ИЛИ прямо под окном (верх
                    # соседа = низ окна: стены под подоконником там нет)
                    if oy0 + EPS < by0 < oy1 + 1.0:
                        segs = [(sa2, sb2) for sa, sb in segs
                                for sa2, sb2 in ((sa, min(sb, ox0)), (max(sa, ox1), sb))
                                if sb2 - sa2 > EPS]
                segs = [(sa, sb) for sa, sb in segs if sa < bx1 - EPS and sb > bx0 + EPS]
                if not segs:
                    continue
                sa, sb = segs[0]
                if abs(sa - la2) > EPS or abs(sb - ra2) > EPS:
                    notes.append("СП-60-40 под окном X=%.0f..%.0f упирается в соседнее "
                                 "окно/край стены — укорочен до %.0f..%.0f" % (bx0, bx1, sa, sb))
                hrails.append({"y": round(by0, 4),
                               "x0": round(sa, 4),
                               "x1": round(sb, 4),
                               "len": round(sb - sa, 4),
                               "kind": "СП-60-40"})
                for fx in (sa, sb):
                    if any(abs(fx - j) <= EPS for j in (la2, ra2)):
                        fittings.append({"x": round(fx, 4),
                                         "y": round(by0, 4),
                                         "kind": "скоба С1"})
            continue

        # ── ОРТОГОНАЛЬНАЯ (ТЗ 26.07 §3) ──
        if sub == "ortho":
            step_m = float(step_main or 800.0)
            step_c = float(step_corner or step_m)
            xs_g = _hpos(x0, x1, min_corner, corner_zone,
                         step_m, step_c, corners)
            ys_g = []
            yy = y0 + float(start_off or 300.0)
            while yy < y1 - EPS:
                ys_g.append(yy)
                yy += ortho_v
            # п.9 (Герман 30.07): ДОП. КРОНШТЕЙНЫ У ПРОЁМОВ. Сетка сама
            # по себе нанесена верно, но если от ближайшего кронштейна
            # до грани окна больше ortho_oh (300) — ставим дополнительный
            # в edge_rail (100) от грани. Сбоку — только в пределах
            # ВЫСОТЫ окна, сверху — только в пределах ЕГО ШИРИНЫ.
            add_br = []
            for bx0, by0, bx1, by1 in hole_boxes:
                ys_in = [yy for yy in ys_g if by0 - EPS < yy < by1 + EPS]
                lf = [px for px in xs_g if px <= bx0 + EPS]
                rt = [px for px in xs_g if px >= bx1 - EPS]
                if ys_in and lf and bx0 - max(lf) > ortho_oh + EPS:
                    add_br += [(bx0 - edge_rail, yy) for yy in ys_in]
                if ys_in and rt and min(rt) - bx1 > ortho_oh + EPS:
                    add_br += [(bx1 + edge_rail, yy) for yy in ys_in]
                up = [yy for yy in ys_g if yy >= by1 - EPS]
                xs_in = [px for px in xs_g if bx0 - EPS < px < bx1 + EPS]
                if up and xs_in and min(up) - by1 > ortho_oh + EPS:
                    add_br += [(px, by1 + edge_rail) for px in xs_in]
                    # 03.08 (аудит): верхние доп. кронштейны должны
                    # ДЕРЖАТЬ профиль — перемычка ГП над окном между
                    # осями Z-профилей (раньше кронштейны висели в
                    # воздухе: ряда сетки на этой отметке нет)
                    hx0 = max(bx0 - edge_off, x0)
                    hx1 = min(bx1 + edge_off, x1)
                    yl = by1 + edge_rail
                    # 23.09: перемычка — по контуру стены и мимо соседних окон
                    lsegs = [(max(sa, hx0), min(sb, hx1)) for sa, sb in _hspans(outer, yl)]
                    for ox0, oy0, ox1, oy1 in hole_boxes:
                        if oy0 + EPS < yl < oy1 - EPS:
                            lsegs = [(sa2, sb2) for sa, sb in lsegs
                                     for sa2, sb2 in ((sa, min(sb, ox0)), (max(sa, ox1), sb))
                                     if sb2 - sa2 > EPS]
                    for sa, sb in lsegs:
                        if sb - sa <= EPS or sb < bx0 - EPS or sa > bx1 + EPS:
                            continue
                        hrails.append({"y": round(yl, 4),
                                       "x0": round(sa, 4),
                                       "x1": round(sb, 4),
                                       "len": round(sb - sa, 4),
                                       "kind": "ГП-40-40"})
            n_add = 0
            for px, yy in add_br:
                if _in_boxes(hole_boxes, px, yy) or not _inside_pt(outer, px, yy):
                    continue
                if not any(abs(h["y"] - yy) <= EPS and h["x0"] - EPS <= px <= h["x1"] + EPS
                           for h in hrails) and \
                        not any(abs(g - yy) <= EPS for g in ys_g):
                    continue                     # ГП на отметке нет
                if any(near_pt(b, px, yy) for b in brackets):
                    continue
                brackets.append({"x": round(px, 4), "y": round(yy, 4),
                                 "kind": "рядовой"})
                n_add += 1
            if n_add:
                notes.append("доп. кронштейнов у проёмов: %d" % n_add)

            # сетка кронштейнов (кроме проёмов) — на ГП своего ряда
            for yy in ys_g:
                # ГП-40-40 горизонтальный на каждом ряду сетки (по контуру)
                segs = list(_hspans(outer, yy))
                for bx0, by0, bx1, by1 in hole_boxes:
                    if by0 - EPS < yy < by1 + EPS:
                        segs = [(sa2, sb2) for sa, sb in segs
                                for sa2, sb2 in
                                (((sa, min(sb, bx0)),
                                  (max(sa, bx1), sb)))
                                if sb2 - sa2 > EPS]
                for px in xs_g:
                    if _in_boxes(hole_boxes, px, yy) or \
                            not any(sa - EPS <= px <= sb + EPS for sa, sb in segs):
                        continue
                    brackets.append({"x": round(px, 4),
                                     "y": round(yy, 4),
                                     "kind": "рядовой"})
                for sa, sb in segs:
                    hrails.append({"y": round(yy, 4),
                                   "x0": round(sa, 4),
                                   "x1": round(sb, 4),
                                   "len": round(sb - sa, 4),
                                   "kind": "ГП-40-40"})
            top_y = max(ys_g) if ys_g else y0
            # вертикальные ШП по рустам; у окон — Z-образные со
            # смещением 100 и выступом 50 (как вертикальная)
            zone_side = set()
            n0_rails = len(rails)
            # 04.08 (фидбэк Германа, п.4): «при ортогональной не
            # рисуются профили по середине плитки, если она более
            # 600 мм; в вертикальной и межэтажной нормально». Причина:
            # mid_rail_over применялся ТОЛЬКО в вертикальной ветке, а
            # у Ортогональной он и вовсе стоял null в systems.json.
            # Промежуточный ШП ставится там же, где в вертикальной, —
            # посередине пролёта шире mid_rail_over.
            jx_o = sorted(set(joints))
            if mid_over and len(jx_o) >= 2:
                mids_o = [(jx_o[i] + jx_o[i + 1]) / 2.0
                          for i in range(len(jx_o) - 1)
                          if jx_o[i + 1] - jx_o[i] > mid_over + EPS]
                jx_o = sorted(jx_o + mids_o)
            staged_o = []
            for jx in jx_o:
                if jx < x0 - EPS or jx > x1 + EPS:
                    continue
                pieces = [(lo7, hi7, jx) for lo7, hi7 in _vspans(outer, jx)]
                for bx0, by0, bx1, by1 in hole_boxes:
                    inside = bx0 - EPS < jx < bx1 + EPS
                    sx = None
                    if not inside and edge_off > EPS:
                        if jx <= bx0 + EPS and \
                                bx0 - jx < edge_off - EPS:
                            sx = bx0 - edge_off
                        elif jx >= bx1 - EPS and \
                                jx - bx1 < edge_off - EPS:
                            sx = bx1 + edge_off
                    if not inside and sx is None:
                        continue
                    sy0 = by0 - (overhang if sx is not None else 0.0)
                    sy1 = by1 + (overhang if sx is not None else 0.0)
                    nxt = []
                    for lo, hi, xe in pieces:
                        c0 = max(lo, sy0 if sx is not None else by0)
                        c1 = min(hi, sy1 if sx is not None else by1)
                        if c1 - c0 <= EPS or (xe != jx):
                            nxt.append((lo, hi, xe))
                            continue
                        if c0 - lo > EPS:
                            nxt.append((lo, c0, xe))
                        if not inside:
                            nxt.append((c0, c1, sx))
                        if hi - c1 > EPS:
                            nxt.append((c1, hi, xe))
                    pieces = nxt
                # 03.08 (аудит): ось, накрытая ЧУЖИМ окном по X, —
                # режем его высотой (стойка не идёт сквозь соседний
                # проём)
                cut2 = []
                for lo, hi, xe in pieces:
                    spans = [(lo, hi)]
                    for ox0, oy0, ox1, oy1 in hole_boxes:
                        if ox0 + EPS < xe < ox1 - EPS:
                            spans = _sub_y(spans, oy0, oy1)
                    for a4, b4 in spans:
                        cut2.append((a4, b4, xe))
                pieces = _clip_pieces(outer, cut2)
                # 29.09 (синтетика «в ролях»): две оси руста у одной грани окна (швы
                # раскладки 1164 и 1176 у окон с узким простенком) смещались в одну и ту же
                # точку — два одинаковых Z-профиля в одном месте. Как в вертикальной
                # (04.08, D-сверка полигона): куски копим и сводим по оси — собственная
                # ось важнее смещённой, от смещённой остаётся незанятое, осколки < 100 — нет
                for _p in pieces:
                    if _p[1] - _p[0] > EPS:
                        staged_o.append((_p[0], _p[1], _p[2], jx, None))
            for s_lo, s_hi, s_x, jx, _st in _dedup_pieces(staged_o):
                if s_hi - s_lo <= EPS:
                    continue
                side = abs(s_x - jx) > EPS
                if side:
                    zone_side.add(round(s_x, 4))
                if stock > EPS and s_hi - s_lo > stock + EPS:
                    notes.append("ШП X=%.0f длиной %.0f > хлыста "
                                 "%.0f" % (s_x, s_hi - s_lo,
                                           stock))
                if not side and s_hi - top_y > ortho_oh + EPS:
                    notes.append("ШП X=%.0f: консольный свес "
                                 "%.0f > %.0f" %
                                 (s_x, s_hi - top_y, ortho_oh))
                # п.11 (Герман 30.07): профиль стандартный 3000,
                # свес ≤300, стык ПОСЕРЕДИНЕ между кронштейнами.
                # Раньше клали ОДНИМ куском на всю высоту зоны —
                # динблок столько не растягивался («профилей нет
                # выше первого этажа», фидбэк по сборке №10)
                segs_o = _cut_by_len(s_lo, s_hi, rail_std, gap)
                for za, zb in segs_o:
                    rails.append({"x": round(s_x, 4),
                                  "y0": round(za, 4),
                                  "y1": round(zb, 4),
                                  "len": round(zb - za, 4),
                                  "clamp_role": "window" if side else "regular",
                                  "kind": "Z-профиль" if side
                                  else "ШП-60-20"})
                # стыки кусков ШП → комбинированный на первом шве
                # выше (полигон: КОМБ 61 на орто стоят над стыками)
                seams_o = [zb for za, zb in segs_o[:-1]]
                _piece_clamps(clamps, rows, s_lo, s_hi, s_x,
                              side, seams_o, wedges,
                              on_seam=_on_seam(jx))
                if not side:
                    _edge_top_clamps(clamps, rows, s_lo, s_hi,
                                     s_x, _ytop(outer, s_x, s_hi, y1), hole_boxes)
            # 23.09n (Герман, ответ по сборке №24): кусок вертикали выше
            # последнего ГП, не опирающийся ни на один ГП, ДЛИННЕЕ 300 мм —
            # добавить ГП у верха: на 300 ниже верха куска («300 от торца»,
            # как у нижнего ряда), с кронштейнами по колонкам сетки;
            # кусок ≤ 300 мм — как есть
            top_piece = 300.0
            add_y = []
            for r in rails[n0_rails:]:
                if r["len"] <= top_piece + EPS:
                    continue
                if any(h["x0"] - EPS <= r["x"] <= h["x1"] + EPS and
                       r["y0"] - EPS <= h["y"] <= r["y1"] + EPS for h in hrails):
                    continue
                yt = r["y1"] - top_piece
                if not any(abs(yt - a0) <= 50.0 for a0 in add_y):
                    add_y.append(yt)
            for yy in add_y:
                segs = list(_hspans(outer, yy))
                for bx0, by0, bx1, by1 in hole_boxes:
                    if by0 - EPS < yy < by1 + EPS:
                        segs = [(sa2, sb2) for sa, sb in segs
                                for sa2, sb2 in ((sa, min(sb, bx0)), (max(sa, bx1), sb))
                                if sb2 - sa2 > EPS]
                for px in xs_g:
                    if _in_boxes(hole_boxes, px, yy) or \
                            not any(sa - EPS <= px <= sb + EPS for sa, sb in segs) or \
                            any(near_pt(b, px, yy) for b in brackets):
                        continue
                    brackets.append({"x": round(px, 4), "y": round(yy, 4),
                                     "kind": "рядовой"})
                for sa, sb in segs:
                    hrails.append({"y": round(yy, 4), "x0": round(sa, 4),
                                   "x1": round(sb, 4), "len": round(sb - sa, 4),
                                   "kind": "ГП-40-40"})
            if add_y:
                notes.append("доп. ГП у верха (кусок вертикали > 300 мм выше "
                             "последнего ГП): %d" % len(add_y))
            # 02.08 (замечание №1 Дениса/полигон): Z-образные у КАЖДОЙ
            # грани окна ВСЕГДА (ТЗ §3: «у окон Z-образные, длина =
            # сторона окна + 100») — раньше Z возникал, лишь если ось
            # сетки случайно стояла ближе edge_off СНАРУЖИ грани, и
            # доп. кронштейны п.9 висели без профиля.
            if edge_off > EPS:
                for bx0, by0, bx1, by1 in hole_boxes:
                    for zx in (bx0 - edge_off, bx1 + edge_off):
                        if any(abs(zx - m) <= EPS for m in zone_side):
                            continue
                        if zx < x0 - EPS or zx > x1 + EPS:
                            continue
                        z0 = max(by0 - overhang, y0)
                        z1 = min(by1 + overhang, y1)
                        if z1 - z0 <= EPS:
                            continue
                        # 03.08 (аудит): Z режется чужими окнами
                        # и МИНУС существующие куски этой оси (±50) —
                        # ни дублей, ни Z сквозь проём; осколки <100
                        # не ставим (В-аг)
                        spans = [(a8, b8) for a8, b8, _x8 in
                                 _clip_pieces(outer, [(z0, z1, zx)])]
                        for ox0, oy0, ox1, oy1 in hole_boxes:
                            if ox0 + EPS < zx < ox1 - EPS:
                                spans = _sub_y(spans, oy0, oy1)
                        for rr in rails[n0_rails:]:
                            if abs(rr["x"] - zx) <= 50.0:
                                spans = _sub_y(spans, rr["y0"],
                                               rr["y1"])
                        spans = [(a5, b5) for a5, b5 in spans
                                 if b5 - a5 > 100.0]
                        if not spans:
                            continue
                        zone_side.add(round(zx, 4))
                        for sa, sb in spans:
                            for za, zb in _cut_by_len(sa, sb,
                                                      rail_std, gap):
                                rails.append({"x": round(zx, 4),
                                              "y0": round(za, 4),
                                              "y1": round(zb, 4),
                                              "len": round(zb - za, 4),
                                              "clamp_role": "window",
                                              "kind": "Z-профиль"})
                            _piece_clamps(clamps, rows, sa, sb, zx,
                                          True, [], wedges)
            continue

        # ── ВЕРТИКАЛЬНАЯ (дефолт; ТЗ 26.07 §1) ──
        # доп. направляющая по центру плиты шире 600 (ТЗ 26.07):
        # пролёт между соседними осями больше порога → ось в середине
        jx_all = [j for j in joints if x0 - EPS <= j <= x1 + EPS]
        # 04.08 (Герман п.2): стойки угловых и краевых зон. Раньше
        # стойки шли только по осям рустов, поэтому у внешнего угла и
        # у конца облицовки профиля с кронштейнами не появлялось.
        # Включается наличием указанных углов (corners_x) или явным
        # rail_step_corner; совпавшие с рустом ближе 50 мм отбрасываем,
        # чтобы не получить сдвоенный профиль.
        if (corners is not None or rail_step_corner) and not tile:
            extra = _zone_rail_axes(x0, x1, corners, corner_zone,
                                    rail_step_corner, rail_step_main,
                                    edge_rail_off)
            add_ax = [e for e in extra
                      if not any(abs(e - j) <= 50.0 for j in jx_all)]
            if add_ax:
                jx_all = sorted(jx_all + add_ax)
                notes.append("стоек в угловых/краевых зонах добавлено: "
                             "%d" % len(add_ax))
        if mid_over and len(jx_all) >= 2:
            mids = []
            for i in range(len(jx_all) - 1):
                if jx_all[i + 1] - jx_all[i] > mid_over + EPS:
                    mids.append((jx_all[i] + jx_all[i + 1]) / 2.0)
            jx_all = sorted(jx_all + mids)

        zone_side = set()
        n0_rails = len(rails)
        staged = []
        for jx in jx_all:
            # угловая зона (В17: типовой случай — полоса у краёв зоны)
            in_corner = _in_corner(jx, x0, x1, corner_zone,
                                   corners)
            step = step_corner if in_corner else step_main

            # куски стойки (lo, hi, ось): проём, накрывающий ось по X,
            # ВЫРЕЗАЕТ диапазон; ось ближе edge_offset к грани проёма —
            # на высоте проёма кусок СМЕЩАЕТСЯ от грани (26.07: крепёж
            # не ближе 100 мм от края проёма)
            pieces = [(lo7, hi7, jx) for lo7, hi7 in _vspans(outer, jx)]
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

            # 03.08 (аудит): кусок, чья ОСЬ накрыта ЧУЖИМ окном по X,
            # режется его высотой — раньше смещённая стойка окна А
            # (и гарантированная 02.08) шла СКВОЗЬ соседнее окно Б
            cut2 = []
            for lo, hi, xe in pieces:
                spans = [(lo, hi)]
                for ox0, oy0, ox1, oy1 in hole_boxes:
                    if ox0 + EPS < xe < ox1 - EPS:
                        spans = _sub_y(spans, oy0, oy1)
                for a4, b4 in spans:
                    cut2.append((a4, b4, xe))
            n_cut = len(cut2)
            pieces = _clip_pieces(outer, cut2)
            if len(pieces) < n_cut and any(abs(xe - jx) > EPS for _a, _b, xe in cut2):
                notes.append("оконная стойка у края зоны (ось %.0f) за контуром стены — "
                             "не ставится" % jx)

            # 04.08 (D-сверка полигона): укладка кусков отложена —
            # смещённая оконная стойка одной оси и СОБСТВЕННАЯ ось
            # руста, стоящая ровно в edge_off от грани, дают ОДНУ
            # координату X → два профиля в одном месте (на полигоне
            # 4 оси × 16 позиций = 64 дубля кронштейнов). Сначала
            # копим, потом дедуп по оси (приоритет — собственная).
            for _p in pieces:
                if _p[1] - _p[0] > EPS:
                    # Moving an opening rail across a corner-zone boundary
                    # changes its design pressure: use the emitted axis, not
                    # the original cladding joint axis.
                    piece_step = step_corner if _in_corner(_p[2], x0, x1, corner_zone, corners) else step_main
                    staged.append((_p[0], _p[1], _p[2], jx, piece_step))

        for s_lo, s_hi, s_x, jx, step in _dedup_pieces(staged):
            if True:
                if s_hi - s_lo <= EPS:
                    continue
                side = abs(s_x - jx) > EPS       # оконный (смещённый)
                if side:
                    zone_side.add(round(s_x, 4))
                fl_in = [f for f in floors_c
                         if s_lo + EPS < f < s_hi - EPS]
                # направляющие. Отметки УКАЗАНЫ: куски между стыками
                # (стык центрован на отметке, зазор gap — В15/В18),
                # первый кронштейн куска, начавшегося стыком на
                # перекрытии, — «несущий» (В-е). Отметок НЕТ (lash,
                # Герман 07.08 ответ 2): хлысты РОВНО rail_std от
                # низа куска, зазор gap МЕЖДУ хлыстами (полигон: низы
                # 24070.3+3010n), короткий хвост делит длину с предыдущим,
                # кронштейны
                # все одного типа. В обоих режимах на каждую
                # направляющую: 300 от торцов + равномерно ≤ шага
                # (ТЗ 26.07), короткая — один в центре.
                segs2 = []
                if lash:
                    cuts = _rail_cuts(s_lo, s_hi, rail_std, gap, start_off)
                    if len(cuts) > 1 and cuts[-2][1] - cuts[-2][0] < rail_std - EPS:
                        rebalanced_cuts += 1
                    segs2 = [(a, b, False) for a, b in cuts]
                    seams_in = [b for _a, b, _f in segs2[:-1]]
                else:
                    cuts = ([s_lo, s_hi]
                            if s_hi - s_lo <= rail_std + EPS
                            else [s_lo] + fl_in + [s_hi])
                    for i in range(len(cuts) - 1):
                        a = cuts[i] + (gap / 2.0 if i > 0 else 0.0)
                        b = cuts[i + 1] - (gap / 2.0
                                           if i + 1 < len(cuts) - 1
                                           else 0.0)
                        if b - a <= EPS:
                            continue
                        if stock > EPS and b - a > stock + EPS:
                            notes.append(
                                "направляющая X=%.0f длиной %.0f > "
                                "хлыста %.0f — задайте перекрытия"
                                % (s_x, b - a, stock))
                        segs2.append((a, b, i > 0))
                    seams_in = fl_in
                for a, b, joined in segs2:
                    rails.append({"x": round(s_x, 4),
                                  "y0": round(a, 4),
                                  "y1": round(b, 4),
                                  "len": round(b - a, 4),
                                  "clamp_role": "window" if side else "regular",
                                  "kind": "направляющая"})
                    if step is not None and start_off is not None:
                        pos = _rail_brackets(a, b, float(start_off),
                                             float(step), exact_step)
                        for pi, y in enumerate(pos):
                            kind = ("несущий" if joined and pi == 0
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
                _piece_clamps(clamps, rows, s_lo, s_hi, s_x, side,
                              seams_in, wedges, on_seam=_on_seam(jx))
                if not side:
                    _edge_top_clamps(clamps, rows, s_lo, s_hi, s_x,
                                     _ytop(outer, s_x, s_hi, y1), hole_boxes)

        # 02.08 (замечание №1 Дениса/полигон): оконные стойки у
        # КАЖДОЙ грани проёма ВСЕГДА (ТЗ §1: «направляющие слева/
        # справа на 100 от края, длина = сторона окна + 100»), даже
        # если рядом нет оси руста — раньше окно, стоящее между
        # осями, оставалось без стоек и без боковых кляммеров.
        # Межэтажная — своя ветка (30.09b: НСП вдоль откосов, от перекрытия до перекрытия).
        if sub == "vertical" and edge_off > EPS:
            for bx0, by0, bx1, by1 in hole_boxes:
                for zx in (bx0 - edge_off, bx1 + edge_off):
                    if any(abs(zx - m) <= EPS for m in zone_side):
                        continue
                    if zx < x0 - EPS or zx > x1 + EPS:
                        continue
                    z0 = max(by0 - overhang, y0)
                    z1 = min(by1 + overhang, y1)
                    if z1 - z0 <= EPS:
                        continue
                    # 03.08 (аудит): гарантированная стойка
                    # режется ЧУЖИМИ окнами и МИНУС уже существующие
                    # куски этой же оси (±50: ось руста ровно в
                    # edge_off от грани, пересекающиеся окна) —
                    # ни дублей, ни стоек сквозь проём; осколки
                    # короче 100 не ставим. Стойка в 50..300 от
                    # грани — В-аг
                    spans = [(a8, b8) for a8, b8, _x8 in
                             _clip_pieces(outer, [(z0, z1, zx)])]
                    for ox0, oy0, ox1, oy1 in hole_boxes:
                        if ox0 + EPS < zx < ox1 - EPS:
                            spans = _sub_y(spans, oy0, oy1)
                    for rr in rails[n0_rails:]:
                        if abs(rr["x"] - zx) <= 50.0:
                            spans = _sub_y(spans, rr["y0"], rr["y1"])
                    spans = [(a5, b5) for a5, b5 in spans
                             if b5 - a5 > 100.0]
                    if not spans:
                        continue
                    zone_side.add(round(zx, 4))
                    in_c = _in_corner(zx, x0, x1, corner_zone,
                                      corners)
                    stp = step_corner if in_c else step_main
                    rail_parts = []
                    for za, zb in spans:
                        cuts = _rail_cuts(za, zb, rail_std, gap, start_off) if lash else [(za, zb)]
                        if len(cuts) > 1 and cuts[-2][1] - cuts[-2][0] < rail_std - EPS:
                            rebalanced_cuts += 1
                        rail_parts.extend(cuts)
                        _piece_clamps(clamps, rows, za, zb, zx, True,
                                      [b for _a, b in cuts[:-1]], wedges)
                    for za, zb in rail_parts:
                        rails.append({"x": round(zx, 4),
                                      "y0": round(za, 4),
                                      "y1": round(zb, 4),
                                      "len": round(zb - za, 4),
                                      "clamp_role": "window",
                                      "kind": "направляющая"})
                        if stp is not None and start_off is not None:
                            for y in _rail_brackets(za, zb,
                                                    float(start_off),
                                                    float(stp),
                                                    exact_step):
                                brackets.append({"x": round(zx, 4),
                                                 "y": round(y, 4),
                                                 "kind": "рядовой"})

    # 29.09l (Герман, ответ на 9в PDF №27): «лучше делать стык хлыстов на
    # направляющей». Прогоны шин режутся на хлысты ПОСЛЕ расстановки: стык — на
    # вертикальном профиле ЭТОЙ зоны, проходящем через высоту шины (±TILE_SUP_TOL).
    # Номера прогонов — сквозные по всем зонам.
    if tile:
        bnd = rail_from + [(None, len(rails))]
        sup_c = dict((c0, rails[i0:i1]) for (c0, i0), (_c1, i1) in zip(bnd, bnd[1:]))
        # 29.09t (Герман, ответ на 6е PDF №28: «необходимо вставить дополнительную направляющую в
        # 100 мм, как у окна»): у каждой вертикальной кромки контура зоны (края вырезов — простенки
        # у входов, уступы) — направляющая в edge_rail от кромки внутрь стены, по высоте кромки +
        # overhang сверху и снизу, как оконная стойка; по стене и мимо проёмов; своя направляющая в
        # ±50 мм уже есть — не ставим (у краёв габарита — крайние оси сетки). Длиннее хлыста — хлыстами.
        n_notch = 0
        kind_v = {"interfloor": "НСП", "ortho": "Z-профиль"}.get(sub, "направляющая")
        gap_v = float(system.get("rail_gap") or 0.0)
        # 30.09b: межэтажная ставит НСП у краёв вырезов в своей ветке — от перекрытия до перекрытия
        for c0, (outer_c, boxes_c) in (sorted(tile_geo.items()) if sub != "interfloor" else []):
            cx0, _cy0, cx1, _cy1 = _bbox(outer_c)
            own = list(sup_c.get(c0, []))
            nv = len(outer_c)
            for i in range(nv):
                (xa, ya), (xb, yb) = outer_c[i], outer_c[(i + 1) % nv]
                if abs(xb - xa) > 0.5 or abs(yb - ya) < 100.0:
                    continue                             # только вертикальные кромки от 100 мм
                lo_e, hi_e = min(ya, yb), max(ya, yb)
                ym = (lo_e + hi_e) / 2.0
                if _pip_strict(outer_c, xa + 2.0, ym) and not _pip_strict(outer_c, xa - 2.0, ym):
                    zx = xa + edge_rail
                elif _pip_strict(outer_c, xa - 2.0, ym) and not _pip_strict(outer_c, xa + 2.0, ym):
                    zx = xa - edge_rail
                else:
                    continue
                spans = [(a8, b8) for a8, b8, _x8 in
                         _clip_pieces(outer_c, [(lo_e - overhang, hi_e + overhang, zx)])]
                for ox0, oy0, ox1, oy1 in boxes_c:
                    if ox0 + EPS < zx < ox1 - EPS:
                        spans = _sub_y(spans, oy0, oy1)
                for rr in own:
                    if abs(rr["x"] - zx) <= 50.0:
                        spans = _sub_y(spans, rr["y0"], rr["y1"])
                for za, zb in [(a5, b5) for a5, b5 in spans if b5 - a5 > 100.0]:
                    rail_parts = _rail_cuts(za, zb, rail_std, gap_v,
                                           start_off if sub == "vertical" else None)
                    if len(rail_parts) > 1 and rail_parts[-2][1] - rail_parts[-2][0] < rail_std - EPS:
                        rebalanced_cuts += 1
                    for pa, pb in rail_parts:
                        if pb - pa <= EPS:
                            continue
                        piece = {"x": round(zx, 4), "y0": round(pa, 4), "y1": round(pb, 4),
                                 "len": round(pb - pa, 4), "kind": kind_v}
                        rails.append(piece)
                        own.append(piece)
                        n_notch += 1
                        if sub == "vertical" and step_main is not None and start_off is not None:
                            stp = step_corner if _in_corner(zx, cx0, cx1, corner_zone, corners) else step_main
                            for yb2 in _rail_brackets(pa, pb, float(start_off), float(stp), exact_step):
                                brackets.append({"x": round(zx, 4), "y": round(yb2, 4), "kind": "рядовой"})
            sup_c[c0] = own
        if n_notch:
            notes.append("плитка: у краёв вырезов и уступов зоны добавлено направляющих %d (в %.0f мм от "
                         "кромки, как у окна)" % (n_notch, edge_rail))
        # 29.09q (Герман, 29.09n: «если шина не попадает ни на одну направляющую, то надо её
        # удлинить»): прогон, под которым нет ни одной направляющей (узкое окно, полоска у края),
        # удлиняется в обе стороны до ближайших направляющих — по стене на его высоте, не через
        # проёмы; соседний прогон на той же высоте (±20 мм) уступает место. Нет направляющих и
        # там (простенок без направляющих) — прогон остаётся как есть, счёт в замечаниях.
        n_ext, extra_all = 0, []
        by_c = {}
        for h in hrails:
            if "_c" in h:
                by_c.setdefault(h["_c"], []).append(h)
        for c0, runs in by_c.items():
            outer_c, boxes_c = tile_geo.get(c0, (None, []))
            if outer_c is None:
                continue
            rl = sup_c.get(c0, ())
            nxt_run = max(h["run"] for h in runs) + 1
            extra = []
            for h in runs:
                if h.get("_dead"):
                    continue
                yy = h["y"]
                sup = sorted(set(round(r["x"], 4) for r in rl
                                 if r["y0"] - TILE_SUP_TOL <= yy <= r["y1"] + TILE_SUP_TOL))
                if any(h["x0"] - EPS <= x <= h["x1"] + EPS for x in sup):
                    continue
                pr = 2.0 if h["kind"] == "шина стартовая" else (-2.0 if h["kind"] == "шина концевая" else 0.0)
                yp = yy + pr
                spans = list(_hspans(outer_c, yp))
                for bx0, by0, bx1, by1 in boxes_c:
                    if by0 + EPS < yp < by1 - EPS:
                        spans = [(a2, b2) for a1, b1 in spans
                                 for a2, b2 in ((a1, min(b1, bx0)), (max(a1, bx1), b1)) if b2 - a2 > EPS]
                xm = (h["x0"] + h["x1"]) / 2.0
                sp = [(a1, b1) for a1, b1 in spans if a1 - EPS <= xm <= b1 + EPS]
                if not sp:
                    continue
                lo, hi = sp[0]
                left = [x for x in sup if lo - EPS <= x < h["x0"] - EPS]
                right = [x for x in sup if h["x1"] + EPS < x <= hi + EPS]
                nx0 = left[-1] if left else h["x0"]
                nx1 = right[0] if right else h["x1"]
                # рядовая не залезает на стартовую/концевую той же высоты — упирается в неё
                if h["kind"] == "шина рядовая":
                    for g in runs + extra:
                        if g is h or g.get("_dead") or g["kind"] == "шина рядовая" or abs(g["y"] - yy) >= 20.0:
                            continue
                        if g["x1"] <= h["x0"] + EPS and g["x1"] > nx0:
                            nx0 = g["x1"]
                        if g["x0"] >= h["x1"] - EPS and g["x0"] < nx1:
                            nx1 = g["x0"]
                if nx0 >= h["x0"] - EPS and nx1 <= h["x1"] + EPS:
                    continue
                h["x0"], h["x1"], h["len"] = round(nx0, 4), round(nx1, 4), round(nx1 - nx0, 4)
                n_ext += 1
                for g in runs + extra:           # соседи на той же высоте уступают место
                    if g is h or g.get("_dead") or abs(g["y"] - yy) >= 20.0:
                        continue
                    if h["kind"] == "шина рядовая" and g["kind"] != "шина рядовая":
                        continue                 # стартовая/концевая рядовой не уступает
                    if g["x1"] <= nx0 + EPS or g["x0"] >= nx1 - EPS:
                        continue
                    remaining = [q for q in ((g["x0"], min(g["x1"], nx0)), (max(g["x0"], nx1), g["x1"]))
                             if q[1] - q[0] >= TILE_MIN_RUN]
                    if not remaining:
                        g["_dead"] = True
                        continue
                    g["x0"], g["x1"] = round(remaining[0][0], 4), round(remaining[0][1], 4)
                    g["len"] = round(g["x1"] - g["x0"], 4)
                    if len(remaining) > 1:
                        extra.append(dict(g, x0=round(remaining[1][0], 4), x1=round(remaining[1][1], 4),
                                          len=round(remaining[1][1] - remaining[1][0], 4), run=nxt_run))
                        nxt_run += 1
            extra_all.extend(extra)
        if n_ext or extra_all:
            hrails = [h for h in hrails if not h.get("_dead")] + extra_all
        new_hr, run_id, n_air, n_bare = [], {}, 0, 0
        for h in hrails:
            if "_c" not in h:
                new_hr.append(h)
                continue
            yy, sa, sb = h["y"], h["x0"], h["x1"]
            sup = sorted(set(round(r["x"], 4) for r in sup_c.get(h["_c"], ())
                             if r["y0"] - TILE_SUP_TOL <= yy <= r["y1"] + TILE_SUP_TOL
                             and sa - EPS <= r["x"] <= sb + EPS))
            cuts, air = _whip_cuts(sa, sb, sup, tile_whip)
            n_air += air
            rid = run_id.setdefault((h["_c"], h["run"]), len(run_id) + 1)
            pts = [sa] + cuts + [sb]
            for xa, xb in zip(pts, pts[1:]):
                if not any(xa - EPS <= x <= xb + EPS for x in sup):
                    n_bare += 1
                new_hr.append({"y": yy, "x0": round(xa, 4), "x1": round(xb, 4),
                               "len": round(xb - xa, 4), "kind": h["kind"], "run": rid})
        hrails = new_hr
        n_wh = sum(1 for h in hrails if h["kind"] in SHINA_KINDS)
        if n_wh:
            notes.append("плитка: хлыстов шины %d — не длиннее %.0f мм, стык на направляющей "
                         "(после окна — заново от грани проёма)" % (n_wh, tile_whip))
        if n_ext:
            notes.append("плитка: шин удлинено до ближайших направляющих %d (под ними не было ни "
                         "одной — узкое окно, полоска у края)" % n_ext)
        if n_air:
            notes.append("плитка: стыков шины без направляющей %d — в пределах хлыста %.0f мм "
                         "направляющей нет" % (n_air, tile_whip))
        if n_bare:
            notes.append("плитка: кусков шины без направляющей под ними %d — удлинить не до чего (в "
                         "простенке или у края нет направляющих) — проверьте" % n_bare)
        if n_air or n_bare:
            unsupported = [dict(h) for h in hrails if h["kind"] in SHINA_KINDS and
                           not any(r["y0"] - TILE_SUP_TOL <= h["y"] <= r["y1"] + TILE_SUP_TOL and
                                   h["x0"] - EPS <= r["x"] <= h["x1"] + EPS for r in rails)]
            return {"ok": False, "error_code": "E_UNSUPPORTED_SHINA",
                    "error": "Подсистема не построена: шин без направляющей %d, стыков без опоры %d. "
                             "Измените шаг направляющих/границы участка или разработайте узел с конструктором. "
                             "Прежняя подсистема сохранена." % (n_bare, n_air),
                    "unsupported": unsupported, "unsupported_counts": {"pieces": n_bare, "joints": n_air},
                    "notes": notes}

    if rebalanced_cuts:
        notes.append("Перераспределены последние два хлыста на %d участках: сохранены зазор и "
                     "заданные отступы кронштейнов от торцов; искусственный короткий хвост устранён."
                     % rebalanced_cuts)

    if calc_rep is None and sub == "vertical" and parts != "clamps":
        members, _horizontal = geometric_members(sub, rails, hrails, brackets)
        missing = [dict(member_index=m["index"], **m["geometry"],
                        length=round(m["geometry"]["y1"] - m["geometry"]["y0"], 4),
                        support_y=m["support_y"], support_count=m["support_count"],
                        bracket_start_offset=start_off) for m in members if m["support_count"] < 2]
        if missing:
            first = missing[0]
            return {"ok": False, "error_code": "E_UNSUPPORTED_RAIL",
                    "error": "Подсистема не построена: %d направляющих имеют менее двух опор. "
                             "Первая: X=%.1f, Y=%.1f…%.1f мм, длина %.1f мм, опор %d. "
                             "При заданных отступах от торцов исходный участок слишком короток; "
                             "требуется изменение границ/стыков или отдельное решение крепления. "
                             "Прежняя подсистема сохранена." %
                             (len(missing), first["x"], first["y0"], first["y1"],
                              first["length"], first["support_count"]),
                    "unsupported": missing, "unsupported_counts": {"rail_pieces": len(missing)},
                    "notes": notes}

    if calc_rep is not None:
        # A passed arithmetic chain does not establish the static model.
        # Record every emitted piece, including zero-gap abutting pieces,
        # before any addressed refusal discards the drawable output.
        boxes = [_bbox(_closed(c.get("outer") or []))
                 for c in req.get("contours") or [] if len(c.get("outer") or []) >= 3]
        member_zones = []
        for rail in rails:
            x, y = rail["x"], (rail["y0"] + rail["y1"]) / 2.0
            own_boxes = [box for box in boxes if box[0] - 0.5 <= x <= box[2] + 0.5 and
                         box[1] - 0.5 <= y <= box[3] + 0.5]
            member_zones.append("corner" if any(
                _in_corner(x, box[0], box[2], corner_zone, corners) for box in own_boxes) else "row")
        static_model = screen_layout(sub, rails, hrails, brackets, calc_rep["inputs"],
                                     member_zones, rail_gap=gap,
                                     profiles_by_zone=calc_rep["profile"], bracket_steps=calc_rep["steps"])
        if sub == "vertical" and start_off is not None:
            brackets, refinements = refine_vertical_supports(rails, brackets, static_model,
                calc_rep, float(start_off), exact_step)
            calc_rep["support_refinements"] = refinements
            if refinements:
                notes.append("На %d направляющих шаг кронштейнов уменьшен по расчёту самого куска; "
                             "общий шаг — верхний предел. Профили и торцевые отступы сохранены."
                             % len(refinements))
                static_model = screen_layout(sub, rails, hrails, brackets, calc_rep["inputs"],
                    member_zones, rail_gap=gap, profiles_by_zone=calc_rep["profile"],
                    bracket_steps=calc_rep["steps"])
        calc_rep["static_model"] = static_model

    if calc_rep is not None and sub == "interfloor":
        # A supported-span chain cannot approve an unmodelled free end.
        # Existing support_tol only matches endpoints across the splice gap;
        # it is not an engineering allowance for a cantilever. Intersections
        # do not prove connection strength or continuity through a splice.
        # Manual engineering layouts remain available.
        missing = []
        support_tol = gap / 2.0 + 1.0
        ngp = [h for h in hrails if h["kind"] == "НГП"]
        for rail in rails:
            if rail["kind"] != "НСП":
                continue
            supports = sorted(set(h["y"] for h in ngp
                                  if h["x0"] - EPS <= rail["x"] <= h["x1"] + EPS and
                                  rail["y0"] - support_tol <= h["y"] <= rail["y1"] + support_tol))
            bottom_free = max(0.0, supports[0] - rail["y0"]) if supports else rail["y1"] - rail["y0"]
            top_free = max(0.0, rail["y1"] - supports[-1]) if supports else rail["y1"] - rail["y0"]
            if len(supports) < 2 or max(bottom_free, top_free) > support_tol:
                missing.append({"x": rail["x"], "y0": rail["y0"], "y1": rail["y1"],
                                "support_y": supports, "bottom_free": round(bottom_free, 4),
                                "top_free": round(top_free, 4), "match_tolerance": support_tol,
                                "reason": "fewer_than_two_supports" if len(supports) < 2 else "unsupported_free_end"})
        if missing:
            return {"ok": False, "error_code": "E_CALC_TOPOLOGY_UNSUPPORTED",
                    "error": "Расчёт не применён: %d кусков НСП имеют менее двух пересечений с НГП "
                             "либо свободный конец за крайней опорой. Консольная/одноопорная схема "
                             "этой расчётной цепочкой не проверяется. "
                             "Уточните отметки/опоры либо используйте ручной режим по проектному расчёту; "
                             "прежняя подсистема сохранена." % len(missing),
                    "unsupported": missing, "unsupported_counts": {"nsp_pieces": len(missing)},
                    "calc_inputs": calc_rep["inputs"], "static_model": static_model}
    if calc_rep is not None:
        topology_error = topology_refusal(static_model)
        if topology_error:
            topology_error.update(calc_inputs=calc_rep["inputs"],
                                  notes=notes + [static_model["scope"]])
            return topology_error
        verification, error = _verify_calc_spacing(calc_rep, req, sub, rails, hrails, brackets,
                                                   corners, corner_zone)
        if error:
            return {"ok": False, "error_code": "E_CALC_LAYOUT", "error": error,
                    "static_model": static_model, "calc_inputs": calc_rep["inputs"]}
        calc_rep["layout_verification"] = verification
        notes.append("Статическая модель не подтверждена: расчёт пролётов и свесов каждого куска "
                     "при равномерной нагрузке не задаёт неподвижные/подвижные соединения, "
                     "непрерывность через стыки и распределение веса.")
        calc_rep["method"] = {
            "name": "Вектор — цепочка по переданным статическим расчётам",
            "version": "member-beams-2026-10-02",
            "coverage": "Каждый кусок проверен по фактическим пролётам и свесам при равномерной "
                        "поперечной нагрузке; проверены шаги и число геометрических опор. "
                        "Закрепления, стыки, распределение веса, "
                        "горизонтальные НГП/СП и проект в целом требуют отдельной проверки конструктора."}
    clamps = _merge_clamps(clamps)
    # 01.08 (письмо Германа, п.2): марка профиля для СОСТОЯНИЯ
    # ВИДИМОСТИ динблока (C# ставит видимость по этому полю).
    # «направляющая» вертикальной системы: явный выбор пользователя →
    # подобранный расчётом → пресет системы. НСП межэтажной: выбор
    # НСП-1/НСП-2 (критерий выбора — вопрос Герману). Z ортогональной
    # → ZП-40-20 (написание Германа 01.08).
    rail_prof = rail_prof_req
    if not rail_prof and calc_rep is not None:
        rail_prof = (calc_rep.get("profile") or {}).get("row")
    if not rail_prof:
        rail_prof = (system.get("calc") or {}).get("profile")
    nsp = str(req.get("nsp_type") or "НСП-1")
    _pm = {"направляющая": rail_prof or "направляющая",
           "НСП": nsp, "Z-профиль": "ZП-40-20"}
    for r in rails:
        r["profile"] = _pm.get(r["kind"], r["kind"])
    for r in hrails:
        r["profile"] = (tile_brand if tile_brand and r["kind"] in SHINA_KINDS
                        else r["kind"])
    lm = sum(r["len"] for r in rails) / 1000.0
    hlm = sum(r["len"] for r in hrails) / 1000.0
    system_used = {k: v for k, v in system.items()
                   if not k.startswith("_src")}
    summary = {
        "system": system.get("_name", "?"),
        "sub_type": sub,
        "rails": len(rails),
        "rails_lm": round(lm, 2),
        "hrails": len(hrails),
        "hrails_lm": round(hlm, 2),
        "fittings": len(fittings),
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
    summary["parts"] = parts
    summary["cladding"] = cladding
    if tile:
        summary.update(shina_summary(hrails))
        summary["tile_whip"] = tile_whip
        summary["tile_rail_brand"] = tile_brand
    if parts == "clamps":
        notes.append("только кляммеры по РАСЧЁТНЫМ осям (существующие направляющие "
                     "не переданы) — подсистема не выдаётся")
        rails, hrails, brackets, fittings = [], [], [], []
        for k in ("rails", "hrails", "fittings", "brackets_main", "brackets_row"):
            summary[k] = 0
        summary["rails_lm"] = summary["hrails_lm"] = 0.0
        summary["rail_stock_est"] = None
    out = {"ok": True, "rails": rails, "hrails": hrails,
           "brackets": brackets, "clamps": clamps,
           "fittings": fittings, "summary": summary, "notes": notes,
           "system_used": system_used}
    if calc_rep is not None:
        out["calc_report"] = calc_rep
        summary["calc_steps"] = calc_rep["steps"]
    return out
