# -*- coding: utf-8 -*-
"""Краш-тест движка ABlockGen (аудит-паттерн ATableSpec/test_audit_scenarios):
грязные и краевые сценарии практики ДО того, как их найдёт Алексей.
Каждый K-сценарий фиксирует ФАКТИЧЕСКОЕ поведение: либо корректный результат,
либо внятная ошибка (ValueError с человеческим текстом), либо осознанный EDGE.
"""
import json
import os
import sys
import tempfile
import time

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

from vitrage_plan import build_plan
from vitrage_recognize import recognize, strips_from_segments
from vitrage_engine import run as engine_run, main as engine_main

PASS = 0
EDGE = []


def ok(cond, msg):
    global PASS
    assert cond, msg
    PASS += 1


def edge(msg):
    EDGE.append(msg)


def bb(x0, y0, x1, y1):
    return {"x0": x0, "y0": y0, "x1": x1, "y1": y1}


def base_req(**over):
    """Валидный минимум: 2 стойки 50 мм, 1 отметка."""
    req = {
        "strips": [bb(0, 0, 50, 3000), bb(2000, 0, 2050, 3000),
                   bb(50, 1475, 2000, 1525)],
        "blocks": {"stand": {"name": "S"}, "rigel": {"name": "R"}},
    }
    req.update(over)
    return req


def must_valueerror(req, tag, fn=recognize):
    try:
        fn(req)
    except ValueError as ex:
        ok(len(str(ex)) > 10, f"{tag}: текст ошибки слишком короткий: {ex!r}")
        return str(ex)
    raise AssertionError(f"{tag}: ValueError не поднят")


# ── K1: перевёрнутые bbox полос (x1<x0, y1<y0) — нормализуются ──
p = recognize(base_req(strips=[bb(50, 3000, 0, 0), bb(2050, 3000, 2000, 0),
                               bb(2000, 1525, 50, 1475)]))
ok(p["summary"]["stands"] == 2 and p["summary"]["rigels"] == 1, "K1: swap bbox")

# ── K2: дубли полос (двойная обводка) — не плодят стоек/ригелей ──
req2 = base_req()
req2["strips"] = req2["strips"] + list(req2["strips"])
p = recognize(req2)
ok(p["summary"]["stands"] == 2 and p["summary"]["rigels"] == 1, "K2: дубли полос")

# ── K3: дрожь координат Revit (±0.4 мм) — одна цепочка, не две ──
p = recognize(base_req(strips=[bb(0, 0, 50, 1000), bb(0.4, 1000, 50.4, 3000),
                               bb(2000, 0, 2050, 3000), bb(50, 1475, 2000, 1525)]))
ok(p["summary"]["stands"] == 2, f"K3: дрожь X → стоек {p['summary']['stands']}")

# ── K4: вырожденные полосы (нулевая площадь, точки) — отсев без падения ──
p = recognize(base_req(strips=[bb(0, 0, 50, 3000), bb(2000, 0, 2050, 3000),
                               bb(50, 1475, 2000, 1525),
                               bb(5, 5, 5, 5), bb(100, 100, 100, 900)]))
ok(p["summary"]["stands"] == 2 and p["summary"]["rigels"] == 1, "K4: вырожденные")

# ── K5: одна колонна вертикалей → внятная ошибка ──
msg = must_valueerror({"strips": [bb(0, 0, 50, 3000)],
                       "blocks": {"stand": {"name": "S"}, "rigel": {"name": "R"}}},
                      "K5: одна вертикаль")
ok("вертикал" in msg, f"K5: текст не про вертикали: {msg}")

