# -*- coding: utf-8 -*-
"""Сквозные проверки стыков между модулями (ревью 23.09).

Каждый сценарий повторяет цепочку C#-команд там, где её нельзя
запустить без AutoCAD: сборка запроса — ЗЕРКАЛО указанных строк C#
(номера строк — на коммит 2e6a937), дальше — настоящие движки. Итог
по сценарию: OK / BUG (с доказательством) / INFO (замер).

  X1  ATFZONE → ATTILE: зона выбрана штриховкой/маркой
      (TilePatternCommand.cs:128-140 шлёт в движок Xrecord ATFZONE,
      в нём нет геометрии; ATCLAD берёт её из <dwg>_fzones.json).
  X2  ATFZONE → ATCLAD: тот же сценарий через _fzones.json — контроль.
  X8  ATFZONE → ATFRAME: часть зоны из _fzones.json (facade_zone/1:
      outer/openings[].poly) → frame_engine._zone_to_contour ждёт
      zone.contour.pts (формат есть только в test_frame_engine.py).
  X3  ATCLAD → ATFRAME: две зоны друг над другом с разной сеткой швов.
      ATCLAD пишет в метку КАЖДОЙ зоны объединение осей прогона
      (clad_engine.op_cladding, CladCommand.cs:570), ATFRAME сливает
      оси всех выбранных меток в один список (FrameCommand.cs:104-121),
      frame_plan фильтрует оси только по габариту контура
      (frame_plan.py: wedges/`x0-EPS <= j <= x1+EPS`).
  X4  ATFRAME: в выборке и штриховка зоны, и её полилинии (выбор
      рамкой). ATCLAD выкидывает полилинии зоны из «голых»
      (CladCommand.cs:185-196), ATFRAME — нет (FrameCommand.cs:266-286).
  X5  ATTILE: размер метки (хэндлы всех кусков в Xrecord) на
      фикстуре 290×82 — замер, не приговор.
  X6  ATSPEC: первичная таблица (ReportCommand — NumClean над
      атрибутами/дин.свойствами) против авто-пересчёта/ATSPECUPDATE/
      ATSPECEDIT (ReportReactor.CollectRecords — без NumClean).
  X7  ATFRAME: голые контуры — L-образная стена и отдельная зона в
      её «кармане». frame_engine группирует контуры по вложенности
      ГАБАРИТОВ, clad_engine — точкой в полигоне.

Запуск (из корня репо): PYTHONUTF8=1 python3 tools/xmod_check.py [--no-fixture]
Нужны: ezdxf (только X5). Выход 1, если есть BUG.
"""
import json
import os
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
for sub in ("Facades/engine", "AClad/engine", "AFrame/engine", "ATableSpec/engine",
            "AClad/tools"):
    sys.path.insert(0, os.path.join(ROOT, sub))

import facades_engine as fe  # noqa: E402
import clad_engine as ce     # noqa: E402
import frame_engine as fre   # noqa: E402

RESULTS = []


def rep(status, sid, msg):
    RESULTS.append((status, sid, msg))
    print("[%s] %s: %s" % (status, sid, msg))


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def roundtrip(obj):
    """Как JavaScriptSerializer: числа/строки/списки, без tuple."""
    return json.loads(json.dumps(obj, ensure_ascii=False))


def atfzone(contours, prefix="Ф-", start=1):
    """ATFZONE: движок зон + то, что ZoneCommand кладёт в Xrecord и JSON."""
    r = fe.run(roundtrip({"op": "zones", "units": "mm", "cladding": "КГ",
                          "zone_prefix": prefix, "start_index": start,
                          "contours": contours}))
    assert r["ok"], r
    xrec = {z["zone_id"]: {"zone_id": z["zone_id"], "cladding": "КГ",
                           "report": z["report"]} for z in r["zones"]}   # ZoneCommand.cs:375-380
    return r, xrec


