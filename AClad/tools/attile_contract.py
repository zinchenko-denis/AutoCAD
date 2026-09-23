# -*- coding: utf-8 -*-
"""Контракт C# ↔ движок для ATTILE (23.09): каждый ключ, который
TilePatternCommand.cs читает из ответа движка (CladCommand.Get(<obj>,
"key")), обязан быть в реальном ответе на запрос, собранный C#-кодом
(BondSettings.ToEngine — дамп mono-прогона tools/attile_ui, shifts.json).
Запуск: PYTHONUTF8=1 python3 AClad/tools/attile_contract.py [shifts.json]"""
import json, os, re, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "engine"))
import clad_engine as ce  # noqa: E402

cs = open(os.path.join(HERE, "..", "src", "ACladPlugin", "TilePatternCommand.cs"), encoding="utf-8").read()
reads = set(re.findall(r'CladCommand\.Get\((\w+), "(\w+)"\)', cs))
# откуда читается: res — ответ; it — кусок; pz — per_zone; sum — summary;
# absorbed/bond — вложенные в summary; d — by_type[t]
dump = json.load(open(sys.argv[1] if len(sys.argv) > 1 else "/tmp/attile_ui/shifts.json", encoding="utf-8"))
missing = set()
for c in dump:
    req = dict(c["engine"])
    req.update(op="tile_pattern", types=["КГ"],
               contours=[{"id": "A", "pts": [[0, 0], [7000, 0], [7000, 5000], [0, 5000]]},
                         {"id": "W", "pts": [[2000, 1500], [3500, 1500], [3500, 3300], [2000, 3300]]},
                         {"id": "B", "pts": [[7000, 0], [9000, 0], [9000, 5000], [7000, 5000]]}])
    res = ce.run(json.loads(json.dumps(req)))
    assert res.get("ok"), res.get("error")
    s = res["summary"]
    objs = {"res": [res], "it": res["pieces"], "pz": res["per_zone"], "sum": [s],
            "absorbed": [s["absorbed"]], "bond": [s["bond"]],
            "d": list(s["by_type"].values()),
            # НЕ ответ движка (24.09, рецензия: давали 4 ложных «неизвестных»):
            # метки ATCLAD/ATTILE/ATFZONE, части и проёмы зон из _fzones.json,
            # элементы C#-запроса контуров
            "cm": [], "m": [], "z": [], "zd": [], "hz": [], "hrep": [], "part": [], "od": [], "cd": []}
    for var, key in reads:
        if var not in objs:
            missing.add((var, key, "неизвестный объект — добавьте в objs (ответ или вход?)"))
            continue
        pool = objs[var]
        if not pool:
            continue
        # ключ обязан быть хотя бы в одном объекте; для кусков «rings» — только у фигурных
        if key == "rings":
            continue
        if not any(key in o for o in pool):
            missing.add((var, key, "нет в ответе"))
    # сквозная проверка моста: объединённая зона — члены A и B
    pz = res["per_zone"]
    assert any(set(z["members"]) == {"контур A", "контур B"} for z in pz), [z["members"] for z in pz]
# «error»/«notes» — ключи ОТКАЗА: C# читает их при ok=false
bad = ce.run({"op": "tile_pattern", "tile": {"w": 600, "h": 600}, "bond": {"kind": "zigzag"},
              "contours": [{"id": "A", "pts": [[0, 0], [100, 0], [100, 100], [0, 100]]}]})
if not (bad.get("ok") is False and "error" in bad and "notes" in bad):
    missing.add(("res", "error/notes", "нет в отказе"))
missing.discard(("res", "error", "нет в ответе"))
print("C#-ключей из ответа движка: %d; запросов из C#: %d; недостающих: %s"
      % (len(reads), len(dump), sorted(missing) or "нет"))
sys.exit(1 if missing else 0)
