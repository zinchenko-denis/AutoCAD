# -*- coding: utf-8 -*-
"""
atspec_report — версионно-независимое ЯДРО табличного движка ATableSpec.

Воспроизводит «Шаблон отчёта» СПДС без зависимости от СПДС:
  • вычислитель выражений над полями блока (Object.«поле», арифметика, литералы,
    конкатенация, Count/Sum/Iff, автонумерация row);
  • раннер шаблона: фильтр источника → разворот по объектам → группировка по
    столбцу → агрегаты (Count/Sum) → сортировка;
  • производные строки: несколько под-шаблонов на один источник (крышка+прижим
    из стойки), результат конкатенируется.

Вход — записи блоков в том же формате, что отдаёт C#-плагин/контракт обмена:
    {"name","layer","attributes":{...}}
Никакой зависимости от AutoCAD/ezdxf здесь нет — это чистая логика, её гоняет
тест без CAD. C# отдаёт записи, рисует результат AcDbTable; пересчёт по правке
блока — реактор C# (ядра не касается).
"""
from __future__ import annotations
import collections
from decimal import Decimal, ROUND_HALF_UP
from typing import Any, Dict, List, Optional

# числовые поля — читаются как float; остальные как строка
_NUMERIC_FIELDS = {"ДЛИНА", "ВЫСОТА", "ШИРИНА", "ДЛИНА,ММ", "ДЛИНА, ММ", "ROTATION"}

# Разделители "Ширина<sep>Высота" в РАЗМЕР_ЗАП. Первый — кириллическая Х (U+0425),
# как в исходных чертежах (ср. mapping.yaml size_separators); латинские/«*» — на всякий.
_SIZE_SEPS = ("\u0425", "X", "\u0445", "x", "*")


def _to_num(x: Any):
    try:
        return float(str(x).replace("\u00a0", "").replace(" ", "").replace(",", "."))
    except ValueError:
        return None


def _split_size(raw: Any):
    """РАЗМЕР_ЗАП вида 'ШиринаХВысота' -> (ширина, высота) как float, иначе (None, None)."""
    if raw is None:
        return (None, None)
    s = str(raw).strip()
    for sep in _SIZE_SEPS:
        if sep in s:
            parts = s.split(sep)
            if len(parts) == 2:
                w, h = _to_num(parts[0]), _to_num(parts[1])
                if w is not None or h is not None:
                    return (w, h)
    return (None, None)


def _nk(s: Any) -> str:
    """Нормализованный ключ поля: без «», без крайних пробелов, в верхнем регистре.
    Имена динамических параметров приходят из C# «как есть» (возможны иной регистр или
    хвостовой пробел), поэтому поиск поля делаем по нормализованному ключу."""
    return str(s).replace("«", "").replace("»", "").strip().upper()


# ─────────────────────────── объект (одна вставка блока) ───────────────────────────
class Obj:
    """Обёртка над записью блока: доступ к Name / Layer / любому атрибуту."""
    def __init__(self, rec: dict):
        self.attrs = {str(k): ("" if v is None else str(v)) for k, v in (rec.get("attributes") or {}).items()}
        self.layer = str(rec.get("layer", ""))
        # Нормализованный поиск полей: ключ без «», без крайних пробелов, в ВЕРХНЕМ
        # регистре. Так Object.«Длина» находит и ATTRIB ДЛИНА, и динам. параметр
        # "Длина"/"Длина "/"длина" (имена ручек приходят из C# как есть). Непустое в приоритете.
        self._norm: Dict[str, str] = {}
        for k, v in self.attrs.items():
            nk = _nk(k)
            if nk not in self._norm or self._norm[nk] == "":
                self._norm[nk] = v
        # Object.Name = эффективное имя блока. У этих блоков оно = профиль (ПРОФ);
        # в C# можно резолвить настоящее имя динамоблока — здесь берём ПРОФ как прокси.
        self._name = self._norm.get("ПРОФ") or str(rec.get("name", ""))

    def field(self, name: str) -> Any:
        up = _nk(name)
        if up in ("ИМЯ_БЛОКА", "NAME"):
            return self._name
        if up in ("СЛОЙ", "LAYER"):
            return self.layer
        raw = self._norm.get(up)
        # Ширина/Высота заполнения: отдельных атрибутов у блока нет — достаём из
        # РАЗМЕР_ЗАП ("ШиринаХВысота"). Прямое поле, если оно есть и непустое, в приоритете.
        if up in ("ШИРИНА", "ВЫСОТА") and (raw is None or str(raw).strip() == ""):
            w, h = _split_size(self._norm.get("РАЗМЕР_ЗАП"))
            return w if up == "ШИРИНА" else h
        if raw is None:
            return None
        if up == "ДЛИНА" or up in {_nk(f) for f in _NUMERIC_FIELDS}:
            try:
                return float(str(raw).replace(",", "."))
            except ValueError:
                return None
        return raw


