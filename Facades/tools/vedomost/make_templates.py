# -*- coding: utf-8 -*-
"""Формы ведомостей работ для ATFTABLE (29.09n, просьба Германа) — очищенные копии его примеров.

Оригиналы (приватно): atspec-testdata/facades/vedomost/{shtukaturka,vent}_primer_german_2909.xlsx.
Что делаем, чтобы форму можно было класть в бандл и публичный репозиторий:
- убираем внешнюю ссылку (у вент-формы — книга «Прайс»: 551 КБ кэша чужих данных; Excel ещё и
  спрашивал бы «обновить связи?») и её definedName;
- убираем calcChain и printerSettings (имя принтера), автора (ФИО) в docProps/core.xml;
- чистим ЧИСЛА ПРИМЕРА в клетках ввода (их заполнит ATFTABLE или человек) и кэш формул;
- ставим fullCalcOnLoad — Excel пересчитает формулы при открытии.
Запуск (из корня репо; atspec-testdata рядом): python3 Facades/tools/vedomost/make_templates.py
"""
import io
import os
import re
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
SRC = os.path.normpath(os.path.join(ROOT, "..", "atspec-testdata", "facades", "vedomost"))
DST = os.path.join(ROOT, "Facades", "bundle", "AFacades.bundle", "Contents", "templates")

JOBS = [("shtukaturka_primer_german_2909.xlsx", "shtukaturka.xlsx", ["D3", "D9", "D10", "D11", "D12"]),
        ("vent_primer_german_2909.xlsx", "vent.xlsx", ["D3", "D11", "D13", "D16", "D19", "D20"])]


def clean(src, dst, inputs):
    zin = zipfile.ZipFile(src)
    drop = {n for n in zin.namelist()
            if n.startswith("xl/externalLinks/") or n == "xl/calcChain.xml" or n.startswith("xl/printerSettings/")}
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zo:
        for it in zin.infolist():
            n = it.filename
            if n in drop:
                continue
            d = zin.read(n)
            if n == "[Content_Types].xml":
                t = d.decode("utf-8")
                t = re.sub(r'<Override PartName="/xl/(externalLinks/[^"]+|calcChain\.xml)"[^>]*/>', "", t)
                t = re.sub(r'<Default Extension="bin"[^>]*/>', "", t)
                d = t.encode("utf-8")
            elif n == "xl/_rels/workbook.xml.rels":
                t = d.decode("utf-8")
                t = re.sub(r'<Relationship [^>]*Target="(externalLinks/[^"]+|calcChain\.xml)"[^>]*/>', "", t)
                d = t.encode("utf-8")
            elif n == "xl/worksheets/_rels/sheet1.xml.rels":
                t = re.sub(r'<Relationship [^>]*printerSettings[^>]*/>', "", d.decode("utf-8"))
                d = t.encode("utf-8")
            elif n == "xl/workbook.xml":
                t = d.decode("utf-8")
                t = re.sub(r"<externalReferences>.*?</externalReferences>", "", t, flags=re.S)
                t = re.sub(r'<definedName name="[^"]*">\[\d+\][^<]*</definedName>', "", t)
                t = re.sub(r"<definedNames>\s*</definedNames>", "", t)
                t = re.sub(r"<calcPr([^>]*?)/>", lambda m: "<calcPr%s fullCalcOnLoad=\"1\"/>" %
                           re.sub(r'\s*fullCalcOnLoad="[^"]*"', "", m.group(1)), t)
                d = t.encode("utf-8")
            elif n == "xl/worksheets/sheet1.xml":
                t = d.decode("utf-8")
                t = re.sub(r'<pageSetup([^>]*?)\s+r:id="[^"]*"', r"<pageSetup\1", t)
                for a in inputs:                     # клетки ввода — пустые (стиль остаётся)
                    t, k = re.subn(r'<c r="%s"( s="\d+")?[^>]*?(?:/>|>.*?</c>)' % a,
                                   lambda m: '<c r="%s"%s/>' % (a, m.group(1) or ""), t, count=1)
                    assert k == 1, (src, a)
                t = re.sub(r"(<f>[^<]*</f>)<v>[^<]*</v>", r"\1", t)     # кэш формул
                d = t.encode("utf-8")
            elif n == "docProps/core.xml":
                t = d.decode("utf-8")
                t = re.sub(r"<dc:creator>[^<]*</dc:creator>", "<dc:creator>AFacades</dc:creator>", t)
                t = re.sub(r"<cp:lastModifiedBy>[^<]*</cp:lastModifiedBy>", "<cp:lastModifiedBy>AFacades</cp:lastModifiedBy>", t)
                d = t.encode("utf-8")
            zo.writestr(it, d)
    with open(dst, "wb") as f:
        f.write(out.getvalue())


if __name__ == "__main__":
    for s, d, inputs in JOBS:
        src = os.path.join(SRC, s)
        if not os.path.exists(src):
            sys.exit("нет оригинала %s (нужен atspec-testdata рядом с репо)" % src)
        clean(src, os.path.join(DST, d), inputs)
        print("форма:", d, os.path.getsize(os.path.join(DST, d)), "Б")
