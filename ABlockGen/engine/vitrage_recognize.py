# -*- coding: utf-8 -*-
"""ABlockGen Э3: распознавание каркаса витража из АР-графики.

Вход  — JSON: полосы (bbox прямоугольных элементов АР — вставки импостов,
        в перспективе контуры линий/полилиний), панели (вставки с именами —
        для дверей), блоки-образцы (указанные пользователем), марки.
Выход — ТОТ ЖЕ формат плана, что у vitrage_plan (Э1): inserts[] с
        block/layer/x/y/rot/dyn/attrs — C#-вставка общая.

Правила (доказаны «Проба 3.dxf» 13.07 + «Образец 1/2.dxf» 15.07 —
docs/CONTRACT.md §Э3):
  1. Вертикальные полосы толщиной strip_w (40..210; Revit-импосты до 200)
     кластеризуются по X-интервалу и сливаются по Y в цепочку → СТОЙКА
     (АР рисует стойку сегментами между ригелями: 250+50+2400+900+300+285=4185).
     При ≥2 отметках стойка тянется сквозь обвязку до общего габарита;
     РАМКА ОБРАМЛЕНИЯ АР (замкнутый прямоугольник, охватывающий ≥50% полос)
     жёстко задаёт низ/верх каркаса (Образец 2: 3334 при графике 3470) и
     исключается из полос; сегментные цепочки, не несущие ≥2 отметок, —
     фантомы (дверные коробки, выноски) и отбрасываются.
  2. Ось стойки: средняя полоса (w≈body_w) → центр; КРАЙНЯЯ полоса шире
     ТИПИЧНОЙ ВНУТРЕННЕЙ +5 (фолбэк body_w+5; «50мм + 25мм зазор» к откосу)
     → тело body_w прижато к ВНУТРЕННЕЙ стороне: ось = грань ∓ body_w/2.
     body_w = min(50 [унификация v1], min АР-полоса); bbox образца блока
     НЕ мерить — обвес (183 при теле 50, урок 15.07b); params.body_w
     перекрывает.
  3. Горизонтальные полосы группируются по оси Y → ОТМЕТКА ригелей;
     ригель ставится в пролёте, если полосы отметки покрывают >50% его света.
  4. ДВЕРЬ (панель с именем под door_pat): в перекрытых ею пролётах
     отметка-«порог» (ось в полосе [y0двери−60 .. y0двери+60] или внутри
     Y-габарита двери) — ригель НЕ ставится.
  5. Вставка — контракт Проба_штапики_2: стойка (ось, низ) rot=0,
     dyn «Длина» = высота; ригель (ось левой стойки, отметка) rot=270,
     dyn «Длина» = ОСЕВОЙ шаг пролёта. Марки — по типоразмерам.
"""
import json
import math
import sys

from vitrage_plan import build_dims, cell_rows, size_attr

EPS = 1.0          # мм: допуск слияния координат АР
COVER_MIN = 0.5    # доля света пролёта, которую должна покрыть отметка


def _rnd05(v):
    return int(math.floor(float(v) + 0.5))


def fmt_len(v):
    return "{0:.2f}".format(float(v))


# ─────────────── классификация полос ───────────────

def classify_strips(strips, wmin, wmax):
    """→ (verticals, horizontals, cubes): списки bbox-кортежей
    (x0,y0,x1,y1,seg); seg=1 — полоса собрана из отрезков (15.07)."""
    vert, horz, cube = [], [], []
    for s in strips:
        x0, y0, x1, y1 = (float(s["x0"]), float(s["y0"]),
                          float(s["x1"]), float(s["y1"]))
        sg = 1 if s.get("seg") else 0
        if x1 < x0: x0, x1 = x1, x0
        if y1 < y0: y0, y1 = y1, y0
        w, h = x1 - x0, y1 - y0
        win = wmin <= w <= wmax
        hin = wmin <= h <= wmax
        if win and hin:
            cube.append((x0, y0, x1, y1, sg))   # стыковой кубик ~50×50
        elif win and h > w:
            vert.append((x0, y0, x1, y1, sg))
        elif hin and w > h:
            horz.append((x0, y0, x1, y1, sg))
        # остальное (значки, стрелки, крупные панели) — не полосы, мимо
    return vert, horz, cube


def chain_verticals(vert, cube):
    """Группировка вертикалей по X-интервалу (±EPS), слияние Y с кубиками.
    → список {x0,x1,y0,y1,gaps,pieces,from_seg} по возрастанию центра X;
    from_seg — ВСЕ полосы цепочки собраны из отрезков (кандидат в фантомы)."""
    groups = []
    for b in vert:
        for g in groups:
            if abs(g["x0"] - b[0]) <= EPS and abs(g["x1"] - b[2]) <= EPS:
                g["seg"].append((b[1], b[3]))
                g["from_seg"] = g["from_seg"] and bool(b[4]); break
        else:
            groups.append({"x0": b[0], "x1": b[2], "seg": [(b[1], b[3])],
                           "from_seg": bool(b[4])})
    for b in cube:  # кубики докидываются ТОЛЬКО в существующие X-группы
        for g in groups:
            if abs(g["x0"] - b[0]) <= EPS and abs(g["x1"] - b[2]) <= EPS:
                g["seg"].append((b[1], b[3]))
                g["from_seg"] = g["from_seg"] and bool(b[4]); break
    out = []
    for g in groups:
        pieces = _merge_ivs(g["seg"])
        y0, y1 = pieces[0][0], pieces[-1][1]
        gaps = [(pieces[k][1], pieces[k + 1][0]) for k in range(len(pieces) - 1)]
        out.append({"x0": g["x0"], "x1": g["x1"], "y0": y0, "y1": y1,
                    "gaps": gaps, "pieces": pieces, "from_seg": g["from_seg"]})
    out.sort(key=lambda z: (z["x0"] + z["x1"]) / 2.0)
    return out