# ── X1/X2 ────────────────────────────────────────────────────────────
def x1_x2():
    r, xrec = atfzone([{"id": "1A", "pts": rect(0, 0, 6000, 3000)},
                       {"id": "1B", "pts": rect(2000, 1000, 3500, 2500)}])
    zid = r["zones"][0]["zone_id"]
    tile = {"tile": {"w": 600, "h": 600}, "gap": {"v": 8, "h": 8}, "types": ["КГ"]}
    # TilePatternCommand.cs:138: zonesPayload.Add({zone_id, zone: <Xrecord ATFZONE>})
    t = ce.run(roundtrip(dict(tile, op="tile_pattern",
                              zones=[{"zone_id": zid, "zone": xrec[zid]}])))
    if t.get("ok"):
        rep("OK", "X1", "ATTILE по штриховке зоны: %d кусков" % len(t["pieces"]))
    else:
        rep("BUG", "X1", "ATTILE по штриховке/марке зоны ATFZONE отказывает: «%s»; notes=%s "
            "(в Xrecord только %s — геометрии нет)"
            % (t.get("error"), t.get("notes"), sorted(xrec[zid])))
    # CladCommand.cs:167-180: геометрия из _fzones.json (FindZoneParts)
    part = r["zones_full"][0]
    c = ce.run(roundtrip(dict(tile, op="tile_pattern",
                              zones=[{"zone_id": part["id"], "zone": part}])))
    ok = c.get("ok") and len(c["pieces"]) > 0
    rep("OK" if ok else "BUG", "X2",
        "та же зона с геометрией из _fzones.json: ok=%s, кусков %d"
        % (c.get("ok"), len(c.get("pieces") or [])))


# ── X3 ───────────────────────────────────────────────────────────────
def x3():
    # цоколь без окон + этаж с окнами (центрирование простенков даёт другую сетку)
    r, _ = atfzone([{"id": "A0", "pts": rect(0, 0, 9000, 1200)},
                    {"id": "B0", "pts": rect(0, 1200, 9000, 4200)},
                    {"id": "B1", "pts": rect(1300, 2100, 2700, 3700)},
                    {"id": "B2", "pts": rect(5100, 2100, 6800, 3700)}])
    parts = {p["id"]: p for p in r["zones_full"]}
    za, zb = sorted(parts)          # Ф-1 (цоколь), Ф-2 (этаж)
    clad = {"op": "cladding", "tile": {"w": 600, "h": 600}, "gap": {"v": 10, "h": 10},
            "origin": {"y": 0.0}, "vjoints": [], "hjoints": []}
    both = ce.run(roundtrip(dict(clad, zones=[{"zone_id": z, "zone": parts[z]} for z in (za, zb)])))
    own = {z: ce.run(roundtrip(dict(clad, zones=[{"zone_id": z, "zone": parts[z]}])))
           for z in (za, zb)}
    assert both["ok"] and all(v["ok"] for v in own.values())
    ja, jb = set(own[za]["joints_x"]), set(own[zb]["joints_x"])
    union = sorted(set(both["joints_x"]))
    frame = {"op": "frame", "sub_type": "vertical", "system": None, "floors_y": [],
             "floor_step": 0.0, "rail_profile": "", "nsp_type": "НСП-1"}

    raw = {za: [{"id": "A0", "pts": rect(0, 0, 9000, 1200)}],
           zb: [{"id": "B0", "pts": rect(0, 1200, 9000, 4200)},
                {"id": "B1", "pts": rect(1300, 2100, 2700, 3700)},
                {"id": "B2", "pts": rect(5100, 2100, 6800, 3700)}]}

    def rails_x(zone, joints, rows):
        # зоны ATFRAME сейчас не читает (X8) — работает путь голых полилиний
        f = fre.run(roundtrip(dict(frame, zones=[], contours=raw[zone],
                                   joints_x=joints, rows_y=rows)))
        assert f["ok"], f.get("error")
        return sorted({round(t["x"], 1) for t in f["rails"]}), f["summary"]

    extra = {}
    for z, jz in ((za, sorted(ja)), (zb, sorted(jb))):
        rows_own = own[z]["rows_y"]
        x_ok, s_ok = rails_x(z, jz, rows_own)
        x_bad, s_bad = rails_x(z, union, sorted(set(both["rows_y"])))   # как FrameCommand
        extra[z] = (sorted(set(x_bad) - set(x_ok)), s_ok["rails"], s_bad["rails"],
                    s_ok["brackets_row"] + s_ok["brackets_main"],
                    s_bad["brackets_row"] + s_bad["brackets_main"])
    if ja == jb:
        rep("OK", "X3", "сетки зон совпали — сценарий не показателен")
        return
    bad = {z: e for z, e in extra.items() if e[0]}
    if bad:
        msg = "; ".join("%s: стоек %d→%d, кронштейнов %d→%d, лишние оси X=%s"
                        % (z, e[1], e[2], e[3], e[4], e[0][:6]) for z, e in bad.items())
        rep("BUG", "X3", "оси швов одной зоны попадают в другую (метка ATCLAD = объединение "
            "прогона; ATFRAME сливает метки): " + msg)
    else:
        rep("OK", "X3", "чужие оси не дали лишних стоек")


