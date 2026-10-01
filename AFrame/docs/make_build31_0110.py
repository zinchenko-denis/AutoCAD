# -*- coding: utf-8 -*-
"""Reproduce the public memo for German: build31 / installer build-bundle #104.

Usage: python3 AFrame/docs/make_build31_0110.py [build_number] [source_commit]
Requires reportlab and DejaVu Sans. No private input files or network access.
The PDF uses deterministic metadata; repeated runs with the same inputs match.
"""

from pathlib import Path
import argparse
import re
from xml.sax.saxutils import escape

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen.canvas import Canvas
from reportlab.platypus import (
    BaseDocTemplate, Frame, KeepTogether, PageBreak, PageTemplate,
    Paragraph, Spacer, Table, TableStyle,
)


DEFAULT_SHA = "ec5160983f61ce5668369d2513313391db4d6c5d"
GUIDE_SHA = "60f3b3587c8d09780e5c7a703f010de690f73e2b"
REPO = "https://github.com/zinchenko-denis/AutoCAD"
HERE = Path(__file__).resolve().parent
OUTPUT = HERE / "FACADES_build31_0110.pdf"
FONT_DIR = Path("/usr/share/fonts/truetype/dejavu")
BLUE = colors.HexColor("#214e70")
INK = colors.HexColor("#203242")
MUTED = colors.HexColor("#566774")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("build", nargs="?", default="104")
    parser.add_argument("sha", nargs="?", default=DEFAULT_SHA)
    args = parser.parse_args()
    if not args.build.isdigit() or not re.fullmatch(r"[0-9a-f]{40}", args.sha):
        parser.error("Expected a numeric build number and full lowercase commit SHA")

    for name, filename in (("DV", "DejaVuSans.ttf"), ("DVB", "DejaVuSans-Bold.ttf")):
        pdfmetrics.registerFont(TTFont(name, str(FONT_DIR / filename)))
    pdfmetrics.registerFontFamily("DV", normal="DV", bold="DVB", italic="DV", boldItalic="DVB")

    styles = {
        "eyebrow": ParagraphStyle("eyebrow", fontName="DVB", fontSize=9, leading=12,
                                  textColor=BLUE, spaceAfter=3*mm),
        "h1": ParagraphStyle("h1", fontName="DVB", fontSize=21, leading=26,
                             textColor=INK, spaceAfter=4*mm),
        "h2": ParagraphStyle("h2", fontName="DVB", fontSize=12, leading=16,
                             textColor=INK, spaceBefore=3*mm, spaceAfter=2*mm,
                             keepWithNext=True),
        "p": ParagraphStyle("p", fontName="DV", fontSize=10, leading=14,
                            textColor=INK, spaceAfter=2.5*mm),
        "small": ParagraphStyle("small", fontName="DV", fontSize=8.5, leading=11.5,
                                textColor=MUTED, spaceAfter=2*mm),
        "cell": ParagraphStyle("cell", fontName="DV", fontSize=9, leading=12,
                               textColor=INK),
        "headcell": ParagraphStyle("headcell", fontName="DVB", fontSize=9, leading=12,
                                   textColor=colors.white),
    }

    def p(text, style="p"):
        return Paragraph(text, styles[style])

    def link(label, url):
        return '<a href="%s" color="#1b6291"><u>%s</u></a>' % (escape(url, {'"': '&quot;'}), escape(label))

    def table(rows, widths):
        data = [[p(c, "headcell" if i == 0 else "cell") for c in row]
                for i, row in enumerate(rows)]
        t = Table(data, colWidths=[w*mm for w in widths], repeatRows=1, hAlign="LEFT")
        t.setStyle(TableStyle([
            ("BACKGROUND", (0, 0), (-1, 0), BLUE),
            ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.HexColor("#f2f6f8"), colors.white]),
            ("LINEBELOW", (0, 0), (-1, -1), .35, colors.HexColor("#d5dfe5")),
            ("VALIGN", (0, 0), (-1, -1), "TOP"),
            ("LEFTPADDING", (0, 0), (-1, -1), 7),
            ("RIGHTPADDING", (0, 0), (-1, -1), 7),
            ("TOPPADDING", (0, 0), (-1, -1), 6),
            ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
        ]))
        return t

    def scenario(number, role, action, expected):
        return KeepTogether([
            p(f"{number}. {role}", "h2"),
            p(action),
            p("<b>Проверить:</b> " + expected),
        ])

    release = f"{REPO}/releases/tag/build-{args.build}"
    pinned = f"{REPO}/releases/download/build-{args.build}/"
    latest = f"{REPO}/releases/download/latest/"
    source = f"{REPO}/tree/{args.sha}"
    guide = f"{REPO}/blob/{GUIDE_SHA}/docs/facades_beginner_3009/Facades_Beginner_Manual_30.09.docx"
    scenarios = f"{REPO}/blob/{args.sha}/docs/catalog_connections_0110/README.md"
    changes = f"{REPO}/blob/{args.sha}/docs/CATALOG_CONNECTIONS_01.10.md"

    story = [
        p("ГЕРМАНУ / ПАМЯТКА №31 / 01.10.2026", "eyebrow"),
        p("Фасады: ведомости<br/>и паспорт соединений", "h1"),
        p(f"Установочный <b>build-bundle #{args.build}</b>. Изменения после #103: "
          "ведомости облицовки и подсистемы, учёт зарегистрированных ручных деталей "
          "и этап 2А - геометрия соединений."),
        p("<b>Живая проверка AutoCAD ещё не выполнена.</b> Локальные регрессии, "
          "синтетические сценарии и компиляция не подтверждают работу транзакций, "
          "динамических блоков и скорость в реальном DWG."),
        p("Что установить", "h2"),
        p("1. Закрыть AutoCAD. Сохранить прежние папки bundle и копию рабочего DWG.<br/>"
          "2. Обновить <b>все три</b> фасадных bundle из одного выпуска. Распаковать "
          "ZIP и заменить соответствующие папки в:<br/>"
          "%APPDATA%\\Autodesk\\ApplicationPlugins\\<br/>"
          "3. Перезапустить AutoCAD. Начать проверку на копии или учебном чертеже."),
        table([
            ["Плагин", f"Зафиксированный build-{args.build}", "Постоянная ссылка"],
            *[[name, link("Скачать ZIP этой сборки", pinned + name + ".bundle.zip"),
               link("Скачать latest ZIP", latest + name + ".bundle.zip")]
              for name in ("AFacades", "AClad", "AFrame")],
        ], [33, 76, 69]),
        Spacer(1, 2*mm),
        p(link(f"Страница выпуска build-{args.build}", release) + " · " +
          link("Исходники этой сборки", source), "small"),
        p("Ссылки latest со временем обновляются. Для воспроизведения результатов "
          f"использовать build-{args.build}. ATableSpec и ABlockGen этой проверкой не затрагиваются.", "small"),
        p("Команды и руководство", "h2"),
        table([
            ["Команда", "Назначение"],
            ["ATFZONE", "Зоны и исходные границы работ."],
            ["ATCLAD / ATTILE", "Раскладка облицовки."],
            ["ATFRAME", "Построение подсистемы; режим и ограничения проверять отдельно."],
            ["ATFTABLE", "Зоны, работы, облицовка, подсистема; регистрация ручных деталей."],
        ], [44, 134]),
        Spacer(1, 2*mm),
        p(link("Руководство новичка (DOCX)", guide) +
          " - 42 страницы, 20 глав, 10 иллюстраций и 4 учебных DXF.", "small"),
        p("Этап 2Б - следующая работа с каталогом в исходниках. "
          "Автоподбор изделий и параметрические узлы в этот выпуск не включены.", "small"),

        PageBreak(),
        p("ЧТО ИЗМЕНИЛОСЬ ПОСЛЕ #103", "eyebrow"),
        p("Состав, актуальность<br/>и границы расчёта", "h1"),
        table([
            ["В ATFTABLE", "Что получаем"],
            ["Облицовка", "Фактические детали, размеры и площади. Контур и его заливка "
             "не становятся двумя деталями. Групповой раскрой и состав выбранных зон имеют разные основания."],
            ["Подсистема → Элементы", "Вариант по умолчанию: фактические профили, шины, "
             "кронштейны и кляммеры, категории и длины. Оценка хлыстов отдельно; это не раскрой и не закупка."],
            ["Ручные → Импорт / Исключить", "Явное назначение образца и полей, проверка геометрии. "
             "Ручные и автоматические детали учитываются совместно. Исключение снимает регистрацию, "
             "сохраняя геометрию. Номенклатура ручной части неполна; в оценку хлыстов она не входит."],
            ["Подсистема → Соединения", "Отдельный паспорт физических направляющих: кандидаты опор, "
             "интервалы, свободные концы, соседние куски и справочные идентичности источника."],
        ], [49, 129]),
        p("Общие правила выдачи", "h2"),
        p("Предпросмотр, таблица в DWG, XLSX или оба результата. Выбор одной распознанной "
          "детали включает состав её зоны. Повторный выбор объекта не удваивает количество. "
          "Разные совпадающие объекты остаются в составе с предупреждением; пользователь "
          "разбирает возможный дубль."),
        p("Изменение или потеря исходников, деталей, связей и свойств вызывает отказ в "
          "выдаче устаревшего результата. После диалогов источники проверяются снова. "
          "Обновляется только свой вид таблицы; примечание сохраняется. Отмена не должна "
          "портить прежний результат. Отсутствие данных не подменяется нулём."),
        p("Паспорт 2А: геометрия, которую нужно проверить", "h2"),
        p("Область: явно выбранные <b>Вектор-1 + вертикальная схема + керамогранит</b>. "
          "Кандидат опоры не подтверждает реальное закрепление. Нулевой зазор между "
          "кусками не доказывает непрерывность; «несущий» не означает «неподвижный». "
          "Без кандидатов опор таблица явно сообщает об их отсутствии. Старый, ручной "
          "или неподдержанный состав получает причину «паспорт не сформирован», "
          "сохраняя доступные количества обычной ведомости."),
        p("Точное совпадение марки с одним расчётным примером - только справочная "
          "идентификация. Это не артикул производителя, не подбор и не подтверждённые "
          "характеристики изделия."),
        p("Ограничения сохраняются", "h2"),
        p("Межэтажный автоматический расчёт заблокирован. Вертикальная и ортогональная "
          "схемы имеют ограниченную расчётную цепочку и геометрический контроль; полной "
          "проверки опор, стыков и статической модели нет. Ручной режим не даёт расчётного разрешения."),
        p("Тип 5 и NordFOX не реализованы; АТР Вектор-4 отсутствует. Расчёт НСП-2 и "
          "ГП-60-40 ограничен до подтверждения сечений. АКП - каркас без узла крепления "
          "кассеты. Вынос и утеплитель не запускают автоподбор кронштейна. "
          "Новые нормы расхода СФТК не добавлены.", "small"),

        PageBreak(),
        p("ЖИВАЯ ПРИЁМКА / СЦЕНАРИИ 1-4", "eyebrow"),
        p("Сверить ведомость<br/>с чертежом", "h1"),
        p("Для первого сценария выбрать Вектор-1 явно: прежние учебные примеры "
          "с пресетом Standart не подтверждают область нового паспорта. Во всех "
          "сценариях сохранять DWG до и после, ожидаемое и фактическое поведение."),
        scenario(1, "Проектировщик",
                 "На учебной зоне построить вертикальный каркас Вектор-1 под керамогранит "
                 "в ручном режиме. Выполнить ATFTABLE → Подсистема сначала с видом "
                 "«Элементы», затем «Соединения».",
                 "числа и реальные длины деталей, каждую направляющую и положения "
                 "кандидатов опор по чертежу. Ручной режим не должен снимать ограничения расчёта."),
        scenario(2, "Проверяющий инженер",
                 "Через полное перестроение получить варианты с разным числом и положением "
                 "опор, разрезанием направляющих и концевыми участками. Отдельно проверить "
                 "нулевой зазор, разрыв, перекрытие проекций и отсутствие кандидатов опор.",
                 "число и длины интервалов, свободные концы. Закрепление и непрерывность "
                 "должны оставаться неподтверждёнными; геометрический сосед не становится "
                 "монтажным стыком. Роль «несущий» не включает неподвижное закрепление."),
        scenario(3, "Оператор ведомостей",
                 "Выбрать две зоны и одну деталь. Проверить варианты «ПоЗонам / Общая», "
                 "«Чертеж / Ексель / Оба». Обновить существующую таблицу своего вида; "
                 "затем попытаться обновить таблицу другого вида.",
                 "полный охват соответствующей зоны без ссылок на соседнюю. В паспорте "
                 "физические направляющие не суммируются в одну строку даже при «Общая». "
                 "Примечание сохраняется; чужой вид таблицы получает отказ и остаётся прежним."),
        scenario(4, "Автор смешанного проекта",
                 "Добавить зарегистрированный ручной образец, старый каркас с действующей "
                 "ведомостью, но без нового паспорта соединений, и каркас иной схемы. "
                 "Отдельно выполнить повтор ATFRAME «только кляммеры».",
                 "обычная ведомость сохраняет количества. Для отсутствующего паспорта "
                 "видна причина, а не ноль. Повтор только кляммеров сохраняет паспорт "
                 "каркаса лишь при полном подтверждении оставшихся связей."),

        PageBreak(),
        p("ЖИВАЯ ПРИЁМКА / СЦЕНАРИИ 5-7", "eyebrow"),
        p("Проверить изменения,<br/>отмену и скорость", "h1"),
        scenario(5, "Рецензент актуальности",
                 "Применить MOVE, STRETCH, COPY и ERASE к направляющей или кронштейну; "
                 "изменить зону, раскладку и определение блока. Сохранить и снова открыть DWG.",
                 "старый паспорт не выдаётся за актуальный. После корректного полного "
                 "перестроения новый снимок читается. COPY ручного образца требует "
                 "явной регистрации новой детали; частичный повтор не освежает остальные записи."),
        scenario(6, "Отмена и запись",
                 "Нажать Esc на диалогах, изменить источник после предпросмотра, "
                 "смоделировать ошибку записи XLSX и обновление занятой таблицы. Проверить Undo.",
                 "отказ и отмена сохраняют прежние DWG/XLSX согласно сценарию транзакций. "
                 "Не должно оставаться частично обновлённой выдачи или утраченного примечания."),
        scenario(7, "Большой пилот",
                 "Повторить операции на нескольких размерах модели и числе зон. "
                 "Записать версию AutoCAD, характеристики компьютера, объём DWG, "
                 "число зон и физических деталей.",
                 "раздельное время чтения/проверки CAD, чистых вычислений, вывода таблицы, "
                 "сохранения/открытия DWG и память. Число полных обходов DWG не должно расти "
                 "пропорционально числу направляющих. Порог допустимой задержки согласовать "
                 "по этому пилоту: время Python и стендовых CAD doubles его не доказывает."),
        p("Что вернуть по результатам", "h2"),
        p(f"Указать <b>build-{args.build}, исходники {args.sha[:7]}</b> и установленную "
          "версию AutoCAD. Для каждого сценария: «пройден / не пройден / не проверен», "
          "шаги, ожидаемый и фактический результат, время, скриншот и минимальный DWG. "
          "Приватные проекты передавать согласованным приватным способом."),
        p("Два вопроса после проверки", "h2"),
        p("1. Совпали ли кандидаты опор, интервалы и отдельные куски с фактическим "
          "каркасом? Для расхождения нужны конкретный узел и подтверждённое правило "
          "закрепления/непрерывности из альбома или расчёта.<br/>"
          "2. Какое время ожидания приемлемо на вашем рабочем DWG и на какой операции "
          "получена наибольшая задержка?"),
        p(link("Исходный план семи live-сценариев", scenarios) + " · " +
          link("Границы этапа 2А и источники", changes), "small"),
        p("Памятка задаёт проверку; результаты живой приёмки в ней не заявлены. "
          "Следующий этап 2Б ведётся отдельно от состава этого выпуска.", "small"),
    ]

    def decorate(canvas, doc):
        canvas.saveState()
        canvas.setStrokeColor(colors.HexColor("#c7d5df"))
        canvas.setLineWidth(.45)
        canvas.line(16*mm, 15*mm, 194*mm, 15*mm)
        canvas.setFont("DV", 7.7)
        canvas.setFillColor(MUTED)
        canvas.drawString(16*mm, 10.5*mm,
                          f"Памятка №31 · build-bundle #{args.build} · {args.sha[:7]} · 01.10.2026")
        canvas.drawRightString(194*mm, 10.5*mm, f"{doc.page}")
        canvas.restoreState()

    class StableCanvas(Canvas):
        def __init__(self, *a, **kw):
            kw["invariant"] = 1
            super().__init__(*a, **kw)

    doc = BaseDocTemplate(str(OUTPUT), pagesize=A4, leftMargin=16*mm,
                          rightMargin=16*mm, topMargin=15*mm, bottomMargin=21*mm,
                          title=f"Фасады: памятка №31, build-bundle #{args.build}",
                          author="Проект AutoCAD / фасадные плагины",
                          subject="Ведомости, паспорт соединений 2А и план живой приёмки")
    frame = Frame(doc.leftMargin, doc.bottomMargin, doc.width, doc.height,
                  leftPadding=0, rightPadding=0, topPadding=0, bottomPadding=0)
    doc.addPageTemplates(PageTemplate(id="memo", frames=frame, onPage=decorate))
    doc.build(story, canvasmaker=StableCanvas)
    print(OUTPUT)


if __name__ == "__main__":
    main()
