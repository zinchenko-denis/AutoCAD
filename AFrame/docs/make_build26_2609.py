# -*- coding: utf-8 -*-
"""PDF Герману: сборка №26 (26.09) — скорость ATTILE на больших фасадах (его фидбэк
по №25: «больше 15 минут», два видео: 25 зон за ночь не разложились, командная строка
залита «Ассоциативная штриховка … не обновлена»). Картинки — build26/*.png (окно хода,
снимок mono + xvfb: AClad/tools/attile_ui/ProgressCheck.cs).
Запуск: python3 make_build26_2609.py [номер_прогона_сборки] [коммит]"""
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
IMG = os.path.join(HERE, "build26")
BUILD = sys.argv[1] if len(sys.argv) > 1 else "99"
SHA = sys.argv[2] if len(sys.argv) > 2 else "5a4622b"
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
    P("h1", "Фасады НВФ — сборка №26 (26.09): скорость ATTILE на больших фасадах"),
    P("p", "Герман, ты прав: раскладка тормозила из-за того, как программа создавала плитки, а не "
           "из-за размера фасада. Твои два видео показали причину — поток сообщений «Ассоциативная "
           "штриховка на заблокированном или замороженном слое не обновлена» на каждую вставку. "
           "В этой сборке отрисовка ATTILE переделана. <b>В живом AutoCAD правки ещё не "
           "проверялись</b> — проверка за тобой (раздел 5)."),
    P("h2", "1. Установка — только AClad"),
    P("li", "В этой сборке изменился только AClad; AFacades и AFrame — прежние. <b>Закрой AutoCAD</b>, "
            "в <b>%APPDATA%\\Autodesk\\ApplicationPlugins\\</b> удали старую папку AClad.bundle и "
            "распакуй новую из архива:"),
    U(DL + "AClad.bundle.zip"),
    P("li", "Контроль при запуске AutoCAD: в командной строке «… загружен (… <b>сборка №" + BUILD +
            ", коммит " + SHA + "</b>)».  Запасная ссылка — тот же архив этой сборки:"),
    U(DLF + "AClad.bundle.zip"),
    P("h2", "2. Что было не так"),
    P("p", "Время уходило на работу AutoCAD, которая нам не нужна:"),
    P("li", "• <b>Динблок пересчитывался на каждом кирпиче.</b> Каждый кирпич вставлялся заново, и "
            "AutoCAD трижды пересчитывал блок: видимость, ширина, высота. При каждом пересчёте он "
            "пытался обновить штриховку внутри блока — отсюда сообщение на каждую вставку. Сама "
            "печать сотен тысяч строк в командную строку тормозит тем сильнее, чем их больше, — "
            "поэтому 1 зона — минута, 5 зон — 10 минут, а 25 зон — больше 4,5 часов."),
    P("li", "• <b>Заливки подрезки были ассоциативными</b> — у каждого куска своя связь «контур → "
            "штриховка» и та же попытка обновления. Поэтому и без галочек время почти не менялось."),
    P("li", "• <b>Весь фасад записывался одной огромной операцией.</b>"),
    P("p", "Моя первая догадка («только динблок») была неполной — ассоциативность и поток "
           "сообщений я не учёл."),
    P("h2", "3. Что сделано"),
    table([
        ["Было", "Стало"],
        ["Каждый кирпич — новая вставка и 2–3 пересчёта динблока",
         "Динблок пересчитывается <b>один раз на каждый типоразмер</b> (образец); остальные кирпичи — "
         "его точные копии, как при COPY, без пересчёта. Если копирование вдруг не сработает, "
         "раскладка доделается по-старому (медленно, но не сломается), и сводка скажет об этом."],
        ["Ассоциативные заливки подрезки и в блоке «Плитка_…»",
         "Ассоциативных штриховок больше нет — заливка строится по вершинам куска."],
        ["Одна операция на весь фасад; не видно, сколько ждать; прервать нельзя",
         "Порции по ~1 секунде и <b>окно хода</b>: сколько уложено, сколько прошло и сколько "
         "осталось. <b>Esc или «Прервать»</b> — всё уложенное удаляется, прежняя раскладка остаётся "
         "целой (она стирается только в самом конце)."],
        ["Командная строка заливается сообщениями",
         "Служебные сообщения AutoCAD на время раскладки выключены (потом включаются обратно)."],
        ["—", "В сводке ATTILE новые строки: <b>«время: расчёт …, отрисовка …, запись меток …»</b> и "
              "<b>«динблок пересчитан N раз … остальные M плиток — его копии»</b>."],
    ], [62, 118]),
]
story += img("progress_mid.png", 110, "Окно хода ATTILE (вид окна; числа на снимке условные, "
                                      "не замер).")
