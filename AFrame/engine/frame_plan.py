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
CLAMP_MERGE = 100.0  # мм: ближе — один кляммер (кромка откоса
                     # и шов раскладки почти совпали, 30.07 п.5)
WIN_NEAR = 200.0     # мм: стойка «принадлежит» грани проёма


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


def _rail_brackets(a, b, start_off, step, exact=False):
    """Кронштейны ОДНОЙ направляющей [a, b] — ТЗ Германа 26.07:
    первый 300 от НИЗА, последний 300 от ВЕРХА, между ними равномерно
    с шагом ≤ расчётного (3000 → 300-800-800-800-300 = 4 шт);
    нестандартная длина — крайние 300/300, между ними ≤ шага;
    короче 2×300 — один кронштейн в центре.

    exact=True — фидбэк Германа 04.08 п.1: «при ручной установке шага
    800 программа ставит кронштейны через 798». Так и было задумано
    (В16, решение Дениса 24.07: «размазывая длину между перекрытиями»
    — на куске 2990 остаётся 2390 между крайними, что при трёх
    пролётах даёт 796.7), но ЗАДАННЫЙ РУКАМИ шаг конструктор ожидает
    видеть буквально. В этом режиме идём от низа ровно шагом, а
    неполный остаток добираем последним кронштейном в 300 от верха —
    короче шага получается только последний пролёт."""
    L = b - a
    if L <= 2 * start_off + EPS:
        return [a + L / 2.0]
    lo, hi = a + start_off, b - start_off
    if not step:
        return [lo, hi]
    if exact:
        out = [lo]
        y = lo + step
        while y < hi - EPS:
            out.append(y)
            y += step
        if hi - out[-1] > EPS:
            out.append(hi)
        return out
    k = max(1, int(math.ceil((hi - lo - EPS) / step)))
    return [lo + (hi - lo) * j / k for j in range(k + 1)]


def _win_edges(holes, axes, edge_off=0.0):
    """Оконные стойки — где разрешён БОКОВОЙ кляммер (Герман 30.07 п.4:
    только в пределах высоты окна). Берём ось у самой грани (ближе
    WIN_NEAR) и смещённые позиции грань∓edge_off вертикальной.
    В межэтажной смещения нет (ТЗ §2) и ось может стоять далеко от
    откоса — тогда боковых не будет: ОТКРЫТЫЙ ВОПРОС В-ц Герману."""
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


