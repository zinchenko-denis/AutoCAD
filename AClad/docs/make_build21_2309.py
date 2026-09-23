# -*- coding: utf-8 -*-
"""PDF Герману: сборка №21 (23.09) — ответ на видео-фидбэк 23.09:
ATTILE — одна команда раскладки (принудительные русты как в ATCLAD, ATCLAD
снят с ленты), ATTILE по штриховке зоны, ATFRAME — одно окно параметров и
режим «только кляммеры». Картинки — build21/*.png (раскладка — настоящий
прогон движка, make_rusty.py; окна — снимки со стенда mono).
Запуск: python3 make_build21_2309.py [номер_прогона_сборки]"""
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
IMG = os.path.join(HERE, "build21")
BUILD = sys.argv[1] if len(sys.argv) > 1 else "94"
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


story = [
    P("h1", "Фасады НВФ — сборка №21 (23.09): одна команда раскладки ATTILE с рустами, "
            "окно ATFRAME и «только кляммеры»"),
    P("p", "Герман, по твоему видео от 23.09 сделано три вещи. <b>ATTILE</b> — теперь "
           "единственная команда раскладки: в её окно перенесены принудительные "
           "вертикальные и горизонтальные русты из ATCLAD. <b>ATCLAD</b> снята с ленты и "
           "меню, но команда осталась — набирается с клавиатуры для старых чертежей; если "
           "что-то в ATTILE не устроит — вернём. <b>ATFRAME</b> — одно окно параметров, как "
           "у ATTILE, с выбором: подсистема и кляммеры, только подсистема или только "
           "кляммеры по существующей облицовке."),

    P("h2", "1. Установка"),
    P("li", "<b>Закрой AutoCAD.</b> Обнови <b>AClad.bundle</b> и <b>AFrame.bundle</b> (лучше все "
            "три фасадных архива — меню «Фасады» общее):"),
    P("url", DL + "AClad.bundle.zip"),
    P("url", DL + "AFrame.bundle.zip"),
    P("url", DL + "AFacades.bundle.zip"),
    P("li", "В <b>%APPDATA%\\Autodesk\\ApplicationPlugins\\</b> удали старые папки и распакуй "
            "новые на их место. Контроль при запуске: «AClad загружен (сборка DLL от "
            "23.09.2026 …)». Запасные ссылки ровно на эту сборку: " + DLF + "AClad.bundle.zip, " +
            DLF + "AFrame.bundle.zip"),
    P("li", "На ленте «Фасады» кнопка <b>«Раскладка»</b> теперь запускает ATTILE; в меню — "
            "«Раскладка облицовки (ATTILE)». ATCLAD — только набором."),

    P("h2", "2. ATTILE: принудительные русты (как в ATCLAD)"),
    P("p", "В окне новый раздел <b>«6. Принудительные русты»</b>: две галки — вертикальные и "
           "горизонтальные. После «Разложить» (и общей точки, если она отмечена) программа "
           "спрашивает точки, как ATCLAD: <b>вертикальный руст</b> — точка на оси шва; "
           "<b>горизонтальный</b> — точка = низ руста; клик по верху окна даёт руст над "
           "окном. Enter — дальше. Если у зоны уже есть точки от прошлой раскладки (ATTILE "
           "или ATCLAD), спросит «Прежние / Новые / Нет»."),
    P("p", "<b>Как раскладывается.</b> Руст — граница участка, облицовки на нём нет. За рустом "
           "раскладка начинается заново <b>от руста</b>: при отсчёте снизу-слева справа от "
           "вертикального руста — целая плитка, слева подрезка приходит к русту; над "
           "горизонтальным — ряды стандартной высоты, под ним — подрезка (как Г3 в ATCLAD). "
           "Разбежка за рустом сохраняется. Клик у грани окна сливается со швом окна — "
           "двойного руста не будет. Руст у самого края зоны не ставится (сообщение). Руст "
           "действует на все зоны, разложенные одним запуском."),
]
story += img("rusty.png", 175, "Настоящая раскладка программы: слева без рустов, справа — "
                               "вертикальный руст x = 2500 и горизонтальный по верху окна. "
                               "Светлые — целые, тёмные — подрезка.")
story += [
    P("p", "<b>Выбор зон.</b> Теперь можно выбирать <b>штриховку или марку зоны ATFZONE</b> — "
           "раньше по ним раскладка отказывала («нет пригодных зон»), работало только по "
           "полилиниям. Если выбрать и штриховку, и полилинии той же зоны, зона не "
           "задвоится. ЗАХВАТКА у плиток — имя зоны («Ф-1»)."),
]
story += img("attile_form.png", 150, "Окно ATTILE с новым разделом 6 (снимок со стенда; в "
                                     "AutoCAD — стандартные шрифты Windows).")