# ─────────────────────────────── вычислитель выражений ───────────────────────────────
class _Tok:
    __slots__ = ("k", "v")
    def __init__(self, k, v): self.k, self.v = k, v


def _tokenize(s: str) -> List[_Tok]:
    s = s.strip()
    if s.startswith("="):
        s = s[1:]
    out, i, L = [], 0, len(s)
    while i < L:
        c = s[i]
        if c in " \t":
            i += 1; continue
        if c == "«":
            j = s.index("»", i); out.append(_Tok("STR", s[i + 1:j])); i = j + 1; continue
        if c == '"':
            j = s.index('"', i + 1); out.append(_Tok("STR", s[i + 1:j])); i = j + 1; continue
        two = s[i:i + 2]
        if two in ("<=", ">=", "<>"):
            out.append(_Tok("CMP", two)); i += 2; continue
        if c in "<>=":
            out.append(_Tok("CMP", "=" if c == "=" else c)); i += 1; continue
        if c in "+-*/(),":
            out.append(_Tok(c, c)); i += 1; continue
        if c == "." :
            out.append(_Tok(".", ".")); i += 1; continue
        if c.isdigit():
            j = i
            while j < L and (s[j].isdigit() or s[j] == "."):
                j += 1
            out.append(_Tok("NUM", float(s[i:j]))); i = j; continue
        if c.isascii() and (c.isalpha() or c == "_"):
            j = i
            while j < L and (s[j].isalnum() or s[j] == "_"):
                j += 1
            out.append(_Tok("ID", s[i:j])); i = j; continue
        raise ValueError(f"непонятный символ {c!r} в выражении {s!r}")
    out.append(_Tok("END", None))
    return out