def _apply_calc(calc_req, system, sub, joints, floors, floor_step,
                corners=None):
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
    b = float(p.get("b") or _median_gap(joints, 600.0))
    rail_len = float(p.get("rail_len") or floor_step or
                     _median_gap(floors, 3000.0))
    scheme = p.get("scheme") or \
        {"vertical": "vertical", "interfloor": "interfloor",
         "ortho": "ortho"}.get(sub, "vertical")
    inp = dict(scheme=scheme, terrain=str(p["terrain"]),
               height=float(p["height"]), q_clad=float(p["q_clad"]),
               gamma_clad=float(p.get("gamma_clad") or 1.1),
               q_rails=float(p.get("q_rails") or 0.0),
               offset=float(p["offset"]), na_max=float(p["na_max"]),
               bracket=str(p["bracket"]),
               extender=p.get("extender") or None,
               profile=p["profile"], b_row=b,
               b_corner=float(p.get("b_corner") or 0) or None,
               rail_len=rail_len,
               max_step=float(p.get("max_step") or 800.0),
               n_rivets=int(p.get("n_rivets") or 2),
               # п.2 (30.07): подбирать профиль вместе с шагом
               auto_profile=bool(p.get("auto_profile", True)),
               profile_candidates=p.get("profile_candidates"))
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
           "inputs": {k: inp[k] for k in
                      ("terrain", "height", "q_clad", "offset",
                       "na_max", "b_row", "b_corner", "rail_len",
                       "max_step")},
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
            fixed.append((round(x, 4), min(a, b), max(a, b)))
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
        for x, a, b in fixed:
            if not (x0 - EPS <= x <= x1 + EPS and a < y1 - EPS and b > y0 + EPS):
                continue
            spans = [(c0, c1) for c0, c1, _xe in _clip_pieces(outer, [(a, b, x)])]
            for bx0, by0, bx1, by1 in hole_boxes:
                if bx0 + EPS < x < bx1 - EPS:
                    spans = _sub_y(spans, by0, by1)
            mine.extend((x, c0, c1) for c0, c1 in spans if c1 - c0 > EPS)
        mine.sort()
        if not mine:
            continue
        used.update((x, a) for x, a, _b in mine)
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
        wedges = _win_edges(hole_boxes, zj or sorted({x for x, _a, _b in mine}), edge_off)
        # стойки: куски одной оси с малым зазором
        stands = []
        for x, a, b in mine:
            if stands and abs(stands[-1][0] - x) <= EPS and a - stands[-1][2] <= 50.0 + EPS:
                st = stands[-1]
                # стык: межэтажная и с отметками — центр зазора (= отметка),
                # хлысты/ортогональная — верх нижнего куска (как движок)
                st[3].append(0.5 * (st[2] + a)
                             if (sub == "interfloor" or (floors_c and sub == "vertical"))
                             else st[2])
                st[2] = max(st[2], b)
            else:
                stands.append([x, a, b, []])
        # хлыст, не дошедший до ВЕРХА стены на зазор стыка (остаток ≤ зазора
        # хлыстом не закрывается) — стойка, как при расстановке, до верха
        tail = float(system.get("rail_gap") or 0.0) + 1.0
        for st in stands:
            yt = _ytop(outer, st[0], st[2], y1)
            if 0.0 < yt - st[2] <= tail:
                st[2] = yt
        for x, a, b, seams in stands:
            side = False
            if sub != "interfloor" and edge_off > EPS:
                for bx0, by0, bx1, by1 in hole_boxes:
                    if (abs(x - (bx0 - edge_off)) <= 0.5 or abs(x - (bx1 + edge_off)) <= 0.5) and \
                            a >= by0 - overhang - 1.0 and b <= by1 + overhang + 1.0:
                        side = True
                        break
            # отметки перекрытий — стыки для вертикальной и межэтажной; у
            # ортогональной комбинированный только над стыками кусков
            fl_in = seams + [f for f in (floors_c if sub != "ortho" else [])
                             if a + EPS < f < b - EPS and
                             not any(abs(f - q) <= 50.0 for q in seams)]
            _piece_clamps(clamps, rows, a, b, x, side, sorted(fl_in), wedges,
                          on_seam=any(abs(x - j) <= seam_tol for j in joints))
            if not side or sub == "interfloor":
                _edge_top_clamps(clamps, rows, a, b, x, _ytop(outer, x, b, y1), hole_boxes)
    clamps = _merge_clamps(clamps)
    skipped = len(fixed) - len({(x, a) for x, a, _b in fixed if (x, a) in used or
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


def frame_plan(req):
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
    hrails, fittings = [], []
    # 23.09b (Герман): что раскладывать — all | frame (без кляммеров) |
    # clamps (только кляммеры; по rails_fixed — существующим направляющим)
    parts = str(req.get("parts") or "all").strip().lower()
    if parts not in ("all", "frame", "clamps"):
        return {"ok": False, "error": "parts: all|frame|clamps (получено %r)" % parts}
    if parts == "clamps" and req.get("rails_fixed") is not None:
        return _clamps_on_rails(req, sub, system, joints, rows, floors)
    if parts == "frame":
        rows = []
    if not joints:
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
    # направляющей сужает подбор до этого сечения. ГП-60-40 в
    # расчётном справочнике НЕТ — считаем по ГП-40-40 (в запас),
    # рисуем выбранную видимость.
    _USER_PROF = {
        "ГП-40-40": ["ГП-40-40-1,2"],
        "ГП-60-40": ["ГП-40-40-1,2"],
        "ШП-60-20": ["ШП-60-20-1,2", "ШП-60-20-20-1,2"],
    }
    rail_prof_req = (str(req.get("rail_profile") or "").strip()
                     or None)
    calc_in = req.get("calc")
    if rail_prof_req and calc_in is not None:
        cands = _USER_PROF.get(rail_prof_req)
        if cands:
            calc_in = dict(calc_in or {})
            calc_in.setdefault("profile_candidates", cands)
            if rail_prof_req == "ГП-60-40":
                notes.append(
                    "сечения ГП-60-40 нет в расчётном справочнике — "
                    "несущая способность посчитана по ГП-40-40 "
                    "(в запас); нужны характеристики профиля")
    if calc_in is not None:
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
            if not calc_in.get("b") and b_fact > b_med + 0.5:
                calc_in["b"] = b_fact
                notes.append("грузовая ширина для расчёта %.0f мм — максимальная по "
                             "итоговым осям стоек (медиана исходных осей %.0f)"
                             % (b_fact, b_med))
        calc_rep, cerr, csteps = _apply_calc(
            calc_in or {}, system, sub, joints, floors,
            floor_step, corners)
        if cerr:
            return {"ok": False, "error": cerr}
        step_main, step_corner = csteps
        notes.append(
            "шаги кронштейнов ПО РАСЧЁТУ: рядовая %.0f / угловая %.0f "
            "(W_p=%.1f/%.1f кг/м²)"
            % (step_main, step_corner, calc_rep["row"]["w_p"],
               calc_rep["corner"]["w_p"]))
        if calc_rep.get("bc_note"):
            notes.append(calc_rep["bc_note"])

    for ci, c in enumerate(req.get("contours") or []):
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
            for jx in joints:
                if jx < x0 - EPS or jx > x1 + EPS:
                    continue
                spans = list(_vspans(outer, jx))
                touch = []
                for bx0, by0, bx1, by1 in hole_boxes:
                    if bx0 - EPS < jx < bx1 + EPS:
                        spans = _sub_y(spans, by0, by1)
                        touch += [by0, by1]
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
                                      "kind": "ШП-60-20" if is_shp
                                      else "НСП"})
                    if not is_shp:
                        # вставки (ВС-300/В-70) на стыках НСП с НГП
                        for f in fl_in:
                            fittings.append({"x": round(jx, 4),
                                             "y": round(f, 4),
                                             "kind": "вставка"})
                    _piece_clamps(clamps, rows, s_lo, s_hi, jx,
                                  False, fl_in, wedges,
                                  on_seam=_on_seam(jx))
                    _edge_top_clamps(clamps, rows, s_lo, s_hi, jx,
                                     _ytop(outer, jx, s_hi, y1), hole_boxes)
            # СП-60-40 в подоконной зоне на скобах С1 к крайним
            # межэтажным профилям
            jset = [j for j in joints if x0 - EPS <= j <= x1 + EPS]
            for bx0, by0, bx1, by1 in hole_boxes:
                # п.7 (Герман 30.07): «крепится к ближайшим/крайним
                # профилям около окна». Ось, СОВПАВШАЯ с гранью проёма,
                # раньше отбрасывалась (строгое <) и СП тянулся до
                # следующей — «соединяет СП со следующими направляющими»
                left = [j for j in jset if j <= bx0 + EPS]
                right = [j for j in jset if j >= bx1 - EPS]
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
                for s_lo, s_hi, s_x in pieces:
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
        if corners is not None or rail_step_corner:
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
                    staged.append((_p[0], _p[1], _p[2], jx, step))

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
                # 24070.3+3010n), последний обрезается, кронштейны
                # все одного типа. В обоих режимах на каждую
                # направляющую: 300 от торцов + равномерно ≤ шага
                # (ТЗ 26.07), короткая — один в центре.
                segs2 = []
                if lash:
                    yq = s_lo
                    while s_hi - yq > EPS:
                        ce = min(yq + rail_std, s_hi)
                        segs2.append((yq, ce, False))
                        yq = ce + gap
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
        # Межэтажную НЕ трогаем (В-ц: у окна по центру руста).
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
                    for za, zb in spans:
                        rails.append({"x": round(zx, 4),
                                      "y0": round(za, 4),
                                      "y1": round(zb, 4),
                                      "len": round(zb - za, 4),
                                      "kind": "направляющая"})
                        if stp is not None and start_off is not None:
                            for y in _rail_brackets(za, zb,
                                                    float(start_off),
                                                    float(stp),
                                                    exact_step):
                                brackets.append({"x": round(zx, 4),
                                                 "y": round(y, 4),
                                                 "kind": "рядовой"})
                        _piece_clamps(clamps, rows, za, zb, zx,
                                      True, [], wedges)

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
        r["profile"] = r["kind"]
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