# ── X4 ───────────────────────────────────────────────────────────────
def x4():
    contours = [{"id": "2F0", "pts": rect(0, 0, 6000, 3000)},
                {"id": "2F1", "pts": rect(2000, 900, 3400, 2400)}]
    r, _ = atfzone(contours)
    part = r["zones_full"][0]
    base = {"op": "frame", "sub_type": "vertical", "system": None, "floors_y": [],
            "floor_step": 0.0, "rail_profile": "", "nsp_type": "НСП-1",
            "joints_x": [305.0 + 610 * k for k in range(10)], "rows_y": [605.0 * k for k in range(1, 5)]}
    # X8: frame_engine зону facade_zone/1 не читает; моделируем ПОЧИНЕННОЕ чтение —
    # та же зона в формате, который он понимает (zone.contour/openings[].contour)
    fixed = {"contour": {"pts": part["outer"]["pts"]},
             "openings": [{"contour": {"pts": o["poly"]["pts"]}} for o in part.get("openings") or []]}
    zrec = [{"zone_id": part["id"], "zone": fixed}]
    only_zone = fre.run(roundtrip(dict(base, zones=zrec, contours=[])))
    # FrameCommand.cs:266-286 — полилинии из выборки идут в contours БЕЗ вычета контуров зоны
    both = fre.run(roundtrip(dict(base, zones=zrec, contours=contours)))
    # CladCommand.cs:185-196 так делает: polyData.Remove(outer_contour_id / openings[].id)
    ids = {part["meta"]["outer_contour_id"]} | {o["id"] for o in part.get("openings") or []}
    dedup = fre.run(roundtrip(dict(base, zones=zrec,
                                   contours=[c for c in contours if c["id"] not in ids])))
    assert only_zone["ok"] and both["ok"] and dedup["ok"]
    n1, n2, n3 = (x["summary"]["rails"] + x["summary"]["brackets_row"] + x["summary"]["brackets_main"]
                  + len(x["clamps"]) for x in (only_zone, both, dedup))
    pos = [(round(t["x"], 1), round(t["y0"], 1)) for t in both["rails"]]
    dup = len(pos) - len(set(pos))
    if n2 > n1:
        rep("BUG", "X4", "ЛАТЕНТНО (проявится после починки X8): штриховка зоны + её полилинии "
            "в одной выборке: элементов %d вместо %d "
            "(%d направляющих-двойников в тех же координатах); с вычетом, как в ATCLAD, — %d"
            % (n2, n1, dup, n3))
    else:
        rep("OK", "X4", "задвоения нет (%d)" % n2)


# ── X8 ───────────────────────────────────────────────────────────────
def x8():
    r, _ = atfzone([{"id": "1A", "pts": rect(0, 0, 6000, 3000)},
                    {"id": "1B", "pts": rect(2000, 900, 3400, 2400)}])
    part = r["zones_full"][0]
    # FrameCommand.cs:270-283: zonesPayload.Add({zone_id: pid, zone: part из _fzones.json})
    f = fre.run(roundtrip({"op": "frame", "sub_type": "vertical", "system": None,
                           "zones": [{"zone_id": part["id"], "zone": part}], "contours": [],
                           "joints_x": [305.0 + 610 * k for k in range(10)],
                           "rows_y": [605.0 * k for k in range(1, 5)], "floors_y": []}))
    if f.get("ok") and f["summary"]["rails"] > 0:
        rep("OK", "X8", "ATFRAME по зоне ATFZONE: стоек %d" % f["summary"]["rails"])
    else:
        rep("BUG", "X8", "ATFRAME по зоне из _fzones.json: «%s», notes=%s — движок ищет "
            "zone.contour, а в facade_zone/1 zone.outer (ключи части: %s)"
            % (f.get("error"), f.get("notes"), sorted(part)))


