"""Угловая геометрия по уточнению Германа 15.10.2026.

Это размещение прямоугольника по ТЗ, не расчёт сечения, креплений или
несущей способности швеллера. Вынос облицовки не заменяет плечо расчёта.
"""
import math

EPS = 1e-6
PICK_TOL = 1.0


def validate_request(req):
    """Новый протокол только с явно переданными XY; старые requests совместимы."""
    if 'external_corners' not in req or req.get('parts') == 'clamps':
        return None, [], None
    points = req.get('external_corners')
    if not isinstance(points, list):
        return None, [], 'external_corners должен быть списком точек X/Y.'
    if not points:
        return None, [], None
    try:
        offset = float(req['cladding_offset'])
        if isinstance(req['cladding_offset'], bool) or not math.isfinite(offset) or offset < 0:
            raise ValueError()
        parsed = []
        for point in points:
            x, y = float(point['x']), float(point['y'])
            if not math.isfinite(x) or not math.isfinite(y):
                raise ValueError()
            if (x, y) not in parsed:
                parsed.append((x, y))
    except (KeyError, TypeError, ValueError, OverflowError):
        return None, [], ('Вынос облицовки cladding_offset должен быть конечным числом '
                          'не меньше нуля; внешние углы — конечными координатами X/Y.')
    return offset, parsed, None


def selected_edges(poly, points, offset):
    """Точка выбирает вертикальную грань outer, направление — внутренняя нормаль.

    Нормаль не зависит от ближайшего bbox-края. Перенос начала координат перед
    вычислением площади исключает потерю знака на геодезических координатах.
    """
    if offset is None or not points:
        return []
    ox, oy = poly[0]
    signed = sum((a[0]-ox)*(b[1]-oy)-(b[0]-ox)*(a[1]-oy)
                 for a, b in zip(poly, poly[1:]+poly[:1]))
    ccw = signed > 0
    found = {}
    for a, b in zip(poly, poly[1:]+poly[:1]):
        if abs(a[0]-b[0]) > EPS or abs(a[1]-b[1]) <= EPS:
            continue
        x, lo, hi = a[0], min(a[1], b[1]), max(a[1], b[1])
        if not any(abs(px-x) <= PICK_TOL and lo-PICK_TOL <= py <= hi+PICK_TOL
                   for px, py in points):
            continue
        direction = (-1 if b[1] > a[1] else 1) * (1 if ccw else -1)
        key = (round(x,4), round(lo,4), round(hi,4), direction)
        found[key] = dict(corner_x=x, inner_x=x+direction*(offset+100),
                          y0=lo, y1=hi, direction=direction, offset=offset,
                          side='left' if direction > 0 else 'right')
    return list(found.values())


def subtract_spans(spans, lo, hi):
    return [(a, b) for s, e in spans for a, b in
            ((s, min(e, lo)), (max(s, hi), e)) if b-a > EPS]


def reserve_vertical(spans, x, edges):
    """Между первым и вторым рядом старые оси не остаются; вне грани неизменны."""
    for edge in edges:
        distance = (x-edge['corner_x'])*edge['direction']
        required_here = any(min(abs(x-e['corner_x']),abs(x-e['inner_x'])) <= EPS
                            and min(edge['y1'],e['y1']) > max(edge['y0'],e['y0'])+EPS for e in edges)
        if EPS < distance < edge['offset']+100-EPS and not required_here:
            spans = subtract_spans(spans, edge['y0'], edge['y1'])
    return spans


def new_axis_spans(x, edges, wall_spans):
    """Добавленная ось ограничена выбранной гранью, а не всем bbox стены."""
    out = []
    for edge in edges:
        if min(abs(x-edge['corner_x']), abs(x-edge['inner_x'])) > EPS:
            continue
        for lo, hi in wall_spans:
            a, b = max(lo, edge['y0']), min(hi, edge['y1'])
            if b-a > EPS:
                out.append((a,b))
    return out