def stand_axis(chain, body_w, is_left_edge, is_right_edge, typ_inner=0.0):
    """Ось стойки по полосе цепочки (правило крайних: тело изнутри).
    Крайняя полоса «широкая» (тело + зазор к откосу) сравнением с ТИПИЧНОЙ
    шириной ВНУТРЕННИХ полос (15.07, Образец 1: все полосы 100 при теле 50 —
    крайние НЕ шире внутренних → оси по центрам, как строит Алексей;
    Образец 2/Проба 3: крайние 200/150/75 шире внутренних 70/50 → тело
    прижато к внутренней грани). Без внутренних (2 стойки) — старый порог
    body_w."""
    w = chain["x1"] - chain["x0"]
    ref = typ_inner if typ_inner > 0 else body_w
    if w > ref + 5.0:
        if is_left_edge:
            return chain["x1"] - body_w / 2.0      # зазор снаружи (слева)
        if is_right_edge:
            return chain["x0"] + body_w / 2.0
    return (chain["x0"] + chain["x1"]) / 2.0


def group_rails(horz):
    """Горизонтали → отметки: группировка по оси Y (±EPS), объединение
    X-интервалов. → [{y, spans:[(x0,x1)], lo, hi}] по возрастанию y;
    lo/hi — Y-габарит полос группы (для правила обвязки «по краю»)."""
    rails = []
    for b in horz:
        ax = (b[1] + b[3]) / 2.0
        for r in rails:
            if abs(r["y"] - ax) <= EPS:
                r["spans"].append((b[0], b[2]))
                r["lo"] = min(r["lo"], b[1]); r["hi"] = max(r["hi"], b[3])
                break
        else:
            rails.append({"y": ax, "spans": [(b[0], b[2])],
                          "lo": b[1], "hi": b[3]})
    for r in rails:
        r["spans"].sort()
    rails.sort(key=lambda z: z["y"])
    return rails