# ── X5 ───────────────────────────────────────────────────────────────
def x5():
    try:
        import ezdxf  # noqa: F401
        import tile_pattern_dxf as T
    except ImportError:
        rep("INFO", "X5", "ezdxf нет — замер метки пропущен")
        return
    import collections
    path = os.path.join(ROOT, "AClad/tools/testdata/tiles290/tiles290_input.dxf")
    doc = ezdxf.readfile(path)
    msp = doc.modelspace()
    sample, _ = T.read_sample(msp, doc, None, "SAMPLE_")
    zp = T.closed_polys(msp, ["ZONE_TERRACOTTA", "ZONE_BEIGE"])
    req = {"op": "tile_pattern", "tile": {"w": 290, "h": 82}, "gap": {"v": 7, "h": 7},
           "sample": sample, "datum": {"mode": "bbox"}, "min_piece": 10, "kerf": 3,
           "merge_touching": True, "warn_cut": 150,
           "contours": [{"id": "%X" % (0x2A000 + i), "pts": p["pts"]} for i, p in enumerate(zp)]}
    res = ce.run(roundtrip(req))
    assert res["ok"], res.get("error")
    cnt = collections.Counter()
    for p in res["pieces"]:          # TilePatternCommand.cs:395-467: что получает хэндл
        n = 1
        if not p["full"]:
            rings = p.get("rings") or [0]
            n = len(rings) + (1 if len(rings) > 1 else 0)
        if p.get("small"):
            n += 1
        cnt[p["zone"]] += n
    h, sizes = 0x100000, []
    for pz in res["per_zone"]:        # TilePatternCommand.cs:478-507
        k = cnt[pz["zone_id"]]
        meta = {"zone_id": pz["zone_id"], "members": pz.get("members"),
                "joints_x": pz.get("joints_x"), "rows_y": pz.get("rows_y"), "tiles": k,
                "handles": ["%X" % (h + i) for i in range(k)]}
        h += k
        sizes.append((len(json.dumps(meta, ensure_ascii=False, separators=(",", ":"))), k,
                      len(pz.get("members") or [1])))
    sizes.sort(reverse=True)
    tot = sum(s * m for s, _, m in sizes)
    rep("INFO", "X5", "фикстура 290×82: хэндлов в метках %d; крупнейшая метка %.0f КБ "
        "(%d хэндлов, %d строк Xrecord по 250, копий на объектах %d); все метки %.1f МБ"
        % (sum(k for _, k, _ in sizes), sizes[0][0] / 1024, sizes[0][1],
           sizes[0][0] // 250 + 1, sizes[0][2], tot / 1e6))


# ── X6 ───────────────────────────────────────────────────────────────
def num_clean(s):
    """Зеркало ReportCommand.NumClean (ReportCommand.cs:585-597)."""
    if not s or ("." not in s and "," not in s):
        return s
    t = s.replace("\u00a0", "").replace(" ", "").replace(",", ".")
    try:
        x = float(t)
    except ValueError:
        return s
    r = round(x)
    if abs(x - r) <= 1e-6 * max(1.0, abs(x)):
        return "%d" % r
    return s


def x6():
    from atspec_report import run_report
    raw = ["1500", "1499.99999999998", "1500.00000000002", "900", "899.999999999997"]
    d = {"title": "T", "sections": [{"section_title": "", "header": ["№", "Длина", "Кол."],
                                     "columns": ["=row", "=Object.«Длина»", "=Count"],
                                     "group_by": 1, "sort_by": [1, "asc"], "filter": []}]}

    def rows(f):
        blocks = [{"name": "Доборник", "layer": "L", "attributes": {"Длина": f(v)}} for v in raw]
        rr = run_report(blocks, roundtrip(d))
        return rr["sections"][0]["rows"] if "sections" in rr else rr["rows"]
    a, b = rows(num_clean), rows(lambda s: s)
    if len(a) != len(b):
        rep("BUG", "X6", "дин.блок с длиной-double: таблица ATSPECREPORT %d строк %s, после "
            "авто-пересчёта/ATSPECUPDATE/ATSPECEDIT %d строк %s (в CollectRecords нет NumClean)"
            % (len(a), [r[1:] for r in a], len(b), [r[1:] for r in b]))
    else:
        rep("OK", "X6", "пересчёт совпал с первичной таблицей")


# ── X7 ───────────────────────────────────────────────────────────────
def x7():
    L = [[0, 0], [9000, 0], [9000, 6000], [6000, 6000], [6000, 3000], [0, 3000]]
    pocket = rect(1000, 3500, 5000, 5500)       # отдельная зона в «кармане» L (над короткой полкой)
    notes = []
    fr = fre._group_contours([{"id": "L", "pts": L}, {"id": "P", "pts": pocket}], notes)
    cl = ce._group_contours([{"id": "L", "pts": L}, {"id": "P", "pts": pocket}], [])
    fo = sorted(cid for cid, _ in fr)
    co = sorted(cid for cid, _ in cl)
    if fo != co:
        rep("BUG", "X7", "группировка голых контуров расходится: ATFRAME видит внешние %s "
            "(проёмов у L: %d), ATCLAD — %s" % (fo, len(dict(fr).get("L", {}).get("holes", [])), co))
    else:
        rep("OK", "X7", "группировка совпала: %s" % fo)


def main(argv):
    x1_x2()
    x8()
    x3()
    x4()
    if "--no-fixture" not in argv:
        x5()
    x6()
    x7()
    nb = sum(1 for s, _, _ in RESULTS if s == "BUG")
    print("── ИТОГ: %d проверок, BUG %d, OK %d, INFO %d"
          % (len(RESULTS), nb, sum(1 for s, _, _ in RESULTS if s == "OK"),
             sum(1 for s, _, _ in RESULTS if s == "INFO")))
    return 1 if nb else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
