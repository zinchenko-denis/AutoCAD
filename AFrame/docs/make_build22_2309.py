# -*- coding: utf-8 -*-
"""PDF Герману: сборка №22 (23.09) — что исправлено по разбору фасадных
программ (ATFZONE, ATCLAD/ATTILE, ATFRAME). Картинки — build22/*.png из
настоящих прогонов движков (make_pics22.py; «было/стало» подсистемы по
контуру стены — docs/review_2309).
Запуск: python3 make_build22_2309.py [номер_прогона_сборки]"""
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
IMG = os.path.join(HERE, "build22")
BUILD = sys.argv[1] if len(sys.argv) > 1 else "95"
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
    P("h1", "Фасады НВФ — сборка №22 (23.09): что исправлено"),
    P("p", "Герман, пока ты тестировал №21, мы прогнали фасадные программы на тысячах "
           "сгенерированных фасадов (разные формы стен, окна, двери, смежные зоны) и сверили "
           "каждую раскладку и подсистему с независимым расчётом. Найденные ошибки исправлены — "
           "ниже по командам, с картинками «было / стало». Всё новое из №21 (окно ATFRAME, "
           "«только кляммеры», русты в ATTILE, выбор зон штриховкой в ATTILE) остаётся."),
    P("p", "<b>На прямоугольных стенах результат не изменился</b> — сверка с твоим полигоном "
           "совпадает один в один. Изменения касаются стен сложной формы, выбора зон и "
           "нескольких зон одним запуском."),

    P("h2", "1. Установка — в этот раз ВСЕ ТРИ архива"),
    P("li", "<b>Закрой AutoCAD.</b> Правки есть во всех трёх фасадных программах, включая "
            "AFacades (ATFZONE), поэтому обнови все три:"),
    P("url", DL + "AFacades.bundle.zip"),
    P("url", DL + "AClad.bundle.zip"),
    P("url", DL + "AFrame.bundle.zip"),
    P("li", "В <b>%APPDATA%\\Autodesk\\ApplicationPlugins\\</b> удали старые папки и распакуй новые "
            "на их место. Контроль при запуске: «… загружен (сборка DLL от 23.09.2026 …)» — дата должна быть сегодняшняя. "
            "Запасные ссылки ровно на эту сборку: " + DLF + "AFacades.bundle.zip, " + DLF +
            "AClad.bundle.zip, " + DLF + "AFrame.bundle.zip"),

    P("h2", "2. ATFRAME — подсистема"),
    P("p", "<b>Стена не прямоугольник</b> (Г-образная, ступенчатая, П-образная, фронтон). Раньше "
           "подсистема ставилась по габаритному прямоугольнику стены — примерно треть стоек, "
           "кронштейнов и кляммеров оказывалась там, где стены нет. Теперь всё идёт по контуру: "
           "стойка доходит до ската фронтона или уступа, кронштейн ставится только на свой "
           "профиль, кляммер «последним рядом» — у верха стены над этой стойкой."),
]
story += img("contour_fix.png", 150, "Настоящая расстановка программы. Красное — за контуром стены "
                                     "(было); справа — после исправления.")
story += [
    P("p", "<b>Зоны ATFZONE.</b> ATFRAME раньше не понимал зону, выбранную штриховкой или маркой, и "
           "строил подсистему только по полилиниям. Теперь можно выбирать штриховку/марку зоны; "
           "ЗАХВАТКА в знаках — имя зоны («Ф-1»), а не служебный номер полилинии."),
    P("p", "<b>Несколько зон одним запуском.</b> Если выбрать сразу, например, цоколь и этаж с разной "
           "раскладкой, в каждую зону попадали стойки по швам соседней. Теперь у каждой зоны свои "
           "оси — те, что записала её раскладка (для этого ATCLAD и ATTILE пишут в метку оси "
           "именно этой зоны)."),
]
story += img("joints_fix.png", 150, "Цоколь 9000×1200 под этажом с двумя окнами: было 76 стоек, "
                                    "стало 42. Красное — стойки по швам чужой зоны.")
