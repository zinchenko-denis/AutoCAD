"""Проверка меток раскладки/подсистемы (23.09n, замечание Германа по сборке №24:
«COPY зоны вместе с раскладкой — на копии новая раскладка ложится поверх старой»).

C# здесь не исполнить (нет AutoCAD), поэтому проверяем исходники: каждая запись
метки ATTILE/ATCLAD/ATFRAME должна нести мягкие ссылки на свои объекты (по ним
на копии узнаются клоны), в метке — признак "refs", скопированная метка разбирается
через CloneHandles, а зона, чья штриховка сдвинута относительно _fzones.json,
не раскладывается по геометрии оригинала. Запуск: python3 tools/label_refs_check.py"""
import re, sys, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
def src(p): return open(os.path.join(ROOT, p), encoding="utf-8").read()

fails = []
def ok(cond, msg):
    if not cond: fails.append(msg)

files = {"AClad/src/ACladPlugin/TilePatternCommand.cs": "XKeyTile",
         "AClad/src/ACladPlugin/CladCommand.cs": "XKeyClad",
         "AFrame/src/AFramePlugin/FrameCommand.cs": "XKeyFrame"}
for f, key in files.items():
    s = src(f)
    calls = re.findall(r"StoreData\(tr,[^;]*?" + key + r"[^;]*?\);", s)
    ok(len(calls) > 0, f"{f}: не найдена запись метки {key}")
    for c in calls:
        args = c[len("StoreData("):-2].split(",")
        ok(len(args) >= 5, f"{f}: метка {key} пишется без ссылок: {c.strip()[:90]}")
    ok(re.search(r'"refs"\s*[,\]]', s) is not None, f"{f}: в метке нет признака \"refs\"")

ac, fr, tp = (src("AClad/src/ACladPlugin/CladCommand.cs"), src("AFrame/src/AFramePlugin/FrameCommand.cs"),
              src("AClad/src/ACladPlugin/TilePatternCommand.cs"))
for name, s in (("AClad", ac), ("AFrame", fr)):
    ok("DxfCode.SoftPointerId" in s and "XlateReferences = true" in s, f"{name}: ссылки 330 / XlateReferences не пишутся")
    ok("internal static List<string> CloneHandles" in s, f"{name}: нет CloneHandles")
    ok("internal static bool ZoneShifted" in s, f"{name}: нет проверки сдвига зоны")
ok("CladCommand.CloneHandles(tr, ent, key, m, l)" in tp and "foreach (var kv in Clones)" in tp,
   "ATTILE/ATCLAD: скопированная метка не разбирается на клоны или клоны не удаляются")
ok("CloneHandles(tr, ent, XKeyFrame, d, lh)" in fr and "CarrierRoot(" in fr,
   "ATFRAME: скопированная метка не разбирается на клоны")
ok("ZoneShifted(parts, he)" in tp and "ZoneShifted(parts, he)" in fr, "ATTILE/ATFRAME: сдвиг зоны не проверяется")

# логика клонов (та же, что в C#): ссылка, чьего хэндла нет среди строк метки, — клон
def clones(label_handles, ref_handles):
    own = {h.upper() for h in label_handles}
    return [h for h in ref_handles if h.upper() not in own]
ok(clones(["1A", "1B"], ["1A", "1B"]) == [], "копия без плиток: клонов быть не должно")
ok(clones(["1A", "1B"], ["2F", "30"]) == ["2F", "30"], "копия с плитками: оба клона")
ok(clones(["1A", "1B"], ["1A", "30"]) == ["30"], "копия части плиток: только скопированная")

if fails:
    print("label_refs_check: НАРУШЕНИЯ"); [print("  -", m) for m in fails]; sys.exit(1)
print("label_refs_check: OK — метки со ссылками (3 команды), клоны копии, сдвиг зоны")