# ── K6: колонна СМЕШАННОЙ ширины (50-сегмент + 75-сегмент, реальный риск
#    «импровизаций») — интервалы X не совпадают → две близкие цепочки →
#    сейчас это ValueError «оси слишком близко». Фиксируем поведение: ошибка
#    ВНЯТНАЯ, не молчаливый кривой каркас. Улучшение — по реальному АР. ──
msg = must_valueerror(base_req(strips=[bb(0, 0, 50, 1500), bb(-12.5, 1500, 62.5, 3000),
                                       bb(2000, 0, 2050, 3000),
                                       bb(50, 1475, 2000, 1525)]),
                      "K6: смешанная ширина колонны")
ok("близко" in msg, f"K6: текст: {msg}")
edge("K6: колонна из сегментов разной ширины (50+75) даёт ошибку «оси близко», "
     "а не каркас — осознанно; лечить по реальному АР-примеру")

# ── K7: наклонные отрезки в segments — игнор, полосы из прямых ──
segs = [{"x0": 0, "y0": 0, "x1": 0, "y1": 3000},
        {"x0": 50, "y0": 0, "x1": 50, "y1": 3000},
        {"x0": 2000, "y0": 0, "x1": 2000, "y1": 3000},
        {"x0": 2050, "y0": 0, "x1": 2050, "y1": 3000},
        {"x0": 50, "y0": 1475, "x1": 2000, "y1": 1475},
        {"x0": 50, "y0": 1525, "x1": 2000, "y1": 1525},
        {"x0": 0, "y0": 0, "x1": 1700, "y1": 2900},     # диагональ — мимо
        {"x0": 10, "y0": 10, "x1": 300, "y1": 40}]      # почти-горизонталь (30/290) — мимо
p = recognize({"strips": [], "segments": segs,
               "blocks": {"stand": {"name": "S"}, "rigel": {"name": "R"}}})
ok(p["summary"]["stands"] == 2 and p["summary"]["rigels"] == 1, "K7: наклонные мимо")

# ── K8: коллинеарные накладывающиеся отрезки (двойная обводка граней) ──
segs8 = segs[:6] + [{"x0": 0, "y0": 500, "x1": 0, "y1": 2500},
                    {"x0": 50, "y0": 500, "x1": 50, "y1": 2500}]
p = recognize({"strips": [], "segments": segs8,
               "blocks": {"stand": {"name": "S"}, "rigel": {"name": "R"}}})
ok(p["summary"]["stands"] == 2, f"K8: дубль-грани → стоек {p['summary']['stands']}")

# ── K9: далеко от нуля (координаты ~1e7, реальные городские CAD-сетки) ──
OFF = 12_345_678.9
p = recognize(base_req(strips=[bb(OFF, OFF, OFF + 50, OFF + 3000),
                               bb(OFF + 2000, OFF, OFF + 2050, OFF + 3000),
                               bb(OFF + 50, OFF + 1475, OFF + 2000, OFF + 1525)]))
st = [i for i in p["inserts"] if i["kind"] == "stand"]
ok(abs(st[0]["x"] - (OFF + 25)) < 0.01, f"K9: точность на 1e7: {st[0]['x']}")
ok(st[0]["attrs"]["ДЛИНА"] == "3000.00", "K9: ДЛИНА на 1e7")

# ── K10: дверь на ровно 50% света пролёта — порог пропускается (граница);
#    дверь НИЖЕ основной отметки (1500 вне её габарита 30..1300) ──
req10 = base_req()
req10["panels"] = [{"name": "Дверь Д-1", "x0": 25, "y0": 30, "x1": 1025, "y1": 1300}]
req10["strips"].append(bb(50, 5, 2000, 55))          # «порог» у низа двери
p = recognize(req10)
ok(p["summary"]["rigels"] == 1, f"K10: порог при 50% перекрытии: {p['summary']['rigels']}")
edge("K10: перекрытие дверью РОВНО 50% света уже глушит порог (граница >=); отметка "
     "ВНУТРИ Y-габарита двери глушится тоже (ригель в проёме двери не ставится) — "
     "оба правила осознанные")

