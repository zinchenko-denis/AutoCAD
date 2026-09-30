# -*- coding: utf-8 -*-
"""PDF Герману: сборка №30 (30.09) — ответы на вопросы PDF №29: межэтажная — направляющие вдоль боковых
откосов (а), раскладка фронтона под керамогранит в ATCLAD (б). Картинки — build30/*.png (make_pics30.py,
настоящие прогоны движков). Запуск: python3 make_build30_3009.py [номер_прогона_сборки] [коммит]"""
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
IMG = os.path.join(HERE, "build30")
BUILD = sys.argv[1] if len(sys.argv) > 1 else "103"
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
    P("h1", "Фасады НВФ — сборка №30 (30.09): межэтажная — направляющие вдоль откосов; "
            "фронтон под керамогранит"),
    P("p", "Герман, в этой сборке — твои ответы на вопросы №29. В <b>межэтажной</b> у каждого окна теперь "
           "идут направляющие вдоль боковых откосов, от перекрытия до перекрытия. <b>ATCLAD</b> раскладывает "
           "фронтон, как ATTILE: плиты у ската режутся по наклону."),
    P("h2", "1. Что установить"),
    P("p", "Обновить <b>все три</b>: AClad, AFrame, AFacades (AFacades не менялся, но меню у трёх общее). "
           "Постоянные ссылки:"),
    U(DL + "AClad.bundle.zip"), U(DL + "AFrame.bundle.zip"), U(DL + "AFacades.bundle.zip"),
    P("p", "Запасные — те же архивы этой сборки:"),
    U(DLF + "AClad.bundle.zip"), U(DLF + "AFrame.bundle.zip"), U(DLF + "AFacades.bundle.zip"),
    P("p", "После установки пришли, пожалуйста, строку версии: «сборка №30, коммит %s»." % SHA),
    P("h2", "2. Твои ответы — что сделано"),
    table([
        ["Ты написал", "Что сделано", "Статус"],
        ["а) «В простенках между окон не может не быть вертикальных направляющих — они там обязательно "
         "идут. Идут вдоль боковых откосов»",
         "У каждой боковой грани окна — межэтажная направляющая вдоль откоса: в 100 мм от грани, в простенке "
         "уже 200 мм — одна посередине. По высоте — от перекрытия под окном до перекрытия над ним, стыки и "
         "вставки на перекрытиях. СП-60-40 под окном крепится к ним. Так же нарисовано в альбоме "
         "«Вектор-1», тип 4 (лист 7, узел 9.19) — извини, вопрос был лишним.", "сделано"],
        ["б) «Раскладка нужна не только на плитку, но и на керамогранит — он также может идти вдоль "
         "фронтонов»", "ATCLAD раскладывает фронтон — раздел 4", "сделано"],
    ], [58, 98, 24]),
    P("h2", "3. Межэтажная: направляющие вдоль откосов"),
    P("p", "Раньше у межэтажной направляющие шли только по рустам (у плитки — по сетке шага), и в простенке "
           "уже шага не было ни одной: шина висела. Теперь у каждой грани окна своя НСП. Если ось руста уже "
           "стоит у самой грани (ближе 150 мм), она и считается направляющей у откоса — лишней не ставим. "
           "Стартового кляммера внизу такой НСП нет: там перекрытие, облицовка идёт и ниже. У краёв вырезов "
           "зоны под плитку направляющие тоже доведены до перекрытий. На нашем эталоне 290×82 кусков шины без "
           "опоры было 277, стало 0; вертикальная и ортогональная не изменились."),
] + img("interfloor_reveals.png", 128, "Настоящий прогон движка: межэтажная под клинкер, шаг 600, перекрытия "
                                       "3000/6000. Оранжевые — НСП вдоль откосов, зелёные — СП-60-40, синие — НГП.") + [
    P("p", "Простенок уже 60 мм (самый узкий С-профиль) направляющую не вмещает — программа пишет об этом "
           "замечание «проверьте»."),
    P("h2", "4. ATCLAD: фронтон под керамогранит"),
    P("p", "Фронтон раскладывается теми же правилами, что стена (центрирование в простенках, русты у окон, "
           "отметка старта), по прямоугольной обёртке; плиты, которые пересекает скат, <b>режутся по "
           "наклону</b>, у конька — по обоим скатам, плиты целиком за скатом убираются, остатки тоньше 10 мм "
           "уходят в шов. Скат — ребро круче 2°; ребро, перекошенное на миллиметры, как и раньше отклоняется. "
           "Оси швов фронтона теперь идут в ATFRAME."),
] + img("atclad_gable.png", 172, "Настоящий прогон движка: фронтон 12 м, конёк 6,5 м, плита 1200×600, окно в "
                                 "треугольнике. Оранжевые — фигурные куски у ската.") + [
    P("p", "Фигурные куски у ската в чертеже — <b>замкнутые полилинии</b> в слое облицовки (как у ATTILE), не "
           "блоки. Повторный ATCLAD их заменяет вместе с камнями. Их число пишется в командной строке."),
    P("h2", "5. Что проверить"),
    P("li", "• ATFRAME, межэтажная, на фасаде с узкими простенками (керамогранит и плитка): у каждой грани окна "
            "направляющая вдоль откоса, от перекрытия до перекрытия; СП-60-40 под окном — к ним."),
    P("li", "• То же с окнами друг над другом: одна направляющая со стыком на перекрытии."),
    P("li", "• ATCLAD на фронтоне: плиты у ската обрезаны по наклону, за скатом ничего, окно в треугольнике "
            "обойдено; повторный ATCLAD заменяет раскладку; ATFRAME по такой раскладке ставит стойки по швам."),
    P("h2", "6. Вопросы"),
    P("li", "а) Фигурные куски у ската фронтона (ATCLAD) — полилинии, в спецификацию блоков они не попадают. "
            "Оставить так (число — в командной строке) или считать их в спецификации целой плитой по габариту "
            "куска?"),
    P("li", "б) Если есть актуальный альбом технических решений <b>«Вектор-4»</b> (клинкер, камень) и свежий "
            "<b>«Вектор-1»</b> — пришли, пожалуйста: у нас «Вектор-1» 2015 года, а «Вектор-4» нет. Дальше будем "
            "сверять по ним, прежде чем спрашивать."),
]


def _frame(canvas, doc):
    canvas.saveState()
    canvas.setFont("DV", 8)
    canvas.drawString(15 * mm, 10 * mm, "Сборка №30 (build-bundle #%s, коммит %s) · 30.09.2026 · стр. %d"
                      % (BUILD, SHA, doc.page))
    canvas.restoreState()


out = os.path.join(HERE, "FACADES_build30_3009.pdf")
doc = BaseDocTemplate(out, pagesize=A4, leftMargin=15 * mm, rightMargin=15 * mm,
                      topMargin=14 * mm, bottomMargin=16 * mm)
doc.addPageTemplates([PageTemplate(id="p", frames=[Frame(doc.leftMargin, doc.bottomMargin,
                                                          doc.width, doc.height, id="f")],
                                   onPage=_frame)])
doc.build(story)
print(out)
