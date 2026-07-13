# -*- coding: utf-8 -*-
"""VitrageGen: единая точка входа движка (замораживается в vitrage_engine.exe).

CLI: vitrage_engine.py <req.json|-> [out.json]
Роутинг по req["op"]:
  "plan"      → vitrage_plan.build_plan   (Э1: сетка из проёма и параметров)
  "recognize" → vitrage_recognize.recognize (Э3: каркас из АР-графики)
Выходной формат общий: {ok, inserts[], summary, notes[]} | {ok:false, error}.
"""
import json
import sys

from vitrage_plan import build_plan
from vitrage_recognize import recognize

OPS = {"plan": build_plan, "recognize": recognize}


def run(req):
    op = (req.get("op") or "plan").strip().lower()
    fn = OPS.get(op)
    if fn is None:
        raise ValueError("неизвестный op: %r (ожидается plan | recognize)" % op)
    return fn(req)


def main(argv):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    if len(argv) < 2:
        print(json.dumps({"ok": False,
                          "error": "usage: vitrage_engine.py <req.json|-> [out.json]"},
                         ensure_ascii=False))
        return 2
    try:
        if argv[1] == "-":
            req = json.load(sys.stdin)
        else:
            with open(argv[1], "r", encoding="utf-8-sig") as f:
                req = json.load(f)
        res = run(req)
    except ValueError as ex:
        res = {"ok": False, "error": str(ex)}
    except Exception as ex:  # noqa
        res = {"ok": False, "error": "%s: %s" % (type(ex).__name__, ex)}
    text = json.dumps(res, ensure_ascii=False, indent=1)
    if len(argv) > 2:
        with open(argv[2], "w", encoding="utf-8") as f:
            f.write(text)
        print("ok" if res.get("ok") else "error: %s" % res.get("error"))
    else:
        print(text)
    return 0 if res.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