def horizontal_positions(x0, x1, y, edges, margin, czone, main, corner,
                         exact, legacy):
    """Первые оси L+100; шаг от выбранного угла, при правом — зеркально.

    calc распределяет внутри каждого интервала с шагом <= предела;
    manual сохраняет указанный шаг и один конечный добор. При двух углах
    первая/последняя оси закреплены, отсчёт последовательности слева.
    """
    active = [e for e in edges if e['y0']-EPS <= y <= e['y1']+EPS]
    left = [e for e in active if e['direction'] > 0 and abs(e['corner_x']-x0) <= PICK_TOL]
    right = [e for e in active if e['direction'] < 0 and abs(e['corner_x']-x1) <= PICK_TOL]
    if not left and not right:
        return legacy()
    a = max(e['inner_x'] for e in left) if left else x0+margin
    b = min(e['inner_x'] for e in right) if right else x1-margin
    if a > b+EPS:
        return []  # Узкий простенок: два заданных отступа не помещаются.
    if b-a <= EPS:
        return [a]
    reverse = bool(right and not left)
    transform = (lambda x: x0+x1-x) if reverse else (lambda x:x)
    lo, hi = (transform(b), transform(a)) if reverse else (a,b)
    intervals = []
    for edge in active:
        ends = [edge['corner_x'], edge['corner_x']+edge['direction']*czone]
        ends = sorted(transform(x) for x in ends)
        intervals.append((ends[0], ends[1]))
    bounds = sorted(set([lo,hi]+[max(lo,min(hi,v)) for pair in intervals for v in pair]))
    out = []
    for start,end in zip(bounds,bounds[1:]):
        if end-start <= EPS:
            continue
        midpoint = (start+end)/2
        step = corner if any(s-EPS <= midpoint <= e+EPS for s,e in intervals) else main
        if exact:
            # Do not restart at zone boundaries: only switch the limit at an axis.
            continue
        count = max(1,int(math.ceil((end-start-EPS)/step)))
        out += [start+(end-start)*i/count for i in range(count)]
    if exact:
        cursor = lo
        while cursor < hi-EPS:
            out.append(cursor)
            step = corner if any(s-EPS <= cursor < e-EPS for s,e in intervals) else main
            # A step entering a corner zone obeys its smaller limit as well.
            if any(cursor < e-EPS and cursor+step > s+EPS for s,e in intervals):
                step = min(step,corner)
            cursor += step
    out.append(hi)
    return sorted(set(round(transform(x),4) for x in out))


def in_reserved_horizontal(x, y, edges):
    return any(e['y0']-EPS <= y <= e['y1']+EPS and
               -EPS <= (x-e['corner_x'])*e['direction'] < e['offset']+100-EPS
               for e in edges)


def rectangle_in_wall(x0,x1,y0,y1,poly,holes,hspans):
    """Прямоугольник целиком в стене: сечения во всех полосах вершин."""
    if y0 < min(p[1] for p in poly)-EPS or y1 > max(p[1] for p in poly)+EPS:
        return False
    levels = sorted(set([y0,y1]+[p[1] for p in poly if y0 < p[1] < y1]))
    if any(y0 < by1-EPS and y1 > by0+EPS and x0 < bx1-EPS and x1 > bx0+EPS
           for bx0,by0,bx1,by1 in holes):
        return False
    # Endpoints plus interval midpoints check linear polygon edges without a sampling grid.
    for y in levels+[(a+b)/2 for a,b in zip(levels,levels[1:])]:
        if not any(a-EPS <= x0 and x1 <= b+EPS for a,b in hspans(poly,y)):
            return False
    return True


def channels(edges, rails, brackets, poly, holes, hspans, placement):
    """Швеллеры только на общих уровнях существующих пар опор, без выдуманных опор."""
    if not edges:
        return [], []
    edges = [e for e in edges if not e.get('insufficient_width')]
    if placement is None:
        return [], [dict(corner_x=round(e['corner_x'],4),
            inner_x=round(e['inner_x'],4), reason='placement_not_specified',
            message='Привязка швеллера не уточнена: задайте наружный свес и смещение центра по Y.')
            for e in edges]
    outer_overhang, y_offset = placement
    by_x = {}
    for bracket in brackets:
        by_x.setdefault(round(bracket['x'],4),set()).add(round(bracket['y'],4))
    spans = {}
    for rail in rails:
        spans.setdefault(round(rail['x'],4),[]).append((rail['y0'],rail['y1']))
    result, issues, seen = [], [], set()
    for edge in edges:
        cx,ix = round(edge['corner_x'],4),round(edge['inner_x'],4)
        common = by_x.get(cx,set()) & by_x.get(ix,set())
        candidates = {y for y in by_x.get(cx,set()) | by_x.get(ix,set())
                      if edge['y0']-EPS <= y <= edge['y1']+EPS}
        if not candidates:
            issues.append(dict(corner_x=cx,inner_x=ix,reason='unpaired_bracket_level'))
            continue
        ends = sorted((edge['corner_x']-edge['direction']*outer_overhang,
            edge['inner_x']+edge['direction']*(100-outer_overhang)))
        interior = sorted((edge['corner_x'],
            edge['inner_x']+edge['direction']*(100-outer_overhang)))
        for bracket_y in sorted(candidates):
            y = bracket_y+y_offset
            reason = None
            if bracket_y not in common:
                reason = 'unpaired_bracket_level'
            elif not all(any(lo-EPS <= bracket_y <= hi+EPS for lo,hi in spans.get(x,[]))
                         for x in (cx,ix)):
                reason = 'rail_end_or_joint'
            elif not rectangle_in_wall(interior[0],interior[1],y-50,y+50,poly,holes,hspans):
                reason = 'opening_or_wall_boundary'
            if reason:
                issues.append(dict(corner_x=cx,inner_x=ix,y=y,bracket_y=bracket_y,reason=reason))
                continue
            key = (round(ends[0],4),round(ends[1],4),y)
            if key in seen:
                continue
            seen.add(key)
            result.append(dict(x0=ends[0],x1=ends[1],y=y,y0=y-50,y1=y+50,
                height=100.0,len=edge['offset']+200,bracket_y=bracket_y,
                outer_overhang=outer_overhang,y_offset=y_offset,role='corner_channel',
                kind='швеллер угловой',corner_x=cx,inner_x=ix,side=edge['side'],
                geometry_status='drawn',capacity_status='not_verified'))
    return result,issues


