# -*- coding: utf-8 -*-
"""Раскладка облицовки универсальным блоком в контурах (модуль AClad).

Правила — ТЗ Германа 22.07 (пп.1.1–1.6, AClad/docs/CLADDING.md):
- 1.1: точка старта (origin.y = низ первого ряда), снизу вверх; ниже
  не кладём (В1);
- 1.2/1.3: пролёт между швами (простенок, участок над/под проёмом) —
  блок целых ПО ЦЕНТРУ, раскладка в обе стороны; крайний кусок < 300
  — смещение на половину плиты (_seg_centered). ЗАМЕНЯЕТ прежний В5;
- 1.5/П4: вокруг проёмов ШОВ на величину руста со всех сторон
  (слева/справа gv — анкерные швы на всю высоту, В2; сверху/снизу gh
  — probe-отступы); камни к проёму не прилипают;
- 1.6: сегмент у края контура (внешний угол) — целыми ОТ УГЛА,
  подрезка у шва проёма (_seg_from_corner);
- 2.5: vjoints — юзер-точки, через которые точно проходит
  вертикальный руст (ось шва по точке, на всю высоту);
- В4: min_cut = 150 мм (дефолт); меньшие куски — note.

Ответы Германа Г1–Г3 (22.07, вторая итерация):
- Г1: подрезка у окна от угла < 300 — плитка перед ней в половину
  (w/2), подрезка увеличивается до rem+w/2 (_seg_from_corner);
- Г2: плитка НЕ шире проёма — над/под узким окном камень уменьшается
  до ширины окна (даёт _seg_centered при L ≤ w);
- Г3 (= 1.4/2.6): hjoints — точки горизонтальных рустов, действуют
  на ВЕСЬ участок: точка = НИЗ руста [py, py+gh]; снизу облицовка
  приходит к русту с подрезкой, выше руста — панели стандартной
  высоты (пояса с новой фазой рядов). Клик по верху окна = руст над
  окном (п.1.4: привязка к верху проёма).

Фидбэк Германа 23.07 (первый живой прогон, скрины):
- ряды режутся по высоте ТОЛЬКО рёбрами ВНЕШНЕГО контура (ступени,
  верх). Низ/верх проёма НЕ режет соседние столбцы: слева/справа от
  окна камни идут целыми рядами, подрезка по высоте — только у
  столбцов над/под самим проёмом (Y-вычитание bbox проёма ± gh из
  камня, чей X-диапазон перекрыт проёмом). Прежняя глобальная резка
  давала «двойные» низкие плитки сбоку окон и, при остатке < min_cut,
  пустую полосу через весь фасад;
- юзер-точка вертикального руста, кликнутая в грань/угол проёма,
  СХЛОПЫВАЕТСЯ с авто-швом этой грани (иначе ось-шов [px±gv/2] и
  анкер [грань, грань+gv] дают сдвоенный руст 1.5·gv и несоосность
  кромок на gv/2 — «раздвинутый руст» и отступ 4 мм на скринах).

Прежний В5 (20.07) и режимы «Проемы/Край» диалога упразднены новым
ТЗ; mode="edge" сохранён в движке как запасной.

Вход/выход — JSON-словари (как vitrage_*): контуры — ортогональные
замкнутые полилинии (outer) с дырами-проёмами (holes), глобальная
сетка рядов от датума, два режима раскладки («edge» — от левого края,
«openings» — от границ проёмов). Только stdlib (движок замораживается
PyInstaller).
"""

EPS = 1e-6


def _closed(pts):
    """Замкнутый список вершин [[x,y],...] без дубля последней точки."""
    p = [(float(a), float(b)) for a, b in pts]
    if len(p) >= 2 and abs(p[0][0] - p[-1][0]) < EPS and \
       abs(p[0][1] - p[-1][1]) < EPS:
        p = p[:-1]
    return p


def _edges(poly):
    """Рёбра замкнутого полигона парами вершин."""
    n = len(poly)
    return [(poly[i], poly[(i + 1) % n]) for i in range(n)]


