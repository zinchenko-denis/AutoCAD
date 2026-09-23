# -*- coding: utf-8 -*-
"""PDF Герману: сборка №23 (23.09) — исправления по независимой рецензии
фасадных программ (ATFZONE/ATFTABLE, ATTILE/ATCLAD, ATFRAME/ATDEDUP).
Картинки — build23/*.png из настоящих прогонов движков (make_pics23.py:
«было» — код aa47a78, «стало» — текущий).
Запуск: python3 make_build23_2309.py [номер_прогона_сборки] [коммит]"""
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
IMG = os.path.join(HERE, "build23")
BUILD = sys.argv[1] if len(sys.argv) > 1 else "96"
SHA = sys.argv[2] if len(sys.argv) > 2 else "4a06f23"
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
    P("h1", "Фасады НВФ — сборка №23 (23.09): исправления по независимой проверке"),
    P("p", "Герман, наши фасадные программы проверил независимый рецензент. Мы повторили каждый "
           "его пример на своём коде, подтвердили находки и исправили их. Почти всё — защита "
           "чертежа и объёмов в нестандартных случаях: двойные обводки, частичный успех, "
           "копирование зон, неравномерная сетка осей. <b>На обычных фасадах результат прежний</b> — "
           "сверка с твоим полигоном и эталон плитки 290×82 (145 333 шт.) не изменились."),

    P("h2", "1. Установка — все три архива"),
    P("li", "<b>Закрой AutoCAD</b> и обнови все три фасадных архива — изменения есть в каждом:"),
    P("url", DL + "AFacades.bundle.zip"),
    P("url", DL + "AClad.bundle.zip"),
    P("url", DL + "AFrame.bundle.zip"),
    P("li", "В <b>%APPDATA%\\Autodesk\\ApplicationPlugins\\</b> удали старые папки и распакуй новые. "
            "Контроль при запуске теперь точнее: «… загружен (сборка DLL от 23.09.2026 …, "
            "<b>сборка №" + BUILD + ", коммит " + SHA + "</b>)». Запасные ссылки: " + DLF + "AFacades.bundle.zip, " +
            DLF + "AClad.bundle.zip, " + DLF + "AFrame.bundle.zip"),

    P("h2", "2. ATFZONE и ATFTABLE — зоны"),
    table([
        ["Случай", "Как теперь"],
        ["Одна и та же обводка дважды (копия поверх)", "Повтор не учитывается, предупреждение с именами "
         "контуров. Раньше окно вычиталось дважды: стена 25 м² с двумя одинаковыми окнами по 9 м² "
         "давала 7 м² вместо 16."],
        ["Две зоны накладываются площадью (общие верх и низ)", "Ошибка с именами контуров, меньшая не "
         "учитывается — площадь не считается дважды."],
        ["Контур в наружной выемке П-стены", "Отдельная зона; из стены не вычитается (раньше стена "
         "28 м² превращалась в 22)."],
        ["Бок окна частично лежит на краю зоны", "Часть на краю — «граница», в откос не идёт (было: "
         "весь бок — откос)."],
        ["Номер новой зоны", "Предлагается следующий свободный по маркам в чертеже; если марка уже "
         "есть — вопрос «создать ещё раз?»."],
        ["Esc у точки таблицы", "Команда отменяется целиком (зоны не создаются)."],
        ["ATFTABLE", "Звёздочка «зона изменена» ставится независимо от того, что выбрано первым — "
         "штриховка или марка; Esc отменяет команду."],
    ], [62, 118]),

    P("h2", "3. ATTILE и ATCLAD — раскладка"),
    table([
        ["Случай", "Как теперь"],
        ["Одна из выбранных зон не разложилась (скос, дуги, нет геометрии)", "Её прежняя раскладка "
         "остаётся — в командной строке сообщение. Раньше старая стиралась, новой не было."],
        ["Раньше несколько контуров раскладывались вместе (одна плоскость), а сейчас выбран один",
         "Перекладываются все вместе — с сообщением. Раньше у соседа терялись плитки."],
        ["Зону скопировали вместе с раскладкой (COPY) и переразложили копию", "Раскладка оригинала не "
         "трогается. Раньше метка копии указывала на плитки оригинала."],
        ["Зону изменили после ATFZONE", "Зона пропускается: «изменена после ATFZONE — повторите "
         "ATFZONE» (как в ATCLAD)."],
        ["U-образная полоса вокруг отдельной зоны", "Раскладываются обе (картинка ниже)."],
        ["Большие фасады", "Расчёт осей швов для ATFRAME быстрее в десятки раз."],
    ], [62, 118]),
]
story += img("u_fix.png", 150, "Настоящая раскладка: слева прежняя версия (полоса пропадала), справа — новая.")
story += [
    P("h2", "4. ATFRAME и ATDEDUP — подсистема"),
    P("p", "<b>Расчёт.</b> Шаг кронштейнов теперь подбирается по ширине, которую реально несёт "
           "стойка, — с учётом достроенных серединных стоек. На неравномерной сетке осей шаг может "
           "стать меньше; в отчёте появится строка «грузовая ширина для расчёта … — по итоговым "
           "осям». На равномерной сетке всё как было."),
]
story += img("calc_fix.png", 95, "Пример проверяющего: оси 100/200/300/400/1600/2800 мм — угловой шаг 500 "
                                 "вместо 800.")
