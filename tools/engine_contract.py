# -*- coding: utf-8 -*-
"""Контракт ключей C# ↔ движок для всех пяти модулей (ревью 23.09).

Идея как у AClad/tools/attile_contract.py, но шире и дешевле: каждый ключ,
который C#-команда читает из ОТВЕТА движка (`Get(<var>, "key")`,
`GetBool(...)`, `D(...)`, `S(...)`), обязан встречаться хотя бы в одном
реальном ответе. Ответы собираются перехватом входов движков во время их
собственных юнит-тестов (сотни настоящих запросов) + несколько запросов,
собранных как в C#. Переменные C#, которые держат НЕ ответ движка (метки
Xrecord, _fzones.json, определения отчётов), перечислены явно — со словом,
откуда они.

Слабое место метода: ключ проверяется «где-нибудь в ответе», не в том же
объекте; опечатку в ключе он ловит, перенос ключа между уровнями — нет.

Запуск (из корня): PYTHONUTF8=1 python3 tools/engine_contract.py
Выход 1, если какой-то ключ C# в ответах не встречается.
"""
import contextlib
import glob
import importlib
import io
import json
import os
import re
import sys
import unittest

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
READ = re.compile(r'\b(?:Get|GetBool|GetD|GetS|D|S|GetDouble|GetBoolFlag|GetDoubleFlag)\(\s*'
                  r'([A-Za-z_][A-Za-z0-9_\.]*)\s*,\s*"([^"]+)"')

# модуль → (C#-файлы, переменные C# с ответом движка, не-ответы с пояснением)
MODULES = {
    "Facades": (["Facades/src/AFacadesPlugin/ZoneCommand.cs", "Facades/src/AFacadesPlugin/TableCommand.cs"],
                {"res", "z", "rep", "d", "fd", "sum", "hd", "bd"},
                {"zd": "_fzones.json (запись C#)", "a": "сортировка строк таблицы", "b": "сортировка"}),
    "AClad/ATCLAD": (["AClad/src/ACladPlugin/CladCommand.cs"],
                     {"res", "it", "sum", "d", "pzd"},
                     {"z": "Xrecord ATFZONE", "part": "_fzones.json", "zd": "_fzones.json", "rep": "Xrecord ATFZONE",
                      "od": "_fzones.json", "Value": "группировка C#", "d.handles": "метка ATCLAD",
                      "meta": "метка раскладки (признак refs, 23.09n)"}),
    "AFrame": (["AFrame/src/AFramePlugin/FrameCommand.cs"],
               {"sum", "res", "r", "it", "rep", "c", "steps", "prof", "ch", "sysUsed"},
               {"m": "метки ATCLAD/ATTILE", "d": "метка ATFRAME", "zd": "_fzones.json", "part": "_fzones.json",
                "od": "_fzones.json (проёмы зоны)", "Value": "группировка C#",
                "meta": "метка ATFRAME (признак refs, 23.09n)"}),
    "ABlockGen": (["ABlockGen/src/ABlockGenPlugin/VitrageCommand.cs",
                   "ABlockGen/src/ABlockGenPlugin/RecognizeCommand.cs"],
                  {"it", "plan", "dd", "sum"}, {}),
    "ATableSpec": (["ATableSpec/src/AtSpecPlugin/ReportCommand.cs", "ATableSpec/src/AtSpecPlugin/ReportReactor.cs",
                    "ATableSpec/src/AtSpecPlugin/Commands.cs", "ATableSpec/src/AtSpecPlugin/QueryForm.cs"],
                   {"result", "rep", "d", "meta"},
                   {"ReportDef": "определение отчёта (форма)", "defDict": "определение (Xrecord таблицы)",
                    "pd": "пресеты формы", "sd": "определение секции"}),
}


def cs_reads(files):
    out = {}
    for f in files:
        for var, key in READ.findall(open(os.path.join(ROOT, f), encoding="utf-8").read()):
            out.setdefault(var.split(".")[-1], set()).add(key)
    return out


def all_keys(obj, acc):
    if isinstance(obj, dict):
        for k, v in obj.items():
            acc.add(k)
            all_keys(v, acc)
    elif isinstance(obj, (list, tuple)):
        for v in obj:
            all_keys(v, acc)
    return acc


def capture(module_dir, engine_mod, fn_names, test_files, extra):
    """Перехват ответов движка во время его юнит-тестов (+ extra-запросы)."""
    sys.path.insert(0, os.path.join(ROOT, module_dir))
    eng = importlib.import_module(engine_mod)
    got = []
    for fn in fn_names:
        orig = getattr(eng, fn)

        def wrap(*a, _orig=orig, **k):
            r = _orig(*a, **k)
            got.append(r)
            return r
        setattr(eng, fn, wrap)
    cwd = os.getcwd()
    os.chdir(os.path.join(ROOT, module_dir))
    try:
        for tf in test_files:
            name = os.path.splitext(os.path.basename(tf))[0]
            sys.modules.pop(name, None)
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                try:
                    mod = importlib.import_module(name)
                    suite = unittest.defaultTestLoader.loadTestsFromModule(mod)
                    if suite.countTestCases():
                        unittest.TextTestRunner(stream=io.StringIO(), verbosity=0).run(suite)
                except SystemExit:
                    pass
        for fn, req in extra:
            got.append(getattr(eng, fn)(json.loads(json.dumps(req))))
    finally:
        os.chdir(cwd)
    return got


def rect(x0, y0, x1, y1):
    return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]