# ── K11: «дверь» латиницей и в другом регистре ──
req11 = dict(req10)
req11["panels"] = [{"name": "ETL_Panel_DOOR_double 1500x2100", "x0": 25, "y0": 30,
                    "x1": 1975, "y1": 1300}]
p = recognize(req11)
ok(p["summary"]["rigels"] == 1, "K11: door латиницей ловится")

# ── K12: полоса-отметка едва заходит в пролёт (<50% света) — ригель не ставится ──
req12 = base_req(strips=[bb(0, 0, 50, 3000), bb(2000, 0, 2050, 3000),
                         bb(50, 1475, 500, 1525)])   # покрывает ~23% света
p = recognize(req12)
ok(p["summary"]["rigels"] == 0, "K12: слабое покрытие — без ригеля")
ok(p["summary"]["rails"] == 1, "K12: отметка при этом видна в rails")

# ── K13: marks без {n} — марки не нумеруются, но не падаем ──
p = recognize(base_req(marks={"stand": "СТОЙКА", "rigel": "Р"}))
names = {i["attrs"]["ИМЯ"] for i in p["inserts"] if i["kind"] == "stand"}
ok(names == {"СТОЙКА"}, f"K13: {names}")
edge("K13: шаблон марки без {n} даёт ОДИНАКОВЫЕ имена всем стойкам — молча; "
     "в C#-диалоге дефолты с {n}, руками сломать можно")

# ── K14: имена блоков с кавычками/бэкслешем/юникодом — JSON-путь цел ──
weird = 'Проф "50х100" \\ Ω'
p = recognize(base_req(blocks={"stand": {"name": weird}, "rigel": {"name": weird}}))
rt = json.loads(json.dumps(p, ensure_ascii=False))   # round-trip как через файлы C#↔exe
ok(rt["inserts"][0]["block"] == weird, "K14: имя с кавычками пережило round-trip")

# ── K15: большой витраж 50 колонн × 30 отметок — время и счётчики ──
strips15 = []
for i in range(50):
    strips15.append(bb(i * 700, 0, i * 700 + 50, 20000))
for j in range(30):
    y = 300 + j * 600
    strips15.append(bb(50, y - 25, 49 * 700, y + 25))
t0 = time.time()
p = recognize({"strips": strips15,
               "blocks": {"stand": {"name": "S"}, "rigel": {"name": "R"}}})
dt = time.time() - t0
ok(p["summary"]["stands"] == 50 and p["summary"]["rigels"] == 30 * 49,
   f"K15: {p['summary']}")
ok(dt < 5.0, f"K15: медленно: {dt:.2f}s")

# ── K16: сегментный шторм (200 отрезков × пары) — сборка полос не квадратит ──
segs16 = []
for i in range(50):
    segs16.append({"x0": i * 700, "y0": 0, "x1": i * 700, "y1": 20000})
    segs16.append({"x0": i * 700 + 50, "y0": 0, "x1": i * 700 + 50, "y1": 20000})
t0 = time.time()
built = strips_from_segments(segs16, 40, 120)
dt = time.time() - t0
ok(len(built) == 50 and dt < 2.0, f"K16: полос {len(built)} за {dt:.2f}s")

# ── K17: plan — ярусы не сходятся с высотой → note, не падение ──
p17 = build_plan({"opening": {"x0": 0, "y0": 0, "x1": 2050, "y1": 3000},
                  "grid": {"step_x": 1000, "tiers": [1000, 1000], "tier_gap": 10},
                  "blocks": {"stand": {"name": "s", "body_w": 50}}})
ok(p17["ok"] and any("ярусы" in n for n in p17["notes"]), f"K17: {p17['notes']}")

# ── K18: plan — отметка ригеля на 0 и на H (края) ──
p18 = build_plan({"opening": {"x0": 0, "y0": 0, "x1": 2050, "y1": 3000},
                  "grid": {"step_x": 1000, "rail_y": [0, 3000]},
                  "blocks": {"stand": {"name": "s", "body_w": 50},
                             "rigel": {"name": "r", "body_w": 45.6},
                             "fill": {"name": "f"}}})
