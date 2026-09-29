# -*- coding: utf-8 -*-
"""PDF Герману: сборка №27 (29.09) — ATFRAME под бетонную и клинкерную плитку
(постановка Дениса 29.09: первым пунктом — облицовка; у плитки стойки стандартным
шагом не по швам, кронштейны шагом по вертикали, шины по рядам). Картинки —
build27/*.png: окно (mono + xvfb, AFrame/tools/frame_ui), настоящий прогон движка на
эталоне 290×82 с рядами из раскладки ATTILE.
Запуск: python3 make_build27_2909.py [номер_прогона_сборки] [коммит]"""
import os
import sys

from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, Frame, Image, PageTemplate,
                                Paragraph, Table, TableStyle)

FDIR = "/usr/share/fonts/truetype/dejavu/"
pdfmetrics.registerFont(TTFont("DV", FDIR + "DejaVuSans.ttf"))
pdfmetrics.registerFont(TTFont("DVB", FDIR + "DejaVuSans-Bold.ttf"))
S = dict(
    h1=ParagraphStyle("h1", fontName="DVB", fontSize=15, leading=19, spaceAfter=4 * mm),
    h2=ParagraphStyle("h2", fontName="DVB", fontSize=12.5, leading=16, spaceBefore=5 * mm, spaceAfter=2.5 * mm),
    p=ParagraphStyle("p", fontName="DV", fontSize=10, leading=13.5, spaceAfter=2 * mm),
    li=ParagraphStyle("li", fontName="DV", fontSize=10, leading=13.5, leftIndent=6 * mm, spaceAfter=1.5 * mm),
    url=ParagraphStyle("url", fontName="DV", fontSize=9.5, leading=13, leftIndent=6 * mm, spaceAfter=1.5 * mm),
    cap=ParagraphStyle("cap", fontName="DV", fontSize=8.5, leading=11, alignment=1, spaceAfter=2 * mm),
    td=ParagraphStyle("td", fontName="DV", fontSize=9, leading=11.5),
)
HERE = os.path.dirname(os.path.abspath(__file__))
IMG = os.path.join(HERE, "build27")
BUILD = sys.argv[1] if len(sys.argv) > 1 else "100"
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
    P("h1", "Фасады НВФ — сборка №27 (29.09): ATFRAME под бетонную и клинкерную плитку"),
    P("p", "Герман, по постановке через Дениса ATFRAME теперь различает облицовку. Керамогранит и "
           "композит — как раньше. Для бетонной и клинкерной плитки — подсистема <b>без привязки к "
           "вертикальным швам</b>: стойки стандартным шагом, кронштейны на них шагом по вертикали, а "
           "по рядам плитки — горизонтальные шины, на которые вешается облицовка. <b>В живом AutoCAD "
           "это ещё не проверялось</b> — проверка за тобой (раздел 5), вопросы — раздел 6."),
    P("p", "И спасибо за прогон №26: ATTILE на 11 610 плитках — 45 секунд вместо часов, сообщений "
           "про штриховку в прогоне нет."),
    P("h2", "1. Установка — только AFrame"),
    P("li", "Изменился только AFrame; AClad и AFacades — прежние. <b>Закрой AutoCAD</b>, в "
            "<b>%APPDATA%\\Autodesk\\ApplicationPlugins\\</b> удали старую папку AFrame.bundle и "
            "распакуй новую из архива:"),
    U(DL + "AFrame.bundle.zip"),
    P("li", "Контроль при запуске AutoCAD: «AFrame загружен (… <b>сборка №" + BUILD + ", коммит " +
            SHA + "</b>)». Запасная ссылка — тот же архив этой сборки:"),
    U(DLF + "AFrame.bundle.zip"),
    P("h2", "2. Окно: первым пунктом — облицовка"),
    P("p", "Керамогранит и композит — алгоритм прежний (в окне только добавился первый пункт). "
           "Бетонная или клинкерная плитка — "
           "«что раскладывать» и тип подсистемы недоступны (кляммеров у плитки нет, тип — "
           "вертикальная), вместо расчёта шагов два поля: шаг кронштейнов <b>по вертикали</b> и <b>по "
           "горизонтали</b> (по умолчанию 600). Углы здания и образцы кляммеров не спрашиваются. "
           "Выбор запоминается в метке подсистемы — повторный ATFRAME на зоне откроется с ним."),
]
story += img("frame_tile_window.png", 150, "Окно ATFRAME, выбрана клинкерная плитка (снимок окна вне AutoCAD).")
story += [
    P("h2", "3. Как раскладывается подсистема под плитку"),
    P("li", "• <b>Вертикальные профили — не по швам.</b> Первый — в 100 мм от левого края зоны, дальше "
            "<b>ровно</b> шагом по горизонтали, последний — в 100 мм от правого края (короче шага выходит "
            "только последний пролёт)."),
    P("li", "• <b>У окон</b> — профили у каждой грани в 100 мм, на высоту окна плюс 50/50 (как в "
            "вертикальной системе); профиль, попавший в проём, режется."),
    P("li", "• <b>Кронштейны</b> на каждом профиле: первый в 300 мм от низа, дальше ровно шагом по "
            "вертикали, последний в 300 мм от верха."),
    P("li", "• <b>Шины</b> — на каждый ряд плитки, по центру горизонтального шва, по всей ширине зоны; "
            "окна их разрезают. Ряды — из раскладки ATTILE; если раскладки нет — шагом швов из окна, "
            "от низа зоны."),
    P("li", "• Шины рисуются прямоугольниками на <b>отдельном слое _01_ПС_ШИНЫ</b> — в спецификации "
            "отдельно от вертикальных профилей. Кляммеров нет."),
    P("h2", "4. Настоящий прогон на эталоне 290×82"),
    P("p", "Эталонный фасад 290×82 (3 717 м²), ряды шин — из раскладки ATTILE этого фасада (модуль "
           "ряда 89 мм), шаги 600 × 600. Это ответ движка ATFRAME; отрисовку в AutoCAD не мерил."),
]
story += img("tile_frame_290.png", 180, "Слева — вся зона с 66 окнами, справа — крупно у окна (оранжевая рамка).")
story += [
    table([
        ["По всему фасаду (17 зон раскладки)", "Сколько"],
        ["Вертикальных профилей", "4 922 куска, 8 213 м.п."],
        ["Шин по рядам плитки", "20 946 кусков, 41 600 м.п. (≈ 11,2 м.п. на м²)"],
        ["Кронштейнов", "15 671"],
        ["Кляммеров", "0"],
        ["Расчёт движка", "0,9 с"],
    ], [80, 100]),
    P("h2", "5. Что проверить (на копии чертежа с раскладкой ATTILE)"),
    P("li", "• ATFRAME → «клинкерная плитка» → шаги → «Разложить»: профили идут шагом от края зоны, а "
            "не по швам; у окон — профили у граней; кронштейны — заданным шагом."),
    P("li", "• Шины — на каждом ряду, окна их режут; слой _01_ПС_ШИНЫ; кляммеров нет; в итоге "
            "команды — «шин N (M м.п.)»."),
    P("li", "• Повторный ATFRAME на той же зоне — окно открывается с плиткой, прежняя подсистема "
            "заменяется новой; ОТМЕНИТЬ (U) — уходит одним шагом."),
    P("li", "• Контроль: керамогранит на другой зоне — всё как раньше."),
    P("li", "• Время ATFRAME на большом фасаде — пришли последние строки команды."),
    P("h2", "6. Вопросы — что я решил сам, подтверди или поправь"),
    P("li", "1) Шина стоит на каждый ряд, по центру горизонтального шва. Нужна ли ещё стартовая шина "
            "по низу зоны и верхняя по верху?"),
    P("li", "2) Шина идёт одним куском на ряд. Резать ли её на хлысты со стыком на стойке — и какой "
            "длины хлыст?"),
    P("li", "3) Марка профиля шины для спецификации и есть ли у тебя блок шины? Пока — прямоугольник."),
    P("li", "4) Профили ровно шагом от левого края, последний пролёт короче. Или раскладывать "
            "равномерно, не больше шага?"),
    P("li", "5) Нужен ли у плитки меньший шаг у углов здания (угловые зоны)?"),
]


def _frame(canvas, doc):
    canvas.saveState()
    canvas.setFont("DV", 8)
    canvas.drawString(15 * mm, 10 * mm, "Сборка №27 (build-bundle #%s, коммит %s) · 29.09.2026 · стр. %d"
                      % (BUILD, SHA, doc.page))
    canvas.restoreState()


out = os.path.join(HERE, "FACADES_build27_2909.pdf")
doc = BaseDocTemplate(out, pagesize=A4, leftMargin=15 * mm, rightMargin=15 * mm,
                      topMargin=14 * mm, bottomMargin=16 * mm)
doc.addPageTemplates([PageTemplate(id="p", frames=[Frame(doc.leftMargin, doc.bottomMargin,
                                                          doc.width, doc.height, id="f")],
                                   onPage=_frame)])
doc.build(story)
print(out)