class _Eval:
    """Рекурсивный спуск. Грамматика: cmp < add < mul < unary < primary."""
    def __init__(self, toks, obj: Optional[Obj], group: List[Obj], rownum: int):
        self.t, self.i = toks, 0
        self.obj, self.group, self.rownum = obj, group, rownum

    def _peek(self): return self.t[self.i]
    def _eat(self, k=None):
        tk = self.t[self.i]
        if k and tk.k != k:
            raise ValueError(f"ожидал {k}, нашёл {tk.k}")
        self.i += 1; return tk

    def run(self):
        v = self._cmp()
        if self._peek().k != "END":
            raise ValueError("лишние токены в выражении")
        return v

    def _cmp(self):
        v = self._add()
        while self._peek().k == "CMP":
            op = self._eat().v; r = self._add()
            v = {"=": v == r, "<": v < r, ">": v > r,
                 "<=": v <= r, ">=": v >= r, "<>": v != r}[op]
        return v

    def _add(self):
        v = self._mul()
        while self._peek().k in ("+", "-"):
            op = self._eat().k; r = self._mul()
            if op == "+":
                v = (v + r) if isinstance(v, (int, float)) and isinstance(r, (int, float)) else (str(v) + str(r))
            else:
                v = v - r
        return v

    def _mul(self):
        v = self._un()
        while self._peek().k in ("*", "/"):
            op = self._eat().k; r = self._un()
            v = v * r if op == "*" else v / r
        return v

    def _un(self):
        if self._peek().k == "-":
            self._eat(); return -self._prim()
        return self._prim()

    def _prim(self):
        tk = self._peek()
        if tk.k == "NUM": self._eat(); return tk.v
        if tk.k == "STR": self._eat(); return tk.v
        if tk.k == "(":
            self._eat("("); v = self._cmp(); self._eat(")"); return v
        if tk.k == "ID":
            name = self._eat().v
            low = name.lower()
            if name == "Object":
                self._eat(".")
                nx = self._peek()
                if nx.k == "ID":
                    fld = self._eat().v
                    return None if self.obj is None else self.obj.field(fld)
                if nx.k == "STR":
                    fld = self._eat().v
                    return None if self.obj is None else self.obj.field(fld)
                raise ValueError("после Object. ждём Name или поле")
            if low == "count":
                if self._peek().k == "(":
                    self._eat("("); self._eat(")")
                return len(self.group)
            if low == "row":
                return self.rownum
            if low == "sum":
                self._eat("("); inner = self._capture_arg(); self._eat(")")
                return sum(_Eval(_clone(inner), o, self.group, self.rownum).run() for o in self.group)
            if low == "iff":
                self._eat("(")
                cond = self._cmp(); self._eat(","); a = self._cmp(); self._eat(","); b = self._cmp()
                self._eat(")")
                return a if cond else b
            raise ValueError(f"неизвестный идентификатор {name!r}")
        raise ValueError(f"неожиданный токен {tk.k}")

    def _capture_arg(self):
        """Сырые токены одного аргумента до запятой/скобки (для Sum по группе)."""
        depth, start = 0, self.i
        while True:
            tk = self._peek()
            if tk.k == "(": depth += 1
            elif tk.k == ")":
                if depth == 0: break
                depth -= 1
            elif tk.k == "," and depth == 0:
                break
            elif tk.k == "END":
                break
            self.i += 1
        return self.t[start:self.i]


def _clone(toks):
    return list(toks) + [_Tok("END", None)]


def evaluate(expr: str, obj: Optional[Obj], group: List[Obj], rownum: int) -> Any:
    # Ячейка без ведущего «=» — это литерал-текст (как в СПДС/Excel): пустая ячейка
    # даёт "", «Примечание» — просто текст. Формула вычисляется только при «=».
    s = (expr or "").strip()
    if not s.startswith("="):
        return s
    return _Eval(_tokenize(s), obj, group, rownum).run()


# ─────────────────────────────── раннер шаблона отчёта ───────────────────────────────
def _num_or_none(s: Any):
    """float из строки или None. Парсим ТОЛЬКО чистое число (опц. знак, один
    разделитель «.» или «,»). Нужно, чтобы фильтр «=»/«≠» сравнивал числа численно:
    длина в атрибуте «3495.00» сводится к float → str «3495.0»; значение из выпадушки —
    «3495.00» (или «3495» как в таблице). Строковое сравнение их не сводит — это и был
    баг фильтра по длине. Идентификаторы (С01, 01_03_06) в число НЕ парсятся (буквы/«_»)
    → для них остаётся строгое строковое сравнение."""
    t = str(s).strip().replace(",", ".")
    body = t[1:] if t[:1] in "+-" else t
    if body and body.replace(".", "", 1).isdecimal():
        try:
            return float(t)
        except ValueError:
            return None
    return None