def _is_ortho(poly):
    return all(abs(a[0] - b[0]) < EPS or abs(a[1] - b[1]) < EPS
               for a, b in _edges(poly))


def _xs_at(polys, y):
    """X-пересечения горизонтали y с вертикальными рёбрами полигонов
    (чётно-нечётное правило) → отсортированные интервалы внутренности."""
    xs = []
    for poly in polys:
        for a, b in _edges(poly):
            if abs(a[0] - b[0]) < EPS:                  # вертикальное ребро
                y0, y1 = sorted((a[1], b[1]))
                # полуоткрытое [y0, y1): нижний конец включён — вершина-
                # стык двух рёбер не считается дважды
                if y0 - EPS <= y < y1 - EPS:
                    xs.append(a[0])
    xs.sort()
    return [(xs[i], xs[i + 1]) for i in range(0, len(xs) - 1, 2)]


def _hys_in(polys, y0, y1):
    """Y горизонтальных рёбер полигонов строго внутри (y0, y1)."""
    ys = set()
    for poly in polys:
        for a, b in _edges(poly):
            if abs(a[1] - b[1]) < EPS and y0 + EPS < a[1] < y1 - EPS:
                ys.add(a[1])
    return sorted(ys)


def _isect(iv1, iv2):
    """Пересечение двух списков интервалов."""
    out = []
    for a1, b1 in iv1:
        for a2, b2 in iv2:
            a, b = max(a1, a2), min(b1, b2)
            if b - a > EPS:
                out.append((a, b))
    out.sort()
    return out


def strip_bands(polys, y0, y1):
    """Полоса ряда [y0,y1] → подполосы [(sy0, sy1, [интервалы X])].

    Горизонтальные рёбра внутри полосы режут её на подполосы; в каждой
    X-интервалы постоянны (сэмпл в середине). Пустые подполосы
    опускаются. Ступени контура/верх ряда дают подполосы НИЖЕ высоты
    камня — подрезка по высоте получается сама (CLADDING.md §Алгоритм).

    С 23.07 (фидбэк Германа) сюда передаётся ТОЛЬКО внешний контур:
    рёбра проёмов полосу НЕ режут — влияние проёмов локально по X
    (Y-вычитание в cladding_plan), боковые столбцы остаются целыми."""
    cuts = [y0] + _hys_in(polys, y0, y1) + [y1]
    bands = []
    for i in range(len(cuts) - 1):
        a, b = cuts[i], cuts[i + 1]
        if b - a < EPS:
            continue
        iv = _xs_at(polys, (a + b) / 2.0)
        if iv:
            bands.append((a, b, iv))
    return bands


def _hole_joints(holes, gv, y0=None, y1=None):
    """Вертикальные ШВЫ вокруг проёмов (фидбэк Германа 21.07 п.4:
    отступ от внутренних областей на величину руста): у левой грани A
    проёма шов [A-gv, A] (снаружи), у правой B — [B, B+gv]. y0/y1
    заданы — только проёмы, пересекающие полосу (режим edge); без них
    — все проёмы контура (режим openings: В2 — швы от границ проёмов
    действуют на всю высоту)."""
    js = []
    for h in holes:
        if y0 is not None:
            hy0 = min(p[1] for p in h)
            hy1 = max(p[1] for p in h)
            if min(hy1, y1) - max(hy0, y0) <= EPS:
                continue
        A = min(p[0] for p in h)
        B = max(p[0] for p in h)
        js.append((A - gv, A))
        js.append((B, B + gv))
    return js


def _cut_spans(a, b, joints):
    """Интервал [a,b] минус швы → пролёты [s0, s1, la, ra]; la/ra=True
    — край рождён швом (якорный: камень кладётся впритык к шву)."""
    spans = [[a, b, False, False]]
    for j0, j1 in joints:
        nxt = []
        for s0, s1, la, ra in spans:
            c0, c1 = max(s0, j0), min(s1, j1)
            if c1 - c0 <= EPS:
                nxt.append([s0, s1, la, ra])
                continue
            if c0 - s0 > EPS:
                nxt.append([s0, c0, la, True])
            if s1 - c1 > EPS:
                nxt.append([c1, s1, True, ra])
        spans = nxt
    return spans


