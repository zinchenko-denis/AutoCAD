"""Public synthetic frame requests and independent physical-element oracles.

Counts come from actual emitted arrays, lengths from individual endpoints and
piece lengths. Rounded summaries and rail_stock_est are never quantity inputs.
"""
import copy
from collections import Counter
from decimal import Decimal
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'AFrame/engine'))
import frame_engine

ARRAYS = ('rails', 'hrails', 'brackets', 'clamps', 'fittings')


def rect(x, y, w, h):
    return [[x, y], [x + w, y], [x + w, y + h], [x, y + h]]


def zone(zid, points, holes=(), **extra):
    return dict({'zone_id': zid, 'zone': {'schema': 'facade_zone/1', 'id': zid, 'units': 'mm',
                'outer': {'pts': points}, 'openings': [
                    {'id': f'H{i}', 'kind': 'window', 'poly': {'pts': hole}} for i, hole in enumerate(holes)]}}, **extra)


def cases():
    base = {'op': 'frame', 'system': 'Standart', 'sub_type': 'vertical',
            'zones': [zone('P1', rect(0, 0, 1220, 6100))], 'joints_x': [610],
            'rows_y': [605 * i for i in range(1, 11)]}
    values = {'vertical_stock': copy.deepcopy(base)}
    values['precision_lengths'] = dict(base, zones=[zone('P1', rect(0, 0, 1220, 6100.1234))])
    opening = [zone('P1', rect(0, 0, 3000, 6000), [rect(1000, 2000, 1000, 1500)])]
    values['vertical_opening'] = dict(base, zones=opening, joints_x=[500, 1500, 2500], floors_y=[3000])
    values['interfloor_opening'] = dict(values['vertical_opening'], system='Межэтажная', sub_type='interfloor')
    values['ortho_opening'] = dict(values['vertical_opening'], system='Ортогональная', sub_type='ortho')
    values['composite_frame_only'] = dict(base, cladding='composite', parts='frame')
    values['only_clamps'] = dict(base, parts='clamps', rails_fixed=[{'x': 610, 'y0': 0, 'y1': 3000}])
    values['empty'] = dict(base, joints_x=[50000], rows_y=[])
    values['independent_zones'] = dict(base, zones=[
        zone('P1', rect(0, 0, 1220, 6100), joints_x=[610]),
        zone('P2', rect(3000, 0, 1220, 3000), joints_x=[3610])])
    for material in ('clinker', 'concrete'):
        values[material + '_shina'] = {
            'op': 'frame', 'system': {'name': 'Standart', 'bracket_step': 600, 'bracket_step_corner': 600},
            'sub_type': 'vertical', 'parts': 'frame', 'exact_step': True, 'cladding': material,
            'tile_step_x': 600, 'tile_step_x_corner': 300, 'corners_x': [0],
            'tile_whip': 1500, 'tile_rail_brand': 'ШК-1',
            'zones': [zone('P1', rect(0, 0, 2000, 1500), rows_y=[72 * i for i in range(1, 20)])]}
    return values


def oracle(response):
    if response.get('ok') is not True:
        raise ValueError('The physical oracle requires a successful engine response')
    elements = []
    for category in ARRAYS:
        if not isinstance(response.get(category), list):
            raise ValueError('Missing engine category: ' + category)
        for index, piece in enumerate(response[category]):
            role = {'rails': 'rail', 'hrails': 'hrail', 'brackets': 'bracket',
                    'clamps': 'clamp', 'fittings': 'fitting'}[category]
            if category == 'hrails' and piece.get('kind', '').startswith('шина'):
                role = 'shina'
            length = None
            if category in ('rails', 'hrails'):
                endpoints = (piece['y0'], piece['y1']) if category == 'rails' else (piece['x0'], piece['x1'])
                geometric = abs(Decimal(str(endpoints[1])) - Decimal(str(endpoints[0])))
                length = Decimal(str(piece['len']))
                if abs(length - geometric) > Decimal('0.001') or length <= 0:
                    raise ValueError('Emitted length disagrees with its geometric endpoints')
            profile = piece.get('profile') if length is not None else None
            if profile in ('направляющая', 'Z-профиль', 'НГП') or (role == 'shina' and profile == piece.get('kind')):
                profile = None  # Generic scheme label is not an identified profile mark.
            elements.append({'source': category + ':' + str(index), 'role': role,
                             'zone': piece['zone'], 'type': piece.get('kind'),
                             'mark': profile,
                             'length_mm': None if length is None else float(length)})
    totals = Counter(element['role'] for element in elements)
    lengths = {}
    for role in ('rail', 'hrail', 'shina'):
        lengths[role] = float(sum((Decimal(str(item['length_mm'])) for item in elements if item['role'] == role), Decimal(0)) / 1000)
    return {'elements': elements, 'counts': dict(totals), 'length_m': lengths,
            'physical_count': len(elements), 'zone_ids': sorted(item['zone_id'] for item in response['per_zone'])}


def run_cases():
    results = {}
    for name, request in cases().items():
        response = frame_engine.run(copy.deepcopy(request))
        result = oracle(response)
        results[name] = {'request': request, 'response': response, 'oracle': result}
    # Small independently calculable controls, not universal design rules.
    stock = results['vertical_stock']
    assert stock['oracle']['counts']['rail'] == 3
    assert stock['oracle']['length_m']['rail'] == 6.08
    assert stock['response']['summary']['rail_stock_est'] == 2
    # Последние два хлыста перераспределены: 80-мм одноопорный хвост
    # устранён, общий погонаж и оба зазора прежние.
    stock_rails = stock['response']['rails']
    assert [piece['len'] for piece in stock_rails] == [3000.0, 1540.0, 1540.0]
    assert all(b['y0'] - a['y1'] == 10 for a, b in zip(stock_rails, stock_rails[1:]))
    assert all(len({b['y'] for b in stock['response']['brackets']
                    if b['x'] == piece['x'] and piece['y0'] <= b['y'] <= piece['y1']}) >= 2
               for piece in stock_rails)
    assert results['empty']['oracle']['physical_count'] == 0
    assert results['only_clamps']['oracle']['counts'] == {'clamp': 5}
    assert results['clinker_shina']['oracle']['counts']['shina'] == 42
    assert results['clinker_shina']['oracle']['length_m']['shina'] == 42
    assert not results['composite_frame_only']['response']['clamps']
    return results


if __name__ == '__main__':
    results = run_cases()
    for name, result in results.items():
        print(name, result['oracle']['counts'], result['oracle']['length_m'])
    print('Frame engine oracle fixtures:', len(results), 'PASS')