def _passes(obj: Obj, flt: List[dict]) -> bool:
    for f in flt or []:
        fld, op, val = f.get("field"), f.get("op", "="), f.get("value", "")
        lhs = obj.field(fld)
        ls = "" if lhs is None else str(lhs).strip()
        vs = str(val).strip()
        if op in ("=", "=="):
            a, b = _num_or_none(ls), _num_or_none(vs)
            ok = (a == b) if (a is not None and b is not None) else (ls.lower() == vs.lower())
        elif op in ("!=", "<>", "≠"):
            a, b = _num_or_none(ls), _num_or_none(vs)
            ok = (a != b) if (a is not None and b is not None) else (ls.lower() != vs.lower())
        elif op in ("contains", "содержит"):
            ok = vs.lower() in ls.lower()
        elif op in ("not_contains", "не содержит"):
            ok = vs.lower() not in ls.lower()
        elif op in (">", "<", ">=", "<=", "≥", "≤"):
            try:
                a, b = float(ls.replace(",", ".")), float(vs.replace(",", "."))
            except ValueError:
                return False
            op2 = {"≥": ">=", "≤": "<="}.get(op, op)
            ok = {">": a > b, "<": a < b, ">=": a >= b, "<=": a <= b}[op2]
        else:
            ok = True
        if not ok:
            return False
    return True


def _fmt_num(v: float, decimals: bool):
    """float -> значение ячейки.

    По умолчанию (decimals=False) — целое: дробную часть динам. параметров
    отбрасываем, округляя к ближайшему (·.5 — от нуля). Длины/количество.
    decimals=True — для столбцов с делением (площадь и т.п.): 2 знака,
    округление половин ВВЕРХ как в СПДС/Excel (через Decimal(str(v)), иначе
    float-арифметика даёт 4.275->4.27), запятая-десятичная, хвостовые нули
    срезаются (0.61875->«0,62», 10.798->«10,8», 3.0->3).
    """
    if not decimals:
        return int(v + 0.5) if v >= 0 else -int(-v + 0.5)
    d = Decimal(str(v)).quantize(Decimal("0.01"), rounding=ROUND_HALF_UP)
    if d == d.to_integral_value():
        return int(d)
    return format(d, "f").rstrip("0").rstrip(".").replace(".", ",")


def run_template(records: List[dict], tmpl: dict) -> List[List[Any]]:
    """Один шаблон отчёта -> строки.

    tmpl = {
      "filter":   [{"field","op","value"}, ...],   # источник: вхождение блока + условия
      "columns":  ["=row-1","=Object.«ИМЯ»", ...], # выражения по столбцам
      "group_by": <индекс столбца> | None,          # группировать строки по столбцу
      "sort_by":  (<индекс>, "asc"|"desc") | None,
    }
    """
    objs = [Obj(r) for r in records if _passes(Obj(r), tmpl.get("filter", []))]
    cols: List[str] = tmpl["columns"]
    gi = tmpl.get("group_by")

    # 1) группировка по значению столбца group_by (вычисленному на объекте)
    if gi is None:
        groups = [[o] for o in objs]
    else:
        buckets: "collections.OrderedDict[Any, List[Obj]]" = collections.OrderedDict()
        for o in objs:
            key = evaluate(cols[gi], o, [o], 0)
            buckets.setdefault(key, []).append(o)
        groups = list(buckets.values())

    # 2) сортировка ГРУПП (до нумерации) — чтобы №п/п (=row) шёл по порядку строк
    sb = tmpl.get("sort_by")
    if sb:
        col, direction = sb
        groups.sort(key=lambda g: _sortkey(evaluate(cols[col], g[0], g, 0)),
                    reverse=(direction == "desc"))

    # 3) строка на группу в финальном порядке; Count/Sum видят всю группу,
    #    row — порядковый номер уже ПОСЛЕ сортировки. Числа: целые, кроме
    #    столбцов с делением (площадь и т.п.) — у тех 2 знака с запятой.
    rows: List[List[Any]] = []
    decs = ["/" in (c or "") for c in cols]
    for idx, grp in enumerate(groups, 1):
        rep = grp[0]
        row = []
        for ci, expr in enumerate(cols):
            try:
                v = evaluate(expr, rep, grp, idx)
            except Exception:
                v = None        # битое выражение в одной ячейке не роняет таблицу
            if isinstance(v, float):
                v = _fmt_num(v, decs[ci])
            row.append(v)
        rows.append(row)

    # строка ИТОГ (опц.): суммируем столбцы, чьё выражение содержит Count
    # (как «сумма» в СПДС: под Кол-во и Площадь — суммы, под №/Ш/В — пусто).
    if tmpl.get("total_row"):
        label = tmpl.get("total_label") or "сумма"
        total: List[Any] = []
        labeled = False
        for ci, expr in enumerate(cols):
            if "count" in (expr or "").lower():
                acc = 0.0
                for j, gj in enumerate(groups, 1):
                    try:
                        vv = evaluate(expr, gj[0], gj, j)
                    except Exception:
                        vv = None
                    if isinstance(vv, (int, float)):
                        acc += vv
                total.append(_fmt_num(float(acc), decs[ci]))
            elif not labeled:
                total.append(label); labeled = True
            else:
                total.append("")
        rows.append(total)
    return rows