def _pt_in_poly(poly, x, y):
    """Чёт-нечет лучом вправо (используется clad_engine для
    группировки контуров); касания вершин не критичны."""
    n = len(poly)
    inside = False
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if (y1 > y) != (y2 > y):
            xi = x1 + (y - y1) * (x2 - x1) / (y2 - y1)
            if xi > x:
                inside = not inside
    return inside


def _bbox(poly):
    xs = [p[0] for p in poly]
    ys = [p[1] for p in poly]
    return min(xs), min(ys), max(xs), max(ys)


def _sub_y(spans, lo, hi):
    """Вычесть [lo, hi] из списка Y-интервалов [(a0, a1), ...]."""
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


def _put(out, notes, x, wc, min_cut):
    if wc <= EPS:
        return
    if wc >= min_cut:
        out.append((x, wc))
    else:
        notes.append("подрезка %.0f < min_cut" % wc)


def _dedup_notes(notes):
    """Одинаковые заметки схлопываются со счётчиком «(×N)» — при
    min_cut=150 однотипных пропусков может быть много."""
    count, order = {}, []
    for n in notes:
        if n in count:
            count[n] += 1
        else:
            count[n] = 1
            order.append(n)
    return [n if count[n] == 1 else "%s (×%d)" % (n, count[n])
            for n in order]