story += [
    P("h2", "3. ATFRAME: одно окно вместо вопросов в командной строке"),
    table([
        ["Раздел", "Что задаёшь"],
        ["1. Что раскладывать", "<b>Подсистему и кляммеры</b> / <b>только подсистему</b> (без "
         "кляммеров) / <b>только кляммеры</b> — на существующие направляющие по текущей облицовке."],
        ["2. Подсистема", "Тип (вертикальная, межэтажная, ортогональная) и профиль (для "
         "вертикальной — Авто/ГП-40-40/ГП-60-40/ШП-60-20, для межэтажной — НСП-1/НСП-2)."],
        ["3. Шаги кронштейнов", "<b>По расчёту</b>: ветровой район, тип местности, высота здания, "
         "вес облицовки, вынос, анкер. <b>Вручную</b>: шаг рядовой и угловой, шаг стоек в угловой "
         "зоне (0 — по рустам), старт кронштейна, зазор стыка, угловая зона."],
        ["4. Оси стоек и швы", "Нужны, только если у зон нет раскладки: шагом от первой оси или "
         "точками, шаг горизонтальных швов (0 — без кляммеров)."],
        ["5. Указать после «Разложить»", "Внешние углы здания, отметки перекрытий; высота этажа "
         "для межэтажной без отметок."],
        ["6. Знаки", "Условные или образцы блоков с чертежа."],
    ], [38, 142]),
    Spacer(1, 2 * mm),
    P("p", "Значения по умолчанию — те же, что раньше давал Enter, так что без правок результат "
           "прежний. Окно запоминает параметры зоны и в следующий раз открывается «с прошлой "
           "подсистемы». Точки (углы, перекрытия, оси, образцы) по-прежнему указываются на "
           "чертеже после «Разложить»."),
]
story += img("atframe_form.png", 150, "Окно ATFRAME (снимок со стенда).")
story += [
    P("h2", "4. «Только кляммеры»"),
    P("li", "<b>Направляющие</b> берутся из прошлого запуска ATFRAME на этих зонах; если его не "
            "было (подсистема нарисована руками) — программа попросит выбрать направляющие на "
            "чертеже."),
    P("li", "<b>Заменяются только прежние кляммеры</b> зоны; направляющие и кронштейны остаются "
            "на месте."),
    P("li", "<b>Тип кляммера</b> считается по текущей облицовке: если облицовку переложили, "
            "кляммеры встанут по новым швам (на шве — рядовой, в поле плиты — боковой, над "
            "стыком — комбинированный, у окон — боковые). Правила — те же, что при полной "
            "раскладке: мы сверили кляммеры «только кляммеры» с кляммерами обычной раскладки "
            "на 1000 разных стен — совпали все."),

    P("h2", "5. Что проверить и о чём ответить"),
    P("li", "<b>Русты:</b> правило «за рустом — заново от руста» подходит? При отсчёте «по "
            "центру» участки между рустами сейчас не центрируются каждый — нужно ли?"),
    P("li", "<b>Только кляммеры:</b> подходит ли, откуда берутся направляющие и что заменяется."),
    P("li", "<b>Окна</b> ATTILE и ATFRAME: если на твоём экране что-то обрезано или не влезает — "
            "пришли скрин."),
    P("li", "<b>Известное:</b> если окно ближе 100 мм к боковому краю зоны, стойка у окна "
            "(грань + 100) выходит за край зоны — в очереди на исправление."),
]


def _frame(canvas, doc):
    canvas.saveState()
    canvas.setFont("DV", 8)
    canvas.drawString(15 * mm, 10 * mm, "Сборка №21 (build-bundle #%s) · 23.09.2026 · стр. %d" % (BUILD, doc.page))
    canvas.restoreState()


out = os.path.join(HERE, "FACADES_build21_2309.pdf")
doc = BaseDocTemplate(out, pagesize=A4, leftMargin=15 * mm, rightMargin=15 * mm,
                      topMargin=14 * mm, bottomMargin=16 * mm)
doc.addPageTemplates([PageTemplate(id="p", frames=[Frame(doc.leftMargin, doc.bottomMargin,
                                                          doc.width, doc.height, id="f")],
                                   onPage=_frame)])
doc.build(story)
print(out)
