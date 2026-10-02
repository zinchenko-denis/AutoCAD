"""Резка вертикальных направляющих и расстановка их геометрических опор."""
import math

from frame_topology import evaluate_member

EPS = 1e-6


def rail_cuts(a, b, max_length, gap, start_off=None):
    """Хлысты от низа; короткий искусственный хвост делим с предпоследним.

    Отступы кронштейнов заданы системой/конструктором, а не новой нормой.
    Две разные опоры при этих отступах помещаются только на длине >2*start_off.
    Переносим только свободный стык без отметки перекрытия; зазор, границы
    участка, максимальную длину и уже уложенные нижние хлысты сохраняем.
    Исходный короткий участок функция не удлиняет и опоры ему не выдумывает.
    """
    if b - a <= EPS:
        return []
    if max_length <= EPS:
        return [(a, b)]
    parts, cursor = [], a
    while b - cursor > max_length + EPS:
        parts.append((cursor, cursor + max_length))
        cursor += max_length + gap
    # Даже если конец попал в прежний зазор, сохраняем границу участка:
    # последний реальный хлыст и этот остаток получают один общий стык.
    parts.append((cursor, b))
    min_length = 2.0 * float(start_off or 0.0)
    if len(parts) > 1 and parts[-1][1] - parts[-1][0] <= min_length + EPS:
        lo, hi = parts[-2][0], b
        half = (hi - lo - gap) / 2.0
        if half > EPS:
            parts[-2:] = [(lo, lo + half), (lo + half + gap, hi)]
    return [(lo, hi) for lo, hi in parts if hi - lo > EPS]


def rail_brackets(a, b, start_off, step, exact=False):
    """Кронштейны ОДНОЙ направляющей [a, b] — ТЗ Германа 26.07:
    первый 300 от НИЗА, последний 300 от ВЕРХА, между ними равномерно
    с шагом ≤ расчётного (3000 → 300-800-800-800-300 = 4 шт);
    нестандартная длина — крайние 300/300, между ними ≤ шага;
    короче 2×300 — один кронштейн в центре (ответ Германа 07.08, В-ад).
    Вызывающий код помечает такой кусок как не проверенный расчётом,
    сохраняя заданную геометрию и торцевые отступы остальных кусков.

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


def refine_vertical_supports(rails, brackets, model, report, start_off, exact=False):
    """Сгустить опоры только перегруженного куска, не меняя его профиль/торцы.

    Та же сетка подбора 50 мм и нижний предел 200 мм, что у pick_step.
    Общую с соседним куском опору не переносим. Решения одинаковой геометрии
    кешируются лишь на эту операцию; окончательный общий расчёт делает caller.
    """
    owners = {}
    for member in model["members"]:
        for support in member["supports"]:
            for source in support["sources"]:
                if source["source"] == "brackets":
                    owners.setdefault(source["index"], set()).add(member["index"])
    failed = {r["member_index"] for r in model["geometric_screening"]["reasons"]
              if r["reason"] == "member_capacity_exceeded"}
    removed, added, beam_cache, choices = set(), [], {}, {}
    changes = []
    for member in model["members"]:
        if member["index"] not in failed or member["support_count"] < 2:
            continue
        owned = {s["index"] for support in member["supports"] for s in support["sources"]
                 if s["source"] == "brackets"}
        if not owned or any(len(owners[index]) != 1 for index in owned):
            continue
        rail, zone = rails[member["index"]], member["zone"]
        profile = report["profile"][zone]
        profile_key = tuple(sorted(profile.items())) if isinstance(profile, dict) else profile
        key = (zone, profile_key, tuple(member["intervals"]),
               member["bottom_free"], member["top_free"])
        if key not in choices:
            choices[key] = None
            maximum = max(member["intervals"])
            for step in range(int(math.floor((maximum - EPS) / 50)) * 50, 199, -50):
                positions = [round(y, 4) for y in rail_brackets(
                    rail["y0"], rail["y1"], start_off, step, exact)]
                candidate = dict(intervals=[round(b - a, 4) for a, b in zip(positions, positions[1:])],
                    bottom_free=round(positions[0] - rail["y0"], 4),
                    top_free=round(rail["y1"] - positions[-1], 4))
                check = evaluate_member(report["inputs"], profile, candidate, zone,
                                        max(candidate["intervals"]), beam_cache)
                if check["chain"]["passed"]:
                    choices[key] = [y - rail["y0"] for y in positions]
                    break
        if choices[key] is None:
            continue
        first = min(owned, key=lambda index: brackets[index]["y"])
        removed.update(owned)
        for i, dy in enumerate(choices[key]):
            added.append(dict(x=rail["x"], y=round(rail["y0"] + dy, 4),
                              kind=brackets[first]["kind"] if i == 0 else "рядовой"))
        positions = [round(rail["y0"] + dy, 4) for dy in choices[key]]
        changes.append(dict(member_index=member["index"], x=rail["x"], y0=rail["y0"], y1=rail["y1"],
            previous_max_step=max(member["intervals"]),
            actual_max_step=round(max(b - a for a, b in zip(positions, positions[1:])), 4),
            brackets_before=len(owned), brackets_after=len(positions)))
    return ([b for i, b in enumerate(brackets) if i not in removed] + added
            if changes else brackets), changes