def _row_edge(a, b, x_min, w, gv, min_cut, notes):
    """Режим «от края»: модульная сетка от x_min (левый край КОНТУРА —
    швы соосны по всей высоте); каждый столбец клипуется интервалом
    [a,b] — у проёма и у правого края вертикальный ряд подрезается."""
    out = []
    step = w + gv
    j = int((a - x_min + EPS) // step)
    while True:
        xj = x_min + j * step
        if xj >= b - EPS:
            break
        x0, x1 = max(xj, a), min(xj + w, b)
        _put(out, notes, x0, x1 - x0, min_cut)
        j += 1
    return out


# ТЗ Германа 22.07 п.1.2/1.3: краевая подрезка центрированного пролёта
# меньше 300 мм — «смещение на половину плиты» (блок целых на один
# меньше, чётность меняется, куски вырастают на полшага).
CENTER_SHIFT_MIN = 300.0


def _seg_centered(out, notes, a, b, w, gv, min_cut):
    """Пролёт между швами (простенок, участок над/под проёмом) —
    ТЗ Германа 22.07 п.1.2/1.3: блок целых ЦЕНТРИРОВАН («целая плитка
    по центру, раскладка в обе стороны от центра»), по краям два
    РАВНЫХ подрезных; крайний кусок < 300 — смещение на полплиты.

    ЗАМЕНЯЕТ прежнее правило В5 от 20.07 (целые от проёмов навстречу,
    остаток в середине) — новое ТЗ 22.07 главнее."""
    step = w + gv
    L = b - a
    if L <= w + EPS:
        # пролёт не шире камня — один камень на весь пролёт
        _put(out, notes, a, min(L, w), min_cut)
        return
    k = int((L - gv - EPS) // step)          # максимум целых с кусками > 0
    e = (L - k * step - gv) / 2.0
    if k >= 1 and EPS < e < CENTER_SHIFT_MIN - EPS:
        k -= 1                                # смещение на половину плиты
        e = (L - k * step - gv) / 2.0
    if k < 1:
        e = (L - gv) / 2.0                    # узкий пролёт: два куска
        _put(out, notes, a, e, min_cut)
        _put(out, notes, b - e, e, min_cut)
        return
    blk = k * step - gv
    x0 = a + (L - blk) / 2.0                  # блок целых по центру
    e = x0 - gv - a
    if e > EPS:
        _put(out, notes, a, e, min_cut)
        _put(out, notes, b - e, e, min_cut)
    for i in range(k):
        out.append((x0 + i * step, w))


def _seg_from_corner(out, notes, a, b, w, gv, min_cut, corner_left):
    """Сегмент между краем контура и швом проёма — ТЗ 22.07 п.1.6:
    от ВНЕШНЕГО УГЛА (края контура) ЦЕЛЫМИ, подрезка у шва проёма.

    Г1 (ответ Германа 22.07): подрезка у окна «маленькая» (<300) —
    плитка ПЕРЕД подрезкой делается в половину (w/2), а подрезка
    увеличивается на освободившееся (rem + w/2): оба куска ≥300 при
    600-м камне, дыр у окна не бывает."""
    step = w + gv
    L = b - a
    n = int((L + gv + EPS) // step)
    rem = L - n * step
    half = n >= 1 and EPS < rem < CENTER_SHIFT_MIN - EPS
    n_full = n - 1 if half else n
    for k in range(n_full):
        out.append(((a + k * step) if corner_left
                    else (b - w - k * step), w))
    if half:
        if corner_left:
            x_half = a + n_full * step
            _put(out, notes, x_half, w / 2.0, min_cut)
            _put(out, notes, x_half + w / 2.0 + gv, rem + w / 2.0,
                 min_cut)
        else:
            r_half = b - n_full * step        # правый край полуплитки
            _put(out, notes, r_half - w / 2.0, w / 2.0, min_cut)
            _put(out, notes, a, rem + w / 2.0, min_cut)
    elif rem > EPS:
        _put(out, notes, (a + n * step) if corner_left else a,
             min(rem, w), min_cut)


def _dead_span(s0, s1, dead_x):
    """Пролёт целиком внутри проёма, накрывающего полосу по высоте —
    камни там не нужны (и не должны шуметь notes о подрезках)."""
    return any(s0 >= dx0 - EPS and s1 <= dx1 + EPS for dx0, dx1 in dead_x)


def _row_openings(a, b, joints, ox, w, gv, min_cut, notes, dead_x=()):
    """Основной режим (ТЗ Германа 22.07): интервал минус анкерные ШВЫ
    (грани проёмов на всю высоту — В2; шов снаружи проёма — 1.5/П4;
    юзер-точки вертикальных рустов — 2.5) → пролёты. Пролёт между
    двумя швами — центрированная раскладка (1.2/1.3: целая по центру,
    в обе стороны, кусок <300 — сдвиг на полплиты); шов с одной
    стороны — целые ОТ УГЛА (края контура, 1.6), подрезка у шва;
    пролёт без швов — модульная сетка от левого края (угла) либо от
    origin.x, если задан. Пролёты внутри проёмов (dead_x) — пропуск."""
    out = []
    for s0, s1, la, ra in _cut_spans(a, b, joints):
        if _dead_span(s0, s1, dead_x):
            continue
        if la and ra:
            _seg_centered(out, notes, s0, s1, w, gv, min_cut)
        elif la or ra:
            _seg_from_corner(out, notes, s0, s1, w, gv, min_cut,
                             corner_left=not la)
        else:
            out.extend(_row_edge(s0, s1, s0 if ox is None else ox,
                                 w, gv, min_cut, notes))
    out.sort()
    return out


def cladding_plan(req):
    """Главный вход: req (CLADDING.md §Модель данных) → план вставок."""
    tile = req.get("tile") or {}
    w = float(tile.get("w", 600.0))
    h = float(tile.get("h", 600.0))
    gap = req.get("gap") or {}
    gv = float(gap.get("v", 0.0))
    gh = float(gap.get("h", 0.0))
    # П3 (21.07): точка привязки сетки рустов. origin.y — низ первого
    # ряда (фаза рядов И отметка старта, В1: ниже не кладём); origin.x
    # — вертикальная грань камня (правая грань верт. руста): модульная
    # сетка столбцов в режиме edge и в пролётах без проёмов. Нет
    # origin.x — сетка от левого края контура (старое поведение);
    # datum поддержан как синоним origin.y.
    origin = req.get("origin") or {}
    datum = float(origin.get("y", req.get("datum", 0.0)))
    ox = origin.get("x")
    if ox is not None:
        ox = float(ox)
    # дефолт — основной режим по ТЗ 22.07 (центрирование/от угла);
    # "edge" сохранён как запасной (модульная сетка на всю высоту)
    mode = (req.get("mode") or "openings").strip().lower()
    # В4 (Герман, 20.07): минимальная подрезка 150 мм
    min_cut = float(req.get("min_cut", 150.0))
    # ТЗ 2.5: точки, через которые ТОЧНО проходит вертикальный руст —
    # ось шва по точке; действуют на всю высоту всех контуров
    user_joints = []
    for px in req.get("vjoints") or []:
        try:
            px = float(px)
        except (TypeError, ValueError):
            continue
        user_joints.append((px - gv / 2.0, px + gv / 2.0))
    # ТЗ 2.6 + Г3 (ответ Германа 22.07): точки горизонтальных рустов —
    # действуют на ВЕСЬ участок: точка = НИЗ руста [py, py+gh]; снизу
    # облицовка приходит к русту С ПОДРЕЗКОЙ, выше руста — панели
    # стандартной высоты (новая фаза рядов от py+gh). Клик по верху
    # окна даёт руст сразу над окном — п.1.4 (привязка к верху проёма)
    hjoints = []
    for py in req.get("hjoints") or []:
        try:
            hjoints.append(float(py))
        except (TypeError, ValueError):
            continue
    hjoints = sorted(set(hjoints))
    notes, inserts = [], []
    if w < EPS or h < EPS:
        return {"ok": False, "error": "нулевой размер камня"}
    n_rows = 0
    joint_axes = set()   # оси вертикальных швов у граней проёмов/юзер-
                         # рустов — мост к этапу 3 (AFrame, 24.07)
    for ci, c in enumerate(req.get("contours") or []):
        outer = _closed(c.get("outer") or [])
        if len(outer) < 4:
            notes.append("контур %d: меньше 4 вершин — пропуск" % (ci + 1))
            continue
        holes = [_closed(hh) for hh in (c.get("holes") or [])]
        polys = [outer] + holes
        bad = [p for p in polys if not _is_ortho(p)]
        if bad:
            notes.append("контур %d: неортогональные рёбра — пропуск"
                         % (ci + 1))
            continue
        y_lo = min(p[1] for p in outer)
        y_hi = max(p[1] for p in outer)
        x_min = min(p[0] for p in outer)
        if y_lo < datum - EPS:
            notes.append("контур %d ниже отметки старта на %.0f мм — "
                         "ниже не облицовывается" % (ci + 1, datum - y_lo))
        # анкерные швы проёмов — на ВЕСЬ контур (В2: сквозные по
        # высоте) + юзер-точки вертикальных рустов (2.5). Юзер-руст,
        # кликнутый в грань/угол проёма, схлопывается с авто-швом
        # грани (23.07: двойной шов давал руст 1.5·gv и несоосность
        # кромок соседних поясов на gv/2)
        hole_js = _hole_joints(holes, gv)
        uj = [j for j in user_joints
              if not any(j[0] < k[1] + EPS and k[0] < j[1] + EPS
                         for k in hole_js)]
        joints_all = (hole_js + uj) if mode == "openings" else []
        for j0, j1 in (hole_js + (uj if mode == "openings" else [])):
            joint_axes.add(round((j0 + j1) / 2.0, 2))
        hole_boxes = [_bbox(hh) for hh in holes]
        x_phase = x_min if ox is None else ox
        # пояса по точкам горизонтальных рустов (Г3): нижний — от
        # origin.y (общий горизонт), каждый следующий — от py + gh
        # (панели стандартной высоты над рустом); к русту снизу
        # облицовка приходит с подрезкой (клип пояса на py)
        belts = []
        start = datum
        for py in hjoints:
            if start + EPS < py < y_hi - EPS:
                belts.append((start, py))
                start = py + gh
        belts.append((start, y_hi))
        for b_lo, b_hi in belts:
            i = 0
            while True:
                y = b_lo + i * (h + gh)
                if y >= b_hi - EPS:
                    break
                i += 1
                # подполосы — ТОЛЬКО по внешнему контуру (ступени,
                # верх); проёмы полосу не режут (23.07: иначе боковые
                # столбцы дробились низом/верхом окна, а подполоса
                # < min_cut выпадала ПОЛОСОЙ через весь фасад)
                for sy0, sy1, ivs in strip_bands([outer], y,
                                                 min(y + h, b_hi)):
                    hc = sy1 - sy0
                    # В12 (ответ Дениса 24.07): мелкие куски ПО ВЫСОТЕ
                    # ставим (принцип раскладки; тонкую полоску позже
                    # прикроет отлив) — min_cut остаётся порогом только
                    # по ШИРИНЕ (В4). Note — информативная.
                    if hc < min_cut:
                        notes.append("контур %d: мелкий ряд на отм. "
                                     "%.0f высотой %.0f мм"
                                     % (ci + 1, sy0, hc))
                    # проёмы, накрывающие полосу по высоте (с рустами):
                    # их X-диапазон мёртв для этой полосы целиком
                    dead_x = [(bx0, bx1) for bx0, by0, bx1, by1
                              in hole_boxes
                              if by0 - gh <= sy0 + EPS and
                              sy1 <= by1 + gh + EPS]
                    # в edge отступ-шов только у проёмов ЭТОЙ полосы
                    # (П4); в openings швы уже глобальные (В2)
                    joints = joints_all if mode == "openings" else \
                        _hole_joints(holes, gv, sy0, sy1)
                    for a, b in ivs:
                        if mode == "openings":
                            row = _row_openings(a, b, joints, ox, w, gv,
                                                min_cut, notes, dead_x)
                        else:
                            row = []
                            for s0, s1, _la, _ra in \
                                    _cut_spans(a, b, joints):
                                if _dead_span(s0, s1, dead_x):
                                    continue
                                row.extend(_row_edge(s0, s1, x_phase, w,
                                                     gv, min_cut, notes))
                        for x, wc in row:
                            # проёмы, перекрывающие камень по X,
                            # вычитаются из его высоты С РУСТАМИ
                            # (гориз. руст сверху/снизу проёма — П4);
                            # столбцы мимо проёма не трогаются
                            yspans = [(sy0, sy1)]
                            for bx0, by0, bx1, by1 in hole_boxes:
                                if min(bx1, x + wc) - max(bx0, x) \
                                        <= EPS:
                                    continue
                                yspans = _sub_y(yspans, by0 - gh,
                                                by1 + gh)
                            for y0c, y1c in yspans:
                                hc2 = y1c - y0c
                                if hc2 <= EPS:
                                    continue
                                # В12: мелкий остаток под/над проёмом
                                # СТАВИМ (note информативная)
                                if hc2 < min_cut:
                                    notes.append("мелкая подрезка по "
                                                 "высоте %.0f мм у "
                                                 "проёма" % hc2)
                                inserts.append({
                                    "x": round(x, 4),
                                    "y": round(y0c, 4),
                                    "w": round(wc, 4),
                                    "h": round(hc2, 4)})
                n_rows += 1
    full = sum(1 for t in inserts
               if abs(t["w"] - w) < EPS and abs(t["h"] - h) < EPS)
    # мост к этапу 3 (AFrame): оси вертикальных швов (стойки
    # подсистемы) и центры горизонтальных (кляммеры) — из фактических
    # стыков камней + граней проёмов/юзер-рустов
    jx = set(joint_axes)
    lefts = set(round(t["x"], 2) for t in inserts)
    lows = set(round(t["y"], 2) for t in inserts)
    ry = set()
    for t in inserts:
        xr = round(t["x"] + t["w"], 2)
        if round(xr + gv, 2) in lefts:
            jx.add(round(xr + gv / 2.0, 2))
        yt = round(t["y"] + t["h"], 2)
        if round(yt + gh, 2) in lows:
            ry.add(round(yt + gh / 2.0, 2))
    return {"ok": True, "inserts": inserts, "notes": _dedup_notes(notes),
            "joints_x": sorted(jx), "rows_y": sorted(ry),
            "summary": {"tiles": len(inserts), "full": full,
                        "cut": len(inserts) - full, "rows": n_rows}}