def validate_placement(req):
    """Нет привязки — не подменять её монтажным default; половина пары тоже отсутствует."""
    names = ('corner_channel_outer_overhang','corner_channel_y_offset')
    if any(req.get(name) is None for name in names):
        return None, None
    try:
        overhang, shift = (float(req[name]) for name in names)
        if not all(math.isfinite(v) for v in (overhang,shift)) or not 0 <= overhang <= 100:
            raise ValueError()
    except (ValueError,TypeError,OverflowError):
        return None, 'Наружный свес швеллера должен быть 0…100 мм, смещение центра по Y — конечным числом.'
    return (overhang,shift),None


ISSUE_MESSAGES = {
    'placement_not_specified': 'Привязка швеллера не уточнена: задайте наружный свес и смещение центра по Y.',
    'unpaired_bracket_level': 'У выбранного угла нет пары кронштейнов на одной отметке; швеллер не нарисован.',
    'rail_end_or_joint': 'На уровне кронштейна отсутствует одна из двух угловых направляющих.',
    'opening_or_wall_boundary': 'Швеллер пересекает проём или выходит за контур с внутренней стороны; геометрия не укорочена.',
    'no_matching_edge': 'Выбранная точка не совпадает с вертикальной внешней гранью контура.',
    'insufficient_width': 'Отступ L+100 не помещается в участке стены; угловая ось здесь не построена.',
}


def complete_issues(issues):
    return [dict(issue, code=issue['reason'], message=ISSUE_MESSAGES[issue['reason']]) for issue in issues]


def width_issues(edges, poly, hspans):
    """У каждой выбранной грани должен помещаться второй ряд, у двух — оба отступа."""
    issues=[]
    for edge in edges:
        middle=(edge['y0']+edge['y1'])/2
        fitting=any(a-EPS <= edge['corner_x'] <= b+EPS and
                    a-EPS <= edge['inner_x'] <= b+EPS for a,b in hspans(poly,middle))
        conflict=any(other is not edge and edge['direction'] != other['direction'] and
            min(edge['y1'],other['y1']) > max(edge['y0'],other['y0'])+EPS and
            min(edge['corner_x'],other['corner_x']) < max(edge['inner_x'],other['inner_x']) and
            ((edge['direction'] > 0 and edge['corner_x'] < other['corner_x'] and edge['inner_x'] > other['inner_x']+EPS) or
             (edge['direction'] < 0 and edge['corner_x'] > other['corner_x'] and edge['inner_x'] < other['inner_x']-EPS))
            for other in edges)
        if not fitting or conflict:
            edge['insufficient_width']=True
            issues.append(dict(corner_x=edge['corner_x'],inner_x=edge['inner_x'],reason='insufficient_width'))
    return issues


def wall_spans(poly,x,edges,vspans):
    """Half-open polygon scan excludes one side of an internal vertical edge.

    An explicitly selected outer edge belongs to the facade boundary regardless
    of which side the scan includes. Add only that selected edge's Y interval.
    """
    spans=list(vspans(poly,x))
    spans += [(e['y0'],e['y1']) for e in edges if abs(x-e['corner_x']) <= EPS]
    merged=[]
    for lo,hi in sorted(spans):
        if merged and lo <= merged[-1][1]+EPS:
            merged[-1]=(merged[-1][0],max(hi,merged[-1][1]))
        else: merged.append((lo,hi))
    return merged