story += [
    table([
        ["Случай", "Как теперь"],
        ["Треугольный фасад", "Подсистема строится (раньше — пустой ответ)."],
        ["Контур с дугами (выбран полилинией)", "Сообщение «дуги не поддерживаются»; раньше дуга "
         "молча заменялась хордой."],
        ["«Только кляммеры»", "Существующие направляющие — по контуру стены и мимо окон."],
        ["Несколько зон одним запуском с расчётом", "Расчётный отчёт — по каждой зоне."],
        ["Одна из зон не построилась", "Её прежняя подсистема остаётся (сообщение)."],
        ["ATDEDUP", "Esc — отмена (раньше Esc чистил весь чертёж); весь чертёж — только по Enter и «Да»; "
         "сначала показывает, сколько дублей и каких, и спрашивает «Удалить?»; дубль — только полное "
         "совпадение, включая длину профиля, видимость и марку."],
    ], [62, 118]),

    P("h2", "5. Что проверить (на копии рабочего чертежа)"),
    P("li", "COPY зоны вместе с раскладкой → ATTILE на копии: раскладка оригинала цела."),
    P("li", "Выбрать две зоны, одна со скосом → у неё осталась прежняя раскладка, вторая переложена."),
    P("li", "ATFZONE повторно на тех же контурах → вопрос о совпадающих марках."),
    P("li", "ATDEDUP: Esc; Enter → «Нет»; два профиля разной длины в одной точке не удаляются."),
    P("li", "ATFRAME с расчётом на неравномерной сетке осей → шаги и строка про грузовую ширину."),
    P("li", "После каждой команды — ОТМЕНИТЬ (U): чертёж возвращается целиком."),
    P("li", "Меню «Фасады»: выйти из AutoCAD, в диалоге сохранения нажать «Отмена» — меню на месте."),

    P("h2", "6. Вопросы (когда будет удобно)"),
    P("li", "Бок окна частично на краю зоны: часть на краю не считать откосом — так правильно?"),
    P("li", "ATDEDUP: искать дубли только среди объектов подсистемы или среди любых блоков?"),
    P("li", "Ортогональная: кусок вертикали выше последнего горизонтального профиля — добавить "
            "профиль у верха или дотянуть кусок?"),
    P("li", "Из прежних: правило рустов ATTILE и «только кляммеры» — подходят?"),
]


def _frame(canvas, doc):
    canvas.saveState()
    canvas.setFont("DV", 8)
    canvas.drawString(15 * mm, 10 * mm, "Сборка №23 (build-bundle #%s, коммит %s) · 23.09.2026 · стр. %d" % (BUILD, SHA, doc.page))
    canvas.restoreState()


out = os.path.join(HERE, "FACADES_build23_2309.pdf")
doc = BaseDocTemplate(out, pagesize=A4, leftMargin=15 * mm, rightMargin=15 * mm,
                      topMargin=14 * mm, bottomMargin=16 * mm)
doc.addPageTemplates([PageTemplate(id="p", frames=[Frame(doc.leftMargin, doc.bottomMargin,
                                                          doc.width, doc.height, id="f")],
                                   onPage=_frame)])
doc.build(story)
print(out)