def _sections_of(report: dict) -> List[dict]:
    """Нормализует определение к списку секций.

    Новый формат (Фаза 2): report["sections"] = [{section_title, header,
    hide_header, header_merges, columns, filter, group_by, sort_by}, ...] —
    у каждой секции СВОИ столбцы/шапка/источник-фильтр/группировка.

    Старый формат (этап 1): report["header"]/["templates"] — общая шапка на весь
    отчёт + шаблоны. Заворачиваем каждый шаблон в секцию с общей шапкой, чтобы
    уже вставленные в чертёж таблицы пересчитывались без изменений раскладки.
    """
    secs = report.get("sections")
    if secs:
        return secs
    shared_header = report.get("header", [])
    hh = bool(report.get("hide_header", False))
    hm = report.get("header_merges", [])
    out = []
    for t in report.get("templates", []):
        out.append({"section_title": "", "header": shared_header,
                    "hide_header": hh, "header_merges": hm,
                    "columns": t.get("columns", []), "filter": t.get("filter", []),
                    "group_by": t.get("group_by"), "sort_by": t.get("sort_by")})
    return out


def run_report(records: List[dict], report: dict) -> Dict[str, Any]:
    """Отчёт = заголовок таблицы + 1..N секций (стойки, ригеля, ... в одной таблице).

    Возвращает:
      sections — [{title, header, hide_header, header_merges, rows}] на каждую секцию
                 (нумерация =row внутри секции — своя, т.к. это отдельный шаблон);
      rows/header — плоско (конкатенация секций), для совместимости со старыми
                 потребителями и тестами.
    """
    out_sections: List[Dict[str, Any]] = []
    flat: List[List[Any]] = []
    for s in _sections_of(report):
        rows = run_template(records, {
            "filter": s.get("filter", []), "columns": s.get("columns", []),
            "group_by": s.get("group_by"), "sort_by": s.get("sort_by"),
            "total_row": s.get("total_row", False),
            "total_label": s.get("total_label", "сумма")})
        out_sections.append({
            "title": s.get("section_title", "") or "",
            "header": s.get("header", []),
            "hide_header": bool(s.get("hide_header", False)),
            "header_merges": s.get("header_merges", []),
            "rows": rows})
        flat.extend(rows)
    top_header = out_sections[0]["header"] if out_sections else report.get("header", [])
    return {"title": report.get("title", ""),
            "header": top_header,      # совместимость (плоско)
            "rows": flat,              # совместимость (конкатенация секций)
            "sections": out_sections}  # Фаза 2


def _sortkey(v):
    if v is None:
        return (2, "")
    if isinstance(v, (int, float)):
        return (0, v)
    return (1, str(v))