story += [
    P("h2", "4. Сколько работы стало меньше — счёт на эталоне 290×82"),
    P("p", "Эталон — фасад 290×82 на 17 зон, 159 695 элементов. Посчитано, сколько операций "
           "AutoCAD нужно по ответу движка. Это <b>счёт операций, а не время</b>: время покажет "
           "только твой прогон."),
    table([
        ["Операция", "Было", "Стало"],
        ["Пересчётов динблока (образец динблоком)", "316–474 тыс. (2–3 на кирпич)",
         "не больше 5 130 (1 710 образцов на типоразмер и зону; без атрибута ЗАХВАТКА — 804)"],
        ["Ассоциативных заливок («Прямоугольник» с раскраской)", "41 328", "0"],
        ["Копий кирпичей", "—", "156 316 за 726 вызовов копирования"],
    ], [66, 44, 70]),
    P("h2", "5. Что проверить (на копии рабочего чертежа)"),
    P("li", "• <b>Тот же тестовый файл, что на видео:</b> 1 зона, 5 зон, все 25 зон. После каждого "
            "запуска пришли последние строки сводки ATTILE — «время: …» и «динблок пересчитан …» — "
            "или их снимок."),
    P("li", "• Остались ли сообщения «Ассоциативная штриховка … не обновлена»? Если да — снимок."),
    P("li", "• <b>Esc посреди раскладки</b> (или кнопка «Прервать»): чертёж таким, каким был до "
            "запуска; прежняя раскладка на месте."),
    P("li", "• <b>ОТМЕНИТЬ (U)</b> после раскладки — вся раскладка уходит одним шагом."),
    P("li", "• <b>Копии выглядят как надо:</b> у подрезанных кирпичей ручки динблока работают, "
            "в свойствах — верные ширина и высота; ATSPEC по слою раскладки даёт то же число и те "
            "же размеры, что раньше."),
    P("li", "• Повторный ATTILE на уже разложенных зонах: прежняя раскладка заменяется новой."),
    P("p", "И пункты из №25, по которым ответа ещё не было:"),
    P("li", "• COPY зоны вместе с раскладкой → ATTILE на копии: на копии одна раскладка, оригинал "
            "цел; то же с подсистемой ATFRAME."),
    P("li", "• COPY штриховки зоны ATFZONE → сразу ATTILE на копии → сообщение «выполните ATFZONE» "
            "→ ATFZONE на копии → ATTILE."),
    P("li", "• Окно, бок которого частично на краю зоны → ATFTABLE: бок в откосах целиком."),
    P("li", "• ATDEDUP в чертеже с посторонними блоками: они не трогаются, в сводке их число."),
    P("li", "• Ортогональная с окном близко к верху зоны: над окном нет висящих кусков длиннее 300 мм."),
    P("h2", "6. Вопросы (когда будет удобно)"),
    P("li", "• Бок окна <b>целиком</b> на краю зоны (окно вплотную к краю) — тоже считать откосом?"),
    P("li", "• Кусок вертикали до 300 мм выше последнего профиля оставляем как есть — верно?"),
    P("li", "• Режим «только кляммеры» в ATFRAME — подходит?"),
]


def _frame(canvas, doc):
    canvas.saveState()
    canvas.setFont("DV", 8)
    canvas.drawString(15 * mm, 10 * mm, "Сборка №26 (build-bundle #%s, коммит %s) · 26.09.2026 · стр. %d"
                      % (BUILD, SHA, doc.page))
    canvas.restoreState()


out = os.path.join(HERE, "FACADES_build26_2609.pdf")
doc = BaseDocTemplate(out, pagesize=A4, leftMargin=15 * mm, rightMargin=15 * mm,
                      topMargin=14 * mm, bottomMargin=16 * mm)
doc.addPageTemplates([PageTemplate(id="p", frames=[Frame(doc.leftMargin, doc.bottomMargin,
                                                          doc.width, doc.height, id="f")],
                                   onPage=_frame)])
doc.build(story)
print(out)
