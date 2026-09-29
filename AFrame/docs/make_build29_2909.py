# -*- coding: utf-8 -*-
"""PDF Герману: сборка №29 (29.09) — раскладка фронтона в ATTILE (ответ 6з), направляющие в 100 мм у
краёв вырезов зоны (6е), верх и низ проёма на краю зоны — откос и отлив (6ж); вопрос по межэтажной.
Картинка — build29/gable_attile.png (make_pics29.py, настоящий прогон движка).
Запуск: python3 make_build29_2909.py [номер_прогона_сборки] [коммит]"""
import json
import os
import sys

from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, Frame, Image, PageTemplate,
                                Paragraph, Spacer, Table, TableStyle)

FDIR = "/usr/share/fonts/truetype/dejavu/"
pdfmetrics.registerFont(TTFont("DV", FDIR + "DejaVuSans.ttf"))
pdfmetrics.registerFont(TTFont("DVB", FDIR + "DejaVuSans-Bold.ttf"))
# <b> в абзацах — настоящим жирным (без семейства шрифтов reportlab рисовал обычным)
pdfmetrics.registerFontFamily("DV", normal="DV", bold="DVB", italic="DV", boldItalic="DVB")
S = dict(
    h1=ParagraphStyle("h1", fontName="DVB", fontSize=15, leading=19, spaceAfter=4 * mm),
    h2=ParagraphStyle("h2", fontName="DVB", fontSize=12.5, leading=16, spaceBefore=5 * mm, spaceAfter=2.5 * mm,
                      keepWithNext=1),
    p=ParagraphStyle("p", fontName="DV", fontSize=10, leading=13.5, spaceAfter=2 * mm),
    li=ParagraphStyle("li", fontName="DV", fontSize=10, leading=13.5, leftIndent=6 * mm, spaceAfter=1.5 * mm),
    url=ParagraphStyle("url", fontName="DV", fontSize=9.5, leading=13, leftIndent=6 * mm, spaceAfter=1.5 * mm),
    cap=ParagraphStyle("cap", fontName="DV", fontSize=8.5, leading=11, alignment=1, spaceAfter=2 * mm),
    td=ParagraphStyle("td", fontName="DV", fontSize=9, leading=11.5),
)
HERE = os.path.dirname(os.path.abspath(__file__))
IMG = os.path.join(HERE, "build29")
BUILD = sys.argv[1] if len(sys.argv) > 1 else "102"
SHA = sys.argv[2] if len(sys.argv) > 2 else "???????"
DL = "https://github.com/zinchenko-denis/AutoCAD/releases/download/latest/"
DLF = "https://github.com/zinchenko-denis/AutoCAD/releases/download/build-%s/" % BUILD


def img(name, width_mm, cap=None):
    from reportlab.lib.utils import ImageReader
    path = os.path.join(IMG, name)
    iw, ih = ImageReader(path).getSize()
    w = width_mm * mm
    out = [Image(path, width=w, height=w * ih / float(iw))]
    if cap:
        out.append(Paragraph(cap, S["cap"]))
    return out


def table(rows, widths):
    data = [[Paragraph(c, S["td"]) for c in r] for r in rows]
    t = Table(data, colWidths=[w * mm for w in widths], repeatRows=1)
    t.setStyle(TableStyle([("GRID", (0, 0), (-1, -1), 0.4, "#9a8f80"),
                           ("BACKGROUND", (0, 0), (-1, 0), "#efe6d6"),
                           ("VALIGN", (0, 0), (-1, -1), "TOP"),
                           ("LEFTPADDING", (0, 0), (-1, -1), 3), ("RIGHTPADDING", (0, 0), (-1, -1), 3)]))
    return t


def P(k, t):
    return Paragraph(t, S[k])


def U(url):
    return Paragraph('<a href="%s" color="#1c5fb0">%s</a>' % (url, url), S["url"])