ok(p18["ok"] and p18["summary"]["rigels"] == 4, "K18: краевые отметки ставятся")
fl18 = [i for i in p18["inserts"] if i["kind"] == "fill"]
ok(all(f["dyn"]["Высота"] > 49 for f in fl18), "K18: щели у краёв не заполняются")

# ── K19: plan — n_cols=1 (одна доля, две стойки) ──
p19 = build_plan({"opening": {"x0": 0, "y0": 0, "x1": 1050, "y1": 2000},
                  "grid": {"n_cols": 1},
                  "blocks": {"stand": {"name": "s", "body_w": 50}}})
ok(p19["summary"]["stands"] == 2, "K19: n_cols=1")

# ── K20: движок-CLI: битый JSON → ok:false и rc!=0, а не трейс ──
tmp = tempfile.mkdtemp()
rq = os.path.join(tmp, "bad.json")
out = os.path.join(tmp, "out.json")
with open(rq, "w", encoding="utf-8") as f:
    f.write('{"op": "recognize", "strips": [')
rc = engine_main(["vitrage_engine.py", rq, out])
ok(rc != 0, "K20: rc на битом JSON")
res = json.load(open(out, encoding="utf-8"))
ok(res["ok"] is False and res.get("error"), f"K20: {res}")

# ── K21: неизвестный op → внятная ошибка ──
try:
    engine_run({"op": "explode"})
    ok(False, "K21: не упал")
except ValueError as ex:
    ok("op" in str(ex), f"K21: {ex}")

# ── K22: recognize без rigel_top на верхней отметке == прежнее поведение
#    (регресс совместимости: отсутствие ключа ничего не меняет) ──
p22a = recognize(base_req())
p22b = recognize(base_req(blocks={"stand": {"name": "S"}, "rigel": {"name": "R"},
                                  "rigel_top": {}}))
ok(p22a["summary"] == p22b["summary"], "K22: пустой rigel_top — no-op")

# ── K23: отрицательный/нулевой fold и min_fill в plan — не роняют ──
p23 = build_plan({"opening": {"x0": 0, "y0": 0, "x1": 2050, "y1": 1000},
                  "grid": {"step_x": 1000, "rail_y": [500]},
                  "blocks": {"stand": {"name": "s", "body_w": 50},
                             "rigel": {"name": "r"},
                             "fill": {"name": "f", "fold": 0}}})
fl23 = [i for i in p23["inserts"] if i["kind"] == "fill"]
ok(p23["ok"] and all("Х" in f["attrs"]["РАЗМЕР_ЗАП"] for f in fl23), "K23: fold=0")

# ── K24: dims при отсутствии ригелей — только габариты, без пустых цепочек ──
p24 = recognize({"strips": [bb(0, 0, 50, 3000), bb(2000, 0, 2050, 3000)],
                 "blocks": {"stand": {"name": "S"}, "rigel": {"name": "R"}}})
vd = [d for d in p24["dims"] if d["dir"] == "v"]
ok(len(vd) == 1 and len(vd[0]["pts"]) == 2, f"K24: {vd}")

# ── K25: rot прокидывается и в Э1-plan как строка "270" (JSON от C# мог
#    привезти число или строку — Convert на C#-стороне, движок ест float()) ──
p25 = build_plan({"opening": {"x0": 0, "y0": 0, "x1": 2050, "y1": 1000},
                  "grid": {"step_x": 1000, "rail_y": [500]},
                  "blocks": {"stand": {"name": "s", "body_w": 50},
                             "rigel": {"name": "r", "rot": "270"}}})
ok(all(i["rot"] == 270.0 for i in p25["inserts"] if i["kind"] == "rigel"),
   "K25: rot строкой")

print(f"crash-тест: {PASS} проверок OK, EDGE {len(EDGE)}:")
for e in EDGE:
    print("  ·", e)