def panel_grid(raw_strips, wmin, wmax):
    """ПАНЕЛЬНЫЙ АР (Проба 2, 15.07c): импосты не нарисованы — сетка задана
    ЗАЗОРАМИ между крупными панелями (стеклопакеты замкнутыми полилиниями,
    зазор 50). Крупные прямоугольники (оба измерения > wmax не обязательно:
    хотя бы одно >> полосы) кластеризуются по X и Y (merge пересечений —
    дверные панели вложены), зазоры кластеров wmin..wmax → СИНТЕЗ-ПОЛОСЫ
    зазоров + крайние полосы шириной в медианный зазор снаружи габарита
    (эталон: ось крайней = грань панелей ∓ gap/2). Дальше полосы идут общим
    пайплайном (chains/rails/оси/обвязка). → (synth_strips, notes)."""
    rects = []
    for s in raw_strips:
        if s.get("seg"):
            continue
        x0, y0 = float(s["x0"]), float(s["y0"])
        x1, y1 = float(s["x1"]), float(s["y1"])
        if x1 < x0: x0, x1 = x1, x0
        if y1 < y0: y0, y1 = y1, y0
        w, h = x1 - x0, y1 - y0
        if w > wmax and h > wmin:          # панель: широкая, не «полоска»
            rects.append((x0, y0, x1, y1))
    if len(rects) < 4:
        return [], []

    def clusters(ivs):
        """merge пересекающихся/вложенных интервалов (щель < wmin — та же
        панель: дверные вложения). Узкие добавляются первыми; интервал,
        цепляющий ≥2 готовых кластера, — СКВОЗНОЙ элемент (поручень
        ограждения во всю ширину, Проба 2) и пропускается."""
        out = []
        for a, b in sorted(ivs, key=lambda ab: ab[1] - ab[0]):
            hits = [c for c in out if a < c[1] + wmin and b > c[0] - wmin]
            if not hits:
                out.append([a, b])
            elif len(hits) == 1:
                hits[0][0] = min(hits[0][0], a)
                hits[0][1] = max(hits[0][1], b)
            # ≥2 кластеров — сквозной, мимо
        out.sort()
        return out

    cx = clusters([(r[0], r[2]) for r in rects])
    if len(cx) < 2:
        return [], []
    # панель принадлежит РОВНО одному столбцу; сквозная графика (поручни
    # ограждений во всю ширину, козырьки) пересекает ≥2 — вон из основы:
    # иначе она растягивает габарит (низ/верх ±50 на Пробе 2) и дробит ряды
    good = []
    for r in rects:
        hits = sum(1 for c in cx if r[0] < c[1] and r[2] > c[0])
        if hits == 1:
            good.append(r)
    if len(good) < 4:
        return [], []
    cy = clusters([(r[1], r[3]) for r in good])
    gx = [(cx[i][1], cx[i + 1][0]) for i in range(len(cx) - 1)]
    gx = [g for g in gx if wmin <= g[1] - g[0] <= wmax]
    if not gx:
        return [], []
    gaps = sorted(g[1] - g[0] for g in gx)
    gap_med = gaps[len(gaps) // 2]
    x_lo, x_hi = cx[0][0], cx[-1][1]
    y_lo, y_hi = min(r[1] for r in good), max(r[3] for r in good)
    rects = good
    synth = []
    # вертикали: зазоры между столбцами + крайние снаружи габарита панелей;
    # низ стоек = панели − зазор (Проба 2: стартовый профиль под нижним
    # рядом), верх = верх панелей (эталон)
    for a, b in gx:
        synth.append({"x0": a, "y0": y_lo - gap_med, "x1": b, "y1": y_hi})
    synth.append({"x0": x_lo - gap_med, "y0": y_lo - gap_med,
                  "x1": x_lo, "y1": y_hi})
    synth.append({"x0": x_hi, "y0": y_lo - gap_med,
                  "x1": x_hi + gap_med, "y1": y_hi})
    # горизонтали: зазоры между рядами (спан — по паре смежных панелей)
    n_rail = 0
    for i in range(len(cy) - 1):
        a, b = cy[i][1], cy[i + 1][0]
        if not (wmin <= b - a <= wmax):
            continue
        for lo, hi in _merge_ivs(
                [(max(r1[0], r2[0]), min(r1[2], r2[2]))
                 for r1 in rects for r2 in rects
                 if abs(r1[3] - a) <= EPS and abs(r2[1] - b) <= EPS
                 and min(r1[2], r2[2]) - max(r1[0], r2[0]) > wmin]):
            synth.append({"x0": lo, "y0": a, "x1": hi, "y1": b})
            n_rail += 1
    # краевые горизонтали ВНУТРЬ габарита стоек — дают обвязкам «полосу,
    # достигающую края» для клэмпа край ± тело_ригеля/2
    synth.append({"x0": x_lo, "y0": y_lo - gap_med, "x1": x_hi, "y1": y_lo})
    synth.append({"x0": x_lo, "y0": y_hi - gap_med, "x1": x_hi, "y1": y_hi})
    notes = ["панельный АР: сетка из зазоров (столбцов %d, осей %d, "
             "отметочных зазоров %d, зазор %.0f); посторонняя графика "
             "(ограждения и т.п.) в панельном режиме игнорируется"
             % (len(cx), len(gx) + 2, n_rail, gap_med)]
    return synth, notes


def thermal_tiers(y_lo, y_hi, l1, l2, gap):
    """Терморазрыв (сценарий Алексея 15.07c, витражи выше хлыста 6000):
    ярус 1 — [низ..L1], ярус 2 — [L1+gap..L2], далее этажи высотой яруса 2;
    последний ярус тянется до верха (остаток), если следующий полный этаж
    не оставляет места ещё на один. → [(y0, y1)] снизу вверх."""
    if not (y_lo + EPS < l1 < l2 < y_hi - EPS):
        raise ValueError("терморазрыв: линии L1=%.1f, L2=%.1f должны лежать "
                         "внутри конструкции %.1f..%.1f (L2 выше L1)"
                         % (l1, l2, y_lo, y_hi))
    h2 = l2 - (l1 + gap)
    if h2 <= gap:
        raise ValueError("терморазрыв: второй ярус вырожден (%.1f мм)" % h2)
    tiers = [(y_lo, l1), (l1 + gap, l2)]
    b = l2 + gap
    while y_hi - b > h2 + gap + h2 - EPS:   # влезает полный этаж И ещё один
        tiers.append((b, b + h2))
        b += h2 + gap
    tiers.append((b, y_hi))                  # верхний ряд — остаток
    return tiers


def covered(spans, a, b, min_frac):
    """Покрывают ли интервалы spans отрезок [a,b] хотя бы на min_frac?"""
    if b <= a:
        return False
    total = 0.0
    for s0, s1 in spans:
        lo, hi = max(a, s0), min(b, s1)
        if hi > lo:
            total += hi - lo
    return total >= (b - a) * min_frac


def _merge_ivs(ivs):
    """Слияние интервалов (union) с допуском EPS."""
    ivs = sorted(ivs)
    out = []
    for a, b in ivs:
        if out and a <= out[-1][1] + EPS:
            if b > out[-1][1]:
                out[-1][1] = b
        else:
            out.append([a, b])
    return [(a, b) for a, b in out]


def axis_lines(segments):
    """Отрезки → осевые ЛИНИИ: (координата, union-интервалы вдоль).
    Сегменты одной координаты (±0.5) сливаются в одну линию — раздробленный
    в АР контур (стойка, разрезанная ригелями) снова становится сплошным.
    → (vlines[(x, [(y0,y1)..])], hlines[(y, [(x0,x1)..])])."""
    vraw, hraw = [], []
    for s in segments:
        x0, y0 = float(s["x0"]), float(s["y0"])
        x1, y1 = float(s["x1"]), float(s["y1"])
        if abs(x1 - x0) <= 0.5 and abs(y1 - y0) > 0.5:
            vraw.append(((x0 + x1) / 2.0, min(y0, y1), max(y0, y1)))
        elif abs(y1 - y0) <= 0.5 and abs(x1 - x0) > 0.5:
            hraw.append(((y0 + y1) / 2.0, min(x0, x1), max(x0, x1)))

    def collect(raw):
        raw.sort()
        lines = []
        for c, lo, hi in raw:
            if lines and c - lines[-1][0] <= 0.5:
                lines[-1][1].append((lo, hi))
            else:
                lines.append([c, [(lo, hi)]])
        return [(c, _merge_ivs(ivs)) for c, ivs in lines]
    return collect(vraw), collect(hraw)


def _covers(ivs, a, b, tol=EPS):
    return any(lo - tol <= a and b <= hi + tol for lo, hi in ivs)


FRAME_MIN = 200.0   # сторона рамки обрамления короче — это полоса, не рамка


def detect_frames(vlines, hlines):
    """Рамки ОБРАМЛЕНИЯ АР (Образец 2, 15.07): замкнутые прямоугольники из
    цельных линий (2 верт + 2 гориз, стороны ≥ FRAME_MIN). Кандидатность
    (охват полос) проверяет вызывающий. → [(x0,y0,x1,y1)]."""
    frames = []
    for i in range(len(vlines)):
        xa, va = vlines[i]
        for j in range(i + 1, len(vlines)):
            xb, vb = vlines[j]
            if xb - xa < FRAME_MIN:
                continue
            for ya, ha in hlines:
                if not _covers(ha, xa, xb):
                    continue
                for yb, hb in hlines:
                    if yb - ya < FRAME_MIN or not _covers(hb, xa, xb):
                        continue
                    if _covers(va, ya, yb) and _covers(vb, ya, yb):
                        frames.append((xa, ya, xb, yb))
    return frames


def strips_from_segments(segments, wmin, wmax, skip_v=(), skip_h=()):
    """Э3-E (решение Дениса 13.07: закладывать заранее): сборка полос из
    ГОЛЫХ ОТРЕЗКОВ — АР, нарисованный линиями, а не вставками.
    Пара параллельных ЛИНИЙ (union сегментов каждой координаты — 15.07,
    Образец 1: раздробленный контур стойки собирается) на расстоянии
    wmin..wmax; каждый кусок пересечения union'ов длиннее max(ширина, 50) →
    полоса-прямоугольник (помечена "seg":1 — сегментное происхождение).
    skip_v/skip_h — координаты сторон рамок обрамления: пара, где ОБЕ линии
    рамочные, полосой не становится (фантом 90/160 мм из рамок Образца 2).
    Наклонные игнорируются; дубли (контур + осевые) сливаются."""
    vlines, hlines = axis_lines(segments)
    out = []

    def near_any(c, coords):
        return any(abs(c - q) <= 0.5 for q in coords)

    def pair_up(lines, skip, make_box):
        for i in range(len(lines)):
            a, aivs = lines[i]
            for j in range(i + 1, len(lines)):
                b, bivs = lines[j]
                w = b - a
                if w > wmax:
                    break                      # sorted: дальше только шире
                if w < wmin:
                    continue
                if near_any(a, skip) and near_any(b, skip):
                    continue                   # обе линии — стороны рамок
                for alo, ahi in aivs:
                    for blo, bhi in bivs:
                        lo, hi = max(alo, blo), min(ahi, bhi)
                        if hi - lo < max(w, 50.0):
                            continue           # перекрытие длиннее ширины
                        out.append(make_box(a, b, lo, hi))

    pair_up(vlines, skip_v,
            lambda a, b, lo, hi: {"x0": a, "y0": lo, "x1": b, "y1": hi, "seg": 1})
    pair_up(hlines, skip_h,
            lambda a, b, lo, hi: {"x0": lo, "y0": a, "x1": hi, "y1": b, "seg": 1})
    ded = []
    for s in out:
        for t in ded:
            if (abs(s["x0"] - t["x0"]) <= 1 and abs(s["y0"] - t["y0"]) <= 1 and
                    abs(s["x1"] - t["x1"]) <= 1 and abs(s["y1"] - t["y1"]) <= 1):
                break
        else:
            ded.append(s)
    return ded


# ─────────────── распознавание ───────────────

def recognize(req):
    blocks = req.get("blocks") or {}
    bs = blocks.get("stand") or {}
    br = blocks.get("rigel") or {}
    stand_name = bs.get("name")
    rigel_name = br.get("name")
    if not stand_name or not rigel_name:
        raise ValueError("blocks.stand.name и blocks.rigel.name обязательны (образцы)")
    body_w = float(bs.get("body_w") or 0.0)   # 0/None → вывести из ширин полос АР
    stand_prof = bs.get("prof") or stand_name
    rigel_prof = br.get("prof") or rigel_name
    # ПОВОРОТ ВСТАВКИ — С ОБРАЗЦА (фикс 14.07, фидбэк Алексея: ригели легли
    # с 270 вместо 0). Поворот — свойство ОПРЕДЕЛЕНИЯ блока: горизонтально
    # рисованный ригель (F50.02.03) вставляется с rot=0, вертикально
    # рисованный (17_06_01 Проба_штапики, F50.01.07 как верхний) — с rot=270.
    # Точка вставки инвариантна: (левый конец, ось) — доказано «Проба 3».
    stand_rot = float(bs.get("rot") or 0.0)
    rigel_rot = float(br.get("rot") or 0.0)
    # Э3-A (решение Дениса 13.07): опциональный образец ВЕРХНЕГО ригеля —
    # ставится на самой верхней отметке В СВЕТУ между телами стоек
    # (эталон «Проба 3»: Р31/Р33 из профиля стойки, 1660/395 при осевых 1710/445)
    bt = blocks.get("rigel_top") or {}
    top_name = bt.get("name")
    top_prof = bt.get("prof") or top_name
    top_rot = float(bt.get("rot") or 0.0)
    # заполнения и створки (просьба Алексея 17.07: «сразу расставлять»;
    # образцы приходят из единой выборки по слоям RF-заполнения/RF-створки)
    bf = blocks.get("fill") or {}
    fill_name = bf.get("name")
    fill_rot = float(bf.get("rot") or 0.0)
    fold = float(bf.get("fold", 15))
    min_fill = float(bf.get("min_fill", 50.0))
    bsash = blocks.get("sash") or {}
    sash_name = bsash.get("name")
    sash_rot = float(bsash.get("rot") or 0.0)

    params = req.get("params") or {}
    wmin = float(params.get("strip_w_min", 40.0))
    # wmax 120→210 (15.07, Образец 2: реальные Revit-импосты 150/200 мм).
    # Для полос ИЗ ОТРЕЗКОВ порог строже (120): bbox вставки — достоверная
    # деталь АР, а сегментная пара — гипотеза; на 150..210 голые линии
    # дверных проёмов Образца 1 дают ложные отметки (28986/29136 → «150»)
    wmax = float(params.get("strip_w_max", 210.0))
    seg_wmax = min(wmax, float(params.get("seg_w_max", 120.0)))
    # стем «двер» (15.07, Образец 2: «ADSK_Двери_Витражная…» — слово «Двери»
    # НЕ содержит подстроку «дверь»)
    door_pat = [p.lower() for p in (params.get("door_pat") or ["двер", "door"])]

    marks = req.get("marks") or {}
    m_stand = marks.get("stand", "С{n}")
    m_rigel = marks.get("rigel", "Р{n}")
    m_fill = marks.get("fill", "Сп{n}")
    m_sash = marks.get("sash", "Ств{n}")

    strips = list(req.get("strips") or [])
    notes = []
    segments = req.get("segments") or []
    frames = []
    if segments:
        # 1-й проход: полосы из всех линий; затем рамки ОБРАМЛЕНИЯ АР —
        # прямоугольники, охватывающие ≥50% центров полос (Образец 2:
        # контуры 3100×3470 и 2850×3334; грани полос самого витража
        # прямоугольники тоже образуют, но ничего не охватывают);
        # при кандидатах — 2-й проход БЕЗ пар «рамка×рамка» (фантомы 90/160)
        built = strips_from_segments(segments, wmin, seg_wmax)
        frames_all = detect_frames(*axis_lines(segments)) if built or strips else []
        if frames_all:
            # охват — по ВСЕМ полосам выбора: вставки + сегментные (Образец 2:
            # без вставок-импостов внутренняя рамка не набирала 50%)
            v0, h0, c0 = classify_strips(strips + built, wmin, wmax)
            allb = v0 + h0 + c0
            for f in frames_all:
                inside = sum(1 for b in allb
                             if f[0] - EPS <= (b[0] + b[2]) / 2 <= f[2] + EPS and
                                f[1] - EPS <= (b[1] + b[3]) / 2 <= f[3] + EPS)
                if allb and inside >= 0.5 * len(allb):
                    frames.append(f)
            if frames:
                skip_v = sorted({f[0] for f in frames} | {f[2] for f in frames})
                skip_h = sorted({f[1] for f in frames} | {f[3] for f in frames})
                built = strips_from_segments(segments, wmin, seg_wmax,
                                             skip_v, skip_h)
                notes.append("обрамление АР: %d рамк(и), каркас по Y %.1f..%.1f"
                             % (len(frames), max(f[1] for f in frames),
                                min(f[3] for f in frames)))
        if built:
            notes.append("полос собрано из отрезков: %d" % len(built))
        strips += built
    if not strips:
        raise ValueError("strips пуст — в выборе нет прямоугольной графики АР "
                         "(и из отрезков полосы не собрались)")
    vert, horz, cube = classify_strips(strips, wmin, wmax)
    chains = chain_verticals(vert, cube)
    if len(chains) < 2:
        # полос-импостов нет — панельный АР (Проба 2): сетка из зазоров.
        # Работаем ТОЛЬКО по синтез-полосам: посторонние полосы декора
        # (поручни ограждений шагом этажа) дают ложные отметки
        synth, pnotes = panel_grid(strips, wmin, wmax)
        if synth:
            notes.extend(pnotes)
            vert, horz, cube = classify_strips(synth, wmin, wmax)
            chains = chain_verticals(vert, cube)
    rails = group_rails(horz)

    # фантом-фильтр (15.07, Образец 1: дверная коробка/полотно и линии
    # выносок дают ложные вертикальные пары): цепочка, собранная ИЗ ОТРЕЗКОВ,
    # обязана нести ≥2 ЯКОРЯ — горизонтальные полосы рядом по X, которые
    # примыкают к торцу её куска (АР рвёт контур стойки на ригелях) или
    # проходят сквозь кусок; дверная коробка обрывается «в поле» — якорей 0
    if len(rails) >= 2:
        need = 2
        kept = []
        for c in chains:
            if c["from_seg"]:
                anchors = 0
                for hb in horz:
                    if hb[2] < c["x0"] - 2.0 or hb[0] > c["x1"] + 2.0:
                        continue               # полоса не рядом по X
                    ax = (hb[1] + hb[3]) / 2.0
                    touch = any(abs(p[0] - hb[3]) <= 2.0 or
                                abs(p[1] - hb[1]) <= 2.0 for p in c["pieces"])
                    inside = any(p[0] - EPS <= ax <= p[1] + EPS
                                 for p in c["pieces"])
                    if touch or inside:
                        anchors += 1
                if anchors < need:
                    notes.append("сегментная вертикаль X=%.1f отброшена: "
                                 "якорных отметок %d < %d (не стойка)"
                                 % ((c["x0"] + c["x1"]) / 2.0, anchors, need))
                    continue
            kept.append(c)
        chains = kept
    if len(chains) < 2:
        raise ValueError("распознано меньше двух вертикальных полос (стоек) — "
                         "витраж не собрать (вертикалей: %d)" % len(chains))

    # куски сегментных цепочек ЦЕЛИКОМ вне пояса горизонтальных полос —
    # хвосты размерных выносок (Образец 1), не стойка: отрезать
    if rails:
        h_lo = min(b[1] for b in horz)
        h_hi = max(b[3] for b in horz)
        for c in chains:
            if not c["from_seg"]:
                continue
            good = [p for p in c["pieces"] if p[1] > h_lo - EPS and p[0] < h_hi + EPS]
            if good and len(good) < len(c["pieces"]):
                notes.append("вертикаль X=%.1f: хвосты выносок отрезаны"
                             % ((c["x0"] + c["x1"]) / 2.0))
                c["pieces"] = good
                c["y0"], c["y1"] = good[0][0], good[-1][1]
                c["gaps"] = [(good[k][1], good[k + 1][0])
                             for k in range(len(good) - 1)]

    if body_w <= 0:
        # тело профиля = УНИФИКАЦИЯ Алексея (50 мм, конвенция v1); АР-полоса
        # УЖЕ 50 (лёгкая система) уменьшает. НЕ с образца: bbox реального
        # блока несёт обвес (крышки/штапики) — 183 при теле 50, давал «91 мм
        # наружу» в габарите и отступы ×3.7 (фидбэк Алексея 15.07b). НЕ голый
        # min АР-полос: Revit-импосты 70..200 условны (Образец 2, тело 50)
        body_w = min(50.0, min(c["x1"] - c["x0"] for c in chains))
        notes.append("тело профиля: %.1f мм (унификация 50 / АР-полосы)"
                     % body_w)
    for c in chains:
        for g0, g1 in c["gaps"]:
            notes.append("разрыв вертикали X=%.1f: %.1f..%.1f — слит"
                         % ((c["x0"] + c["x1"]) / 2.0, g0, g1))

    # типичная ширина ВНУТРЕННИХ полос — эталон «крайняя шире» (Образец 1)
    typ_inner = 0.0
    if len(chains) > 2:
        ws = sorted(c["x1"] - c["x0"] for c in chains[1:-1])
        typ_inner = ws[len(ws) // 2]
    axes = []
    for i, c in enumerate(chains):
        axes.append(stand_axis(c, body_w, i == 0, i == len(chains) - 1,
                               typ_inner))
    for i in range(1, len(axes)):
        if axes[i] - axes[i - 1] < body_w:
            raise ValueError("оси стоек слишком близко: %.1f и %.1f"
                             % (axes[i - 1], axes[i]))

    if not rails:
        notes.append("горизонтальных полос не найдено — только стойки")

    # габарит стоек: стойка тянется СКВОЗЬ обвязочные ригели до их внешних
    # граней (Образец 1: цепочки рвутся на ригелях и не доходят до габарита);
    # рамка обрамления, если есть, задаёт низ/верх ЖЁСТКО (Образец 2:
    # высотная отметка = низ внутренней рамки, стойки 3334, не 3470)
    if len(rails) >= 2:
        y_lo = min(min(c["y0"] for c in chains), h_lo)
        y_hi = max(max(c["y1"] for c in chains), h_hi)
        for c in chains:
            c["y0"], c["y1"] = y_lo, y_hi
    if frames:
        f_lo = max(f[1] for f in frames)
        f_hi = min(f[3] for f in frames)
        for c in chains:
            c["y0"] = max(c["y0"], f_lo)
            c["y1"] = min(c["y1"], f_hi)

    # тело РИГЕЛЯ — посадка обвязки (Проба 2: обвязки на ±26 = 52/2 тела
    # ригеля КП_45152-2 при теле стойки 53); не задано — тело стойки
    rb = float(br.get("body_w") or 0.0)
    if rb <= 0:
        rb = body_w

    # ТЕРМОРАЗРЫВ (сценарий Алексея 15.07c, витражи выше хлыста 6000):
    # ярусы стоек по кликам L1/L2 + зазор (Enter=5); стыки ярусов гасят
    # отметки своей зоны в ВЕРХНЮЮ обвязку нижнего яруса (Проба 2)
    tiers = None
    joints = []
    th = params.get("thermal") or {}
    if th.get("l1") is not None and th.get("l2") is not None:
        t_gap = float(th.get("gap") or 5.0)
        tiers = thermal_tiers(min(c["y0"] for c in chains),
                              max(c["y1"] for c in chains),
                              float(th["l1"]), float(th["l2"]), t_gap)
        joints = [(tiers[i][1], tiers[i + 1][0])
                  for i in range(len(tiers) - 1)]
        notes.append("терморазрыв %.0f: ярусов %d (%s)"
                     % (t_gap, len(tiers),
                        "/".join("%.0f" % (t - b) for b, t in tiers)))

    # ОБВЯЗКА «ПО ГАБАРИТАМ» (решение Алексея 15.07 на вопрос конвенции:
    # «по краю рамки ставить край блока, необходимые изменения потом
    # вручную»): отметка, чья полоса ДОСТИГАЕТ края габарита каркаса
    # (рамка обрамления или общий габарит стоек), ставится краем ТЕЛА
    # РИГЕЛЯ на край: ось = край ± rb/2. Покрывает и полосы, торчащие ЗА
    # рамку (Образец 2, низ на подставке), и полосы, доходящие до края
    # изнутри (Образец 2, верх). Отметки в поле (порог двери «Проба 3»,
    # середины) не трогаются — оси полос АР. При терморазрыве отметка в
    # зоне СТЫКА ярусов ложится верхней обвязкой нижнего яруса.
    # Совпавшие после клэмпа отметки сливаются. Старые ручные эталоны
    # Алексея местами ставили обвязку по оси полосы (Образец 1: ±15..25) —
    # по его решению расхождение допустимо, правится вручную.
    if rails and len(chains) >= 2:
        g_lo = min(c["y0"] for c in chains)
        g_hi = max(c["y1"] for c in chains)
        moved = []
        for r in rails:
            if r["lo"] <= g_lo + EPS and abs(r["y"] - (g_lo + rb / 2.0)) > EPS:
                moved.append((r["y"], g_lo + rb / 2.0))
                r["y"] = g_lo + rb / 2.0
            elif r["hi"] >= g_hi - EPS and abs(r["y"] - (g_hi - rb / 2.0)) > EPS:
                moved.append((r["y"], g_hi - rb / 2.0))
                r["y"] = g_hi - rb / 2.0
            else:
                for jt, jb in joints:
                    if jt - rb - EPS <= r["y"] <= jb + rb + EPS:
                        if abs(r["y"] - (jt - rb / 2.0)) > EPS:
                            moved.append((r["y"], jt - rb / 2.0))
                            r["y"] = jt - rb / 2.0
                        break
        if moved:
            notes.append("обвязка по краю габарита/стыка (правило 15.07): " +
                         ", ".join("%.1f→%.1f" % m for m in moved))
            merged = []
            for r in sorted(rails, key=lambda z: z["y"]):
                if merged and abs(merged[-1]["y"] - r["y"]) <= EPS:
                    merged[-1]["spans"] = sorted(merged[-1]["spans"] +
                                                 r["spans"])
                    merged[-1]["lo"] = min(merged[-1]["lo"], r["lo"])
                    merged[-1]["hi"] = max(merged[-1]["hi"], r["hi"])
                else:
                    merged.append(r)
            rails = merged

    # двери: панели с именем под паттерн
    doors = []
    for p in (req.get("panels") or []):
        nm = (p.get("name") or "").lower()
        if any(pat in nm for pat in door_pat):
            doors.append((float(p["x0"]), float(p["y0"]),
                          float(p["x1"]), float(p["y1"])))
    if doors:
        notes.append("дверей распознано: %d (пороги пропущены)" % len(doors))

    def door_blocks_rail(rail_y, a, b):
        """Порог: отметка от (низ двери − wmax) до верха двери, в пролёте
        [a,b], перекрытом дверью >50% света. Зона вниз расширена с 60 до
        wmax (15.07, Образец 2: дверь на подставке — полоса порога на 75
        ниже низа полотна)."""
        for dx0, dy0, dx1, dy1 in doors:
            lo, hi = max(a, dx0), min(b, dx1)
            if hi - lo < (b - a) * COVER_MIN:
                continue
            if dy0 - wmax - EPS <= rail_y <= dy1 + EPS:
                return True
        return False

    inserts = []

    def marker(tmpl):
        seen = {}
        def mk(key):
            if key not in seen:
                seen[key] = tmpl.replace("{n}", str(len(seen) + 1))
            return seen[key]
        return mk
    mk_stand, mk_rigel = marker(m_stand), marker(m_rigel)
    mk_fill, mk_sash = marker(m_fill), marker(m_sash)

    # стойки (с терморазрывом — ярусами: марки по типоразмерам сами
    # различат этажи разной высоты)
    for ax, c in zip(axes, chains):
        for b, t in (tiers if tiers else [(c["y0"], c["y1"])]):
            ln = t - b
            inserts.append({
                "kind": "stand", "block": stand_name, "layer": "RF-стойки",
                "x": round(ax, 4), "y": round(b, 4), "rot": stand_rot,
                "dyn": {"Длина": round(ln, 4)},
                "attrs": {"ИМЯ": mk_stand((stand_name, _rnd05(ln * 10))),
                          "ПРОФ": stand_prof, "ДЛИНА": fmt_len(ln)},
            })

    # ригели по отметкам × пролётам; верхняя отметка — опц. спец-образцом В СВЕТУ
    n_rig = 0
    skipped_doors = 0
    rail_hits = {}                # пролёт → отсортированные Y его ригелей
    top_y = rails[-1]["y"] if rails else None
    for r in rails:
        is_top = top_name and top_y is not None and abs(r["y"] - top_y) <= EPS
        for i in range(len(axes) - 1):
            a_in = axes[i] + body_w / 2.0       # свет пролёта
            b_in = axes[i + 1] - body_w / 2.0
            if not covered(r["spans"], a_in, b_in, COVER_MIN):
                continue
            if door_blocks_rail(r["y"], a_in, b_in):
                skipped_doors += 1
                continue
            rail_hits.setdefault(i, []).append(r["y"])
            if is_top:
                ln = b_in - a_in                 # в свету между телами
                inserts.append({
                    "kind": "rigel_top", "block": top_name, "layer": "RF-ригеля",
                    "x": round(a_in, 4), "y": round(r["y"], 4), "rot": top_rot,
                    "dyn": {"Длина": round(ln, 4)},
                    "attrs": {"ИМЯ": mk_rigel((top_name, _rnd05(ln * 10))),
                              "ПРОФ": top_prof, "ДЛИНА": fmt_len(ln)},
                })
            else:
                span = axes[i + 1] - axes[i]     # осевой шаг
                inserts.append({
                    "kind": "rigel", "block": rigel_name, "layer": "RF-ригеля",
                    "x": round(axes[i], 4), "y": round(r["y"], 4), "rot": rigel_rot,
                    "dyn": {"Длина": round(span, 4)},
                    "attrs": {"ИМЯ": mk_rigel((rigel_name, _rnd05(span * 10))),
                              "ПРОФ": rigel_prof, "ДЛИНА": fmt_len(span)},
                })
            n_rig += 1
    if top_name and rails:
        notes.append("верхняя отметка %.1f — ригель-образец «%s» в свету"
                     % (top_y, top_name))
    if skipped_doors:
        notes.append("порогов под дверями пропущено: %d" % skipped_doors)

    # ── заполнения и створки в СВЕТАХ ячеек (просьба Алексея 17.07) ──
    # Ячейка: пролёт (свет между телами стоек) × ряд (свет между телами
    # ФАКТИЧЕСКИХ ригелей пролёта; контракт Э1: cell_rows/size_attr, fold=15,
    # min_fill=50, точка вставки — левый низ света). При терморазрыве ряды
    # считаются В ПРЕДЕЛАХ ЯРУСА. Ячейка двери (панель под door_pat кроет
    # ≥50% света по X и Y) — ПРОПУСК: двери Алексей ставит сам (17.07).
    # Ячейка с панелью-«створкой» (sash_pat, центр в ячейке) — блок створки.
    n_fill = 0
    n_sash = 0
    if fill_name or sash_name:
        sash_pat = [p.lower() for p in
                    (params.get("sash_pat") or ["створк", "sash"])]
        sashes = []
        for p in (req.get("panels") or []):
            nm = (p.get("name") or "").lower()
            if any(pat in nm for pat in sash_pat):
                sashes.append(((float(p["x0"]) + float(p["x1"])) / 2.0,
                               (float(p["y0"]) + float(p["y1"])) / 2.0))
        cell_tiers = tiers if tiers else [(min(c["y0"] for c in chains),
                                           max(c["y1"] for c in chains))]
        for i in range(len(axes) - 1):
            a_in = axes[i] + body_w / 2.0
            b_in = axes[i + 1] - body_w / 2.0
            wc = b_in - a_in
            if wc < min_fill:
                continue
            ys = sorted(rail_hits.get(i, []))
            for tb, tt in cell_tiers:
                rails_in = [y for y in ys if tb + EPS < y < tt - EPS]
                for lo, hc in cell_rows(tb, tt, rails_in, rb, min_fill):
                    hit_door = any(
                        min(b_in, dx1) - max(a_in, dx0) >= wc * COVER_MIN and
                        min(lo + hc, dy1) - max(lo, dy0) >= hc * COVER_MIN
                        for dx0, dy0, dx1, dy1 in doors)
                    if hit_door:
                        continue
                    is_sash = sash_name and any(
                        a_in - EPS <= sx <= b_in + EPS and
                        lo - EPS <= sy <= lo + hc + EPS for sx, sy in sashes)
                    if is_sash:
                        sa = size_attr(wc, hc, fold)
                        inserts.append({
                            "kind": "sash", "block": sash_name,
                            "layer": "RF-створки",
                            "x": round(a_in, 4), "y": round(lo, 4),
                            "rot": sash_rot,
                            "dyn": {"Ширина": round(wc, 4),
                                    "Высота": round(hc, 4)},
                            "attrs": {"МАРКИРОВКА": mk_sash(sa),
                                      "РАЗМЕР_ЗАП": sa},
                        })
                        n_sash += 1
                    elif fill_name:
                        sa = size_attr(wc, hc, fold)
                        inserts.append({
                            "kind": "fill", "block": fill_name,
                            "layer": "RF-заполнения",
                            "x": round(a_in, 4), "y": round(lo, 4),
                            "rot": fill_rot,
                            "dyn": {"Ширина": round(wc, 4),
                                    "Высота": round(hc, 4)},
                            "attrs": {"МАРКИРОВКА": mk_fill(sa),
                                      "РАЗМЕР_ЗАП": sa},
                        })
                        n_fill += 1
        if n_fill or n_sash:
            notes.append("заполнений: %d, створок: %d (двери пропущены)"
                         % (n_fill, n_sash))

    # размеры (фидбэк Алексея 14.07): габаритные + межосевые цепочки;
    # вертикальная — по осям ФАКТИЧЕСКИ ПОСТАВЛЕННЫХ ригелей (порог двери
    # в цепочку не попадает), от габарита до габарита
    dims = []
    if params.get("dims", True):
        y_lo = min(c["y0"] for c in chains)
        y_hi = max(c["y1"] for c in chains)
        rail_used = sorted({round(i["y"], 1) for i in inserts
                            if i["kind"] in ("rigel", "rigel_top")})
        off = max(150.0, 4 * body_w)   # отступ вдвое меньше (фидбэк Алексея 15.07b)
        dims = build_dims(axes, body_w, y_lo, y_hi, rail_used, off)

    return {
        "ok": True,
        "inserts": inserts,
        "dims": dims,
        "summary": {"stands": len(axes) * (len(tiers) if tiers else 1),
                    "rigels": n_rig,
                    "fills": n_fill, "sashes": n_sash,
                    "rails": len(rails),
                    "axes_x": [round(a, 2) for a in axes]},
        "notes": notes,
    }


# ─────────────── CLI (тот же контракт, что vitrage_plan) ───────────────

def main(argv):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    if len(argv) < 2:
        print(json.dumps({"ok": False,
                          "error": "usage: vitrage_recognize.py <req.json|-> [out.json]"},
                         ensure_ascii=False))
        return 2
    try:
        if argv[1] == "-":
            req = json.load(sys.stdin)
        else:
            with open(argv[1], "r", encoding="utf-8-sig") as f:
                req = json.load(f)
        plan = recognize(req)
    except ValueError as ex:
        plan = {"ok": False, "error": str(ex)}
    except Exception as ex:  # noqa
        plan = {"ok": False, "error": "%s: %s" % (type(ex).__name__, ex)}
    text = json.dumps(plan, ensure_ascii=False, indent=1)
    if len(argv) > 2:
        with open(argv[2], "w", encoding="utf-8") as f:
            f.write(text)
        print("ok" if plan.get("ok") else "error: %s" % plan.get("error"))
    else:
        print(text)
    return 0 if plan.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