story += [
    table([
        ["Что ещё исправлено в ATFRAME", "Как теперь"],
        ["Выбор рамкой: и штриховка зоны, и её полилинии", "Зона строится один раз (раньше грозило "
         "задвоение)."],
        ["Отдельная зона в «кармане» Г-образной стены", "Своя подсистема (раньше считалась проёмом "
         "большой стены и оставалась пустой)."],
        ["Окно ближе 100 мм к боковому краю зоны", "Стойка у окна (грань + 100) больше не ставится "
         "за краем зоны — в командной строке сообщение."],
        ["Межэтажная: подоконный СП-60-40", "Не проходит сквозь соседнее окно и вдоль окна прямо "
         "под ним; если укорочен — сообщение."],
    ], [66, 114]),

    P("h2", "3. ATFZONE — зоны"),
    P("p", "<b>Окно, нарисованное с нахлёстом за край стены.</b> Раньше оно молча становилось "
           "отдельной «зоной» с площадью окна, а из стены не вычиталось. Теперь в командной строке "
           "ошибка с именами обоих контуров, окно не учитывается — поправь обводку (окно целиком "
           "внутри стены или ровно по её краю) и запусти ATFZONE ещё раз."),
]
story += img("phantom_fix.png", 140, "Стена 8×5 м, окно залезает за край на 50 мм.")
story += [
    P("p", "<b>Круглые и арочные окна.</b> Длина отлива и откоса у круглых окон «гуляла» до 17 см в "
           "зависимости от того, где окно стоит на чертеже. Теперь не зависит от положения; "
           "расхождение с точным расчётом — меньше миллиметра."),

    P("h2", "4. Все фасадные команды"),
    P("p", "Если расчёт почему-то завис (антивирус, повреждённая установка), AutoCAD раньше "
           "зависал навсегда. Теперь через 2 минуты (ATFZONE), 10 минут (ATTILE/ATCLAD) или "
           "5 минут (ATFRAME) процесс останавливается, в командной строке — «движок не ответил». "
           "Для обычных фасадов это время с большим запасом."),

    P("h2", "5. Что проверить"),
    P("li", "ATFRAME на Г-образной или ступенчатой стене и на фронтоне — стойки не выходят за "
            "контур, верхние кляммеры у ската/уступа."),
    P("li", "ATFRAME, выбрав зону ATFZONE штриховкой: подсистема строится, в знаках ЗАХВАТКА = имя "
            "зоны."),
    P("li", "ATFRAME на две зоны с разной раскладкой одним запуском — лишних стоек нет."),
    P("li", "ATFZONE с окном у края стены: если окно чуть выходит за край — появится сообщение."),

    P("h2", "6. Вопросы (ответь, когда будет удобно)"),
    P("li", "<b>Ортогональная:</b> кусок вертикального профиля выше последнего горизонтального "
            "профиля (или между стыком профиля и окном) ни на что не опирается. Как правильно: "
            "добавить горизонтальный профиль у верха или дотянуть кусок до профиля ниже?"),
    P("li", "<b>Откосы:</b> если бок окна лишь частично лежит на краю зоны, эту часть считать "
            "откосом или нет?"),
    P("li", "Из №21 — остаются: правило рустов в ATTILE («за рустом — заново от руста») и "
            "«только кляммеры» (откуда брать направляющие) — подходит ли?"),
]


def _frame(canvas, doc):
    canvas.saveState()
    canvas.setFont("DV", 8)
    canvas.drawString(15 * mm, 10 * mm, "Сборка №22 (build-bundle #%s) · 23.09.2026 · стр. %d" % (BUILD, doc.page))
    canvas.restoreState()


out = os.path.join(HERE, "FACADES_build22_2309.pdf")
doc = BaseDocTemplate(out, pagesize=A4, leftMargin=15 * mm, rightMargin=15 * mm,
                      topMargin=14 * mm, bottomMargin=16 * mm)
doc.addPageTemplates([PageTemplate(id="p", frames=[Frame(doc.leftMargin, doc.bottomMargin,
                                                          doc.width, doc.height, id="f")],
                                   onPage=_frame)])
doc.build(story)
print(out)
