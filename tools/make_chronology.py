# -*- coding: utf-8 -*-
"""Полный указатель хронологии проекта для ревизора (30.09): по дням — коммиты (git log), заголовки
записей журналов фасадных модулей (Facades/AClad/AFrame docs/NEXT.md, раздел «Статус»), теги сборок
build-N; в конце — все «ГРАБЛИ» из документов. Повествовательная хронология — docs/CHRONOLOGY.md.
Запуск (из корня репо): PYTHONUTF8=1 python3 tools/make_chronology.py > docs/CHRONOLOGY_INDEX.md"""
import collections
import io
import os
import re
import subprocess

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))


def sh(*args):
    return subprocess.check_output(args, cwd=ROOT).decode("utf-8", "replace")


def main():
    days = collections.OrderedDict()
    for line in sh("git", "log", "--reverse", "--format=%ad\x1f%h\x1f%s", "--date=short").splitlines():
        d, h, subj = line.split("\x1f", 2)
        days.setdefault(d, {"commits": [], "journal": [], "tags": []})["commits"].append((h, subj))
    for line in sh("git", "for-each-ref", "--sort=creatordate", "--format=%(creatordate:short)\x1f%(refname:short)"
                   "\x1f%(objectname:short)", "refs/tags").splitlines():
        d, tag, obj = line.split("\x1f")
        days.setdefault(d, {"commits": [], "journal": [], "tags": []})["tags"].append((tag, obj))
    pat = re.compile(r"^- \*\*(\d{1,2})\.(\d{2})([a-zа-я0-9\-]*)")
    for mod in ("Facades", "AClad", "AFrame"):
        s = io.open(os.path.join(ROOT, mod, "docs", "NEXT.md"), encoding="utf-8").read()
        i = s.find("## Статус")
        for ln in (s[i:] if i >= 0 else "").splitlines():
            m = pat.match(ln)
            if not m:
                continue
            d = "2026-%s-%02d" % (m.group(2), int(m.group(1)))
            head = re.sub(r"\*\*", "", ln[2:]).strip()
            days.setdefault(d, {"commits": [], "journal": [], "tags": []})["journal"].append(
                (mod, head[:230] + ("…" if len(head) > 230 else "")))
    out = ["# Указатель хронологии (генерируется: `tools/make_chronology.py`)", "",
           "> История проекта. Слова «текущий» и «следующий» ниже относятся к датам",
           "> записей и не задают новую задачу. Актуальный статус, ограничения и",
           "> порядок работ — только в [CURRENT_MILESTONES.md](CURRENT_MILESTONES.md).", "",
           "Повествование, фазы, сборки и люди — `docs/CHRONOLOGY.md`. Здесь — ВСЁ по дням: теги сборок,",
           "заголовки исторических записей NEXT.md, коммиты (тема",
           "полностью; хэш — для `git show`). Журналы ATableSpec/ABlockGen устроены по-другому (история по",
           "сессиям) — их хронология здесь по коммитам.", ""]
    for d in sorted(days):
        e = days[d]
        out.append("## %s — коммитов %d" % (d, len(e["commits"])))
        for tag, obj in e["tags"]:
            out.append("- **тег `%s`** → `%s`" % (tag, obj))
        for mod, head in e["journal"]:
            out.append("- журнал %s: %s" % (mod, head))
        for h, subj in e["commits"]:
            out.append("- `%s` %s" % (h, subj))
        out.append("")
    out += ["## Грабли (ГРАБЛЯ-N) — где описаны", ""]
    seen = set()
    for dp, _dn, fns in os.walk(ROOT):
        if "/.git" in dp:
            continue
        for fn in sorted(fns):
            if not fn.endswith(".md") or fn == "CHRONOLOGY_INDEX.md":
                continue
            p = os.path.join(dp, fn)
            for k, ln in enumerate(io.open(p, encoding="utf-8", errors="replace").read().splitlines(), 1):
                for m in re.finditer(r"ГРАБЛЯ-\d+", ln):
                    key = (m.group(0), os.path.relpath(p, ROOT))
                    if key in seen:
                        continue
                    seen.add(key)
                    out.append("- %s — `%s:%d`: %s" % (m.group(0), key[1], k, ln.strip()[:160]))
    print("\n".join(out))


if __name__ == "__main__":
    main()