def responses():
    res = {}
    res["Facades"] = capture("Facades/engine", "facades_engine", ["run", "op_zones"],
                             ["test_facades_engine.py"],
                             [("run", {"op": "zones", "units": "mm", "cladding": "КГ", "zone_prefix": "Ф-",
                                       "start_index": 1, "merge": m,
                                       "contours": [{"id": "A", "pts": rect(0, 0, 6000, 3000)},
                                                    {"id": "B", "pts": rect(1000, 0, 2000, 2100)},
                                                    {"id": "C", "pts": rect(6000, 0, 9000, 3000)},
                                                    {"id": "X", "pts": [[0, 0], [10, 10], [10, 0], [0, 10]]}]})
                              for m in (False, True)])
    res["AClad/ATCLAD"] = capture("AClad/engine", "clad_engine", ["run", "op_cladding"],
                                  ["test_clad_engine.py", "test_cladding_plan.py"],
                                  [("run", {"op": "cladding", "tile": {"w": 600, "h": 600}, "gap": {"v": 10, "h": 10},
                                            "origin": {"y": 0}, "vjoints": [], "hjoints": [],
                                            "contours": [{"id": "A", "pts": rect(0, 0, 6000, 3000)},
                                                         {"id": "B", "pts": rect(1000, 900, 2400, 2400)}]})])
    res["AFrame"] = capture("AFrame/engine", "frame_engine", ["run", "op_frame"],
                            ["test_frame_engine.py"],
                            [("run", {"op": "frame", "sub_type": st, "system": sysn,
                                      "contours": [{"id": "A", "pts": rect(0, 0, 6000, 6000)},
                                                   {"id": "W", "pts": rect(2000, 900, 3400, 2400)}],
                                      "joints_x": [305.0 + 610 * k for k in range(10)],
                                      "rows_y": [605.0 * k for k in range(1, 10)], "floors_y": fl,
                                      "floor_step": 3000.0,
                                      "calc": {"wind_region": "II", "terrain": "B", "height": 25, "q_clad": 30,
                                               "offset": 150, "na_max": 1880}})
                             for st, sysn, fl in (("vertical", "Standart", []), ("vertical", "Standart", [3000.0]),
                                                  ("interfloor", "Межэтажная", [3000.0]),
                                                  ("ortho", "Ортогональная", []))])
    res["ABlockGen"] = capture("ABlockGen/engine", "vitrage_recognize", ["recognize"],
                               ["test_vitrage_recognize.py"], [])
    res["ABlockGen"] += capture("ABlockGen/engine", "vitrage_plan", ["build_plan"],
                                ["test_vitrage_plan.py"], [])
    # C# читает ответ CLI vitrage_engine (ошибки входа — {ok:false, error}), а не исключение build_plan
    import tempfile
    import vitrage_engine
    for bad_req in ({"op": "plan", "opening": {"x0": 0, "y0": 0, "x1": 10, "y1": 10}}, {"op": "recognize"}):
        with tempfile.TemporaryDirectory() as td:
            ip, op_ = os.path.join(td, "in.json"), os.path.join(td, "out.json")
            json.dump(bad_req, open(ip, "w", encoding="utf-8"))
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                try:
                    vitrage_engine.main(["vitrage_engine", ip, op_])   # main ждёт sys.argv целиком
                except SystemExit:
                    pass
            if os.path.exists(op_):
                res["ABlockGen"].append(json.load(open(op_, encoding="utf-8")))
    # ATableSpec: вход C# — dxf_spec.engine_json (describe / run / report)
    sys.path.insert(0, os.path.join(ROOT, "ATableSpec/engine"))
    import yaml
    import dxf_spec
    cfg = yaml.safe_load(open(os.path.join(ROOT, "ATableSpec/engine/mapping.yaml"), encoding="utf-8"))
    blocks = [{"name": "RF-стойка", "layer": "RF-стойки", "x": 0, "y": 0, "rotation": 0, "xscale": 1, "yscale": 1,
               "attributes": {"ИМЯ": "С1", "ПРОФ": "01_03_06", "ДЛИНА": "3495"}},
              {"name": "RF-ригель", "layer": "RF-ригеля", "x": 0, "y": 0, "rotation": 0, "xscale": 1, "yscale": 1,
               "attributes": {"ИМЯ": "Р1", "ПРОФ": "01_02_04", "ДЛИНА": "705"}}]
    rdef = {"title": "T", "sections": [{"section_title": "С", "header": ["№", "Марка"], "header_merges": [[0, 1]],
                                        "columns": ["=row", "=Object.«ИМЯ»"], "group_by": 1, "filter": []}]}
    at = [dxf_spec.engine_json({"blocks": blocks, "action": "describe"}, cfg),
          dxf_spec.engine_json({"blocks": blocks, "action": "run"}, cfg),
          dxf_spec.engine_json({"blocks": blocks, "action": "report", "report": rdef}, cfg)]
    presets = [r.get("name") for r in cfg.get("reports", [])][:1]
    if presets:
        at.append(dxf_spec.engine_json({"blocks": blocks, "action": "run", "report": presets[0]}, cfg))
    res["ATableSpec"] = at
    return res


def main():
    resp = responses()
    bad = 0
    for mod, (files, rvars, skip) in MODULES.items():
        reads = cs_reads(files)
        keys = set()
        for r in resp[mod]:
            all_keys(r, keys)
        miss = []
        n_read = 0
        for var, ks in sorted(reads.items()):
            if var not in rvars:
                continue
            for k in sorted(ks):
                if "%s.%s" % (var, k) in skip:
                    continue
                n_read += 1
                if k not in keys:
                    miss.append("%s.%s" % (var, k))
        other = sorted(v for v in reads if v not in rvars)
        unknown = [v for v in other if v not in skip]
        print("%-13s ответов %4d | ключей C# из ответа %3d | нет в ответах: %s%s"
              % (mod, len(resp[mod]), n_read, ", ".join(miss) if miss else "—",
                 (" | неразмеченные переменные C#: %s" % unknown) if unknown else ""))
        bad += len(miss)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