story = [
    P("h1", "Фасады НВФ — сборка №29 (29.09): раскладка фронтона, направляющие у вырезов, "
            "верх и низ проёма на краю зоны"),
    P("p", "Герман, в этой сборке — твои ответы на вопросы №28: ATTILE теперь <b>раскладывает фронтон</b>; "
           "у краёв вырезов зоны (простенки у входов, уступы) ставится <b>направляющая в 100 мм, как у окна</b>; "
           "верх и низ проёма, лежащие на краю зоны, считаются <b>откосом и отливом</b>."),
    P("h2", "1. Что установить"),
    P("p", "Обновить <b>все три</b>: AClad, AFrame, AFacades. Постоянные ссылки:"),
    U(DL + "AClad.bundle.zip"), U(DL + "AFrame.bundle.zip"), U(DL + "AFacades.bundle.zip"),
    P("p", "Запасные — те же архивы этой сборки:"),
    U(DLF + "AClad.bundle.zip"), U(DLF + "AFrame.bundle.zip"), U(DLF + "AFacades.bundle.zip"),
    P("p", "После установки пришли, пожалуйста, строку версии: «сборка №%s, коммит %s»." % (BUILD, SHA)),
    P("h2", "2. Твои ответы — что сделано"),
    table([
        ["Ты написал", "Что сделано", "Статус"],
        ["а–д: понял, сделано правильно", "Колонка D, площадь без проёмов, низ витража, двери в строке 4.3, углы и цоколь вручную — без изменений", "подтверждено"],
        ["е: вставить дополнительную направляющую в 100 мм, как у окна", "У каждой вертикальной кромки зоны под плитку (края вырезов, уступы) — направляющая в 100 мм внутрь стены, по высоте кромки + 50 мм сверху и снизу, с кронштейнами; если своя уже стоит ближе 50 мм — не дублируется", "сделано"],
        ["ж: верх и низ проёма на краю зоны — откос и отлив", "Считаются и рисуются как обычные кромки; у двери низ — порог, как было", "сделано"],
        ["з: на фронтоне раскладка нужна", "ATTILE раскладывает фронтон — раздел 3", "сделано"],
    ], [52, 104, 24]),
    P("h2", "3. ATTILE: раскладка фронтона"),
    P("p", "Фронтон раскладывается той же сеткой, что и стена: тот же образец цветов, та же перевязка, тот же "
           "датум. Плитки, которые пересекает скат, <b>режутся по наклону</b>; у конька — по обоим скатам. "
           "Плитки целиком за скатом убираются, полоски тоньше минимального куска уходят в руст — как у прямых "
           "кромок. Обрезанные по скату куски в раскрое считаются «плитка на кусок», в чертеже это полилиния со "
           "штриховкой цвета. Ряды доходят до конька — по ним ATFRAME ставит шины."),
] + img("gable_attile.png", 172, "Настоящий прогон движка: фронтон 6,0 × 3,4 м с окном, плитка 290×82, руст 7. "
                               "Светлые — подрезка, красные — плитки по скату.") + [
    P("p", "Скатом считается ребро круче 2°. Ребро, перекошенное на миллиметры (ошибка чертежа), как и раньше "
           "отклоняется с сообщением — поправь контур."),
    P("h2", "4. Что проверить"),
    P("li", "• ATTILE на фронтоне (и на стене с фронтоном рядом): плитки у ската обрезаны по наклону, за скатом "
            "ничего нет, цвета по образцу продолжаются; повторный ATTILE заменяет раскладку."),
    P("li", "• ATFRAME под плитку по такому фронтону: шины по рядам до конька, направляющие по скату."),
    P("li", "• ATFRAME на зоне с вырезами у входов: в простенках — направляющие в 100 мм от краёв выреза."),
    P("li", "• ATFZONE: окно, верх которого на краю зоны, — откос по верху есть."),
    P("h2", "5. Вопросы"),
    P("li", "а) <b>Межэтажная подсистема</b> (ты уже думаешь над этим вопросом): в простенках между окнами уже шага "
            "направляющих шина не попадает ни на одну направляющую, и удлинить её некуда — у межэтажной по ТЗ "
            "нет направляющих у граней окна. На эталоне таких кусков 277. Ставить в межэтажной направляющие у "
            "граней окна (в 100 мм, как в вертикальной)?"),
    P("li", "б) Раскладка фронтона нужна и для <b>керамогранита (ATCLAD)</b>? Сейчас она сделана для плитки (ATTILE)."),
]


def _frame(canvas, doc):
    canvas.saveState()
    canvas.setFont("DV", 8)
    canvas.drawString(15 * mm, 10 * mm, "Сборка №29 (build-bundle #%s, коммит %s) · 29.09.2026 · стр. %d"
                      % (BUILD, SHA, doc.page))
    canvas.restoreState()


out = os.path.join(HERE, "FACADES_build29_2909.pdf")
doc = BaseDocTemplate(out, pagesize=A4, leftMargin=15 * mm, rightMargin=15 * mm,
                      topMargin=14 * mm, bottomMargin=16 * mm)
doc.addPageTemplates([PageTemplate(id="p", frames=[Frame(doc.leftMargin, doc.bottomMargin,
                                                          doc.width, doc.height, id="f")],
                                   onPage=_frame)])
doc.build(story)
print(out)
