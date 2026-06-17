# -*- coding: utf-8 -*-
"""Генерация PDF-руководства ATableSpec (кириллица через DejaVu)."""
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.platypus import (SimpleDocTemplate, Paragraph, Spacer, Table,
                                TableStyle, ListFlowable, ListItem, HRFlowable)
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

DJV = "/usr/share/fonts/truetype/dejavu"
pdfmetrics.registerFont(TTFont("DejaVu", f"{DJV}/DejaVuSans.ttf"))
pdfmetrics.registerFont(TTFont("DejaVu-Bold", f"{DJV}/DejaVuSans-Bold.ttf"))
pdfmetrics.registerFont(TTFont("DejaVu-Mono", f"{DJV}/DejaVuSansMono.ttf"))
pdfmetrics.registerFontFamily("DejaVu", normal="DejaVu", bold="DejaVu-Bold",
                              italic="DejaVu", boldItalic="DejaVu-Bold")

NAVY = colors.HexColor("#1f3a5f")
GREY = colors.HexColor("#555555")
LIGHT = colors.HexColor("#eef2f7")
LINE = colors.HexColor("#c9d3df")

def st(name, **kw):
    base = dict(fontName="DejaVu", fontSize=10, leading=14, textColor=colors.black)
    base.update(kw); return ParagraphStyle(name, **base)

title   = st("title", fontName="DejaVu-Bold", fontSize=20, leading=24, textColor=NAVY)
sub     = st("sub", fontSize=11, leading=15, textColor=GREY)
h1      = st("h1", fontName="DejaVu-Bold", fontSize=14, leading=18, textColor=NAVY,
            spaceBefore=14, spaceAfter=6)
h2      = st("h2", fontName="DejaVu-Bold", fontSize=11.5, leading=15, textColor=colors.black,
            spaceBefore=8, spaceAfter=3)
body    = st("body", spaceAfter=4)
small   = st("small", fontSize=9, leading=12, textColor=GREY)
cell    = st("cell", fontSize=9, leading=12)
cellm   = st("cellm", fontName="DejaVu-Mono", fontSize=8.5, leading=11)
note    = st("note", fontSize=9.5, leading=13, textColor=colors.HexColor("#7a3b00"))

def MONO(s): return f'<font name="DejaVu-Mono">{s}</font>'

doc = SimpleDocTemplate("/home/claude/ATableSpec_manual.pdf", pagesize=A4,
                        leftMargin=20*mm, rightMargin=18*mm, topMargin=18*mm, bottomMargin=16*mm,
                        title="ATableSpec — руководство", author="ATableSpec")
S = []

def bullets(items, style=body):
    return ListFlowable([ListItem(Paragraph(t, style), leftIndent=6) for t in items],
                        bulletType="bullet", bulletColor=NAVY, leftIndent=14, bulletFontSize=8)

# ── шапка ──
S += [Paragraph("ATableSpec", title),
      Paragraph("Спецификации и ведомости из блоков AutoCAD — без зависимости от СПДС", sub),
      Spacer(1, 4),
      Paragraph("Руководство: что сделано · что умеет · установка · работа &nbsp;·&nbsp; редакция 1.4 · июнь 2026", small),
      HRFlowable(width="100%", color=LINE, spaceBefore=8, spaceAfter=10)]

# ── 1. Назначение ──
S += [Paragraph("1. Назначение", h1),
      Paragraph("ATableSpec — плагин AutoCAD, который собирает данные из блоков чертежа "
                "(стойки, ригеля, заполнения, кронштейны, доборники, створки и т.п.) и вставляет "
                "ведомость/спецификацию обычной таблицей AutoCAD (" + MONO("AcDbTable") + "). "
                "Цель — получать спецификации напрямую из блоков и не зависеть от платной надстройки СПДС. "
                "Логика расчётов вынесена во внешний движок и настраивается, код AutoCAD при этом тонкий.", body)]

# ── 2. Что сделано на этом этапе ──
S += [Paragraph("2. Что сделано на этом этапе", h1),
      bullets([
          "Добавлен режим <b>«свой отчёт»</b> — команда " + MONO("ATSPECREPORT") + ": столбцы ведомости "
          "задаются <b>формулами над полями блока</b>, как в «Шаблоне отчёта» СПДС.",
          "Реализовано <b>ядро выражений</b> (отдельный модуль движка): ссылки на поля блока, арифметика, "
          "литералы, конкатенация, " + MONO("Count") + " / " + MONO("Sum") + " / " + MONO("Iff") + ", "
          "группировка и сортировка, а также <b>производные строки</b> (из одной стойки — строки крышки и прижима).",
          "Расширен контракт обмена движка (действие " + MONO("report") + "): отчёт строится по «сырым» полям "
          "любого блока (любой атрибут и слой), не требуя предварительной настройки под проект.",
          "<b>Авто-пересчёт таблиц</b>: команда " + MONO("ATSPECUPDATE") + " (ручной/принудительный пересчёт "
          "выбранных или всех отчётных таблиц) и <b>реактор</b> — правка блока обновляет таблицу сама по "
          "завершении команды. Определение отчёта хранится в самой таблице, поэтому её можно скопировать "
          "в другой чертёж и пересчитать там.",
          "Добавлена авто-проверка веток в CI (сборка плагина + тест движка) — отдельно от публикации релиза.",
          "Прежний режим " + MONO("ATSPEC") + " (быстрый запрос: фильтр/группировка/меры, готовые пресеты) — работает как раньше.",
      ])]

# ── 3. Что модуль даёт делать конструктору ──
S += [Paragraph("3. Что модуль позволяет делать", h1),
      Paragraph("3.1. " + MONO("ATSPEC") + " — быстрая ведомость", h2),
      Paragraph("Выбрать блоки → в окне указать готовый пресет (профили, раскрой, заполнения, кронштейны…) "
                "либо собрать запрос вручную (источник по слою, один фильтр, поля группировки, меры "
                + MONO("количество") + " / " + MONO("сумм. длина") + ") → таблица вставляется в чертёж.", body),
      Paragraph("3.2. " + MONO("ATSPECREPORT") + " — свой отчёт по формулам", h2),
      Paragraph("Выбрать блоки → в окне-построителе задать столбцы как выражения (по образцу СПДС): "
                "для каждого столбца — заголовок и формула. Источник ограничивается слоем (и при желании фильтром "
                "по атрибуту), строки группируются по выбранному столбцу и сортируются. Результат — таблица в чертеже. "
                "Так получаются и базовые спецификации, и производные комплектующие.", body),
      Paragraph("3.3. Выражения в ячейках (то же, что в построителе СПДС):", h2)]

rows = [
    [Paragraph("Выражение", cell), Paragraph("Что делает", cell)],
    [Paragraph(MONO("=Object.«ИМЯ»"), cellm), Paragraph("значение атрибута блока (марка)", cell)],
    [Paragraph(MONO("=Object.Name"), cellm), Paragraph("имя блока (у этих блоков совпадает с профилем/артикулом)", cell)],
    [Paragraph(MONO("=Object.«Длина»"), cellm), Paragraph("длина блока (размер по ручкам / атрибут)", cell)],
    [Paragraph(MONO("=Object.«Длина»-150"), cellm), Paragraph("арифметика над полем: укоротить/удлинить (любое число)", cell)],
    [Paragraph(MONO("=«01 02 04»"), cellm), Paragraph("литерал — например, свой артикул крышки", cell)],
    [Paragraph(MONO('="Опора "+Object.«ИМЯ»'), cellm), Paragraph("конкатенация строки и поля", cell)],
    [Paragraph(MONO("=Count"), cellm), Paragraph("количество элементов в группе", cell)],
    [Paragraph(MONO("=Iff(усл.; A; B)"), cellm), Paragraph("условие: A если истина, иначе B", cell)],
    [Paragraph(MONO("=row-1"), cellm), Paragraph("порядковый номер строки", cell)],
]
t = Table(rows, colWidths=[62*mm, 105*mm])
t.setStyle(TableStyle([
    ("BACKGROUND", (0,0), (-1,0), NAVY),
    ("TEXTCOLOR", (0,0), (-1,0), colors.white),
    ("FONTNAME", (0,0), (-1,0), "DejaVu-Bold"),
    ("ROWBACKGROUNDS", (0,1), (-1,-1), [colors.white, LIGHT]),
    ("GRID", (0,0), (-1,-1), 0.4, LINE),
    ("VALIGN", (0,0), (-1,-1), "MIDDLE"),
    ("LEFTPADDING", (0,0), (-1,-1), 5), ("RIGHTPADDING", (0,0), (-1,-1), 5),
    ("TOPPADDING", (0,0), (-1,-1), 3), ("BOTTOMPADDING", (0,0), (-1,-1), 3),
]))
S += [t, Spacer(1, 6),
      Paragraph("3.4. Производные строки", h2),
      Paragraph("Из одного источника отчёт может порождать несколько строк на каждый элемент. Пример: из каждой "
                "стойки — строка «крышка» (свой артикул-литерал, длина = " + MONO("Длина − 150") + ") и строка «прижим» "
                "(свой артикул, своя длина). Так комплектующие считаются из параметров несущих блоков по формуле.", body),
      Paragraph("3.5. Авто-пересчёт таблиц", h2),
      Paragraph("Построенная отчётная таблица «живая»: определение отчёта (формулы, источник, группировка) "
                "хранится в самой таблице. Изменили блок — длину, марку, добавили или удалили элемент — и по "
                "завершении текущей команды таблица пересчитывается сама из актуальных блоков чертежа. Команда "
                + MONO("ATSPECUPDATE") + " делает то же принудительно: пересчитать выбранные таблицы (или все "
                "отчётные в чертеже) прямо сейчас. Поскольку определение лежит внутри таблицы, готовую таблицу "
                "можно скопировать в другой чертёж с такими же блоками и пересчитать там.", body)]

# ── 4. Установка ──
S += [Paragraph("4. Установка (на машине нужен только AutoCAD)", h1),
      bullets([
          "Скачать <b>ATableSpec.bundle.zip</b> со страницы релизов: "
          + MONO("github.com/zinchenko-denis/AutoCAD") + " → Releases → latest.",
          "Распаковать архив.",
          "Папку <b>ATableSpec.bundle</b> положить в "
          + MONO("%APPDATA%\\Autodesk\\ApplicationPlugins\\") + " "
          "(обычно " + MONO("C:\\Users\\&lt;Имя&gt;\\AppData\\Roaming\\Autodesk\\ApplicationPlugins\\") + ").",
          "Запустить AutoCAD — бандл подхватывается автоматически (ручной " + MONO("NETLOAD") + " не нужен); "
          "плагин активен сразу: следит за правками блоков и держит отчётные таблицы в актуальном состоянии.",
      ]),
      Paragraph("Обновление — заменить папку " + MONO("ATableSpec.bundle") + " новой из релиза. "
                "Поддерживаемые версии AutoCAD: 2013–2024 (ветка под 2025+ заготовлена).", small)]

# ── 5. Работа ──
S += [Paragraph("5. Как пользоваться", h1),
      Paragraph("Таблицы строятся командами (набрали команду — получили таблицу), а после построения "
                "поддерживаются в актуальном состоянии автоматически. Отдельно запускать или останавливать "
                "ничего не нужно.", body),
      Paragraph("Быстрая ведомость:", h2),
      bullets([
          "В чертеже набрать " + MONO("ATSPEC") + " → Enter.",
          "Выбрать блоки (рамкой весь фасад или поштучно) → Enter.",
          "В окне выбрать пресет или собрать запрос → ОК.",
          "Указать точку вставки — таблица появится в модели.",
      ]),
      Paragraph("Свой отчёт по формулам:", h2),
      bullets([
          "Набрать " + MONO("ATSPECREPORT") + " → Enter, выбрать блоки → Enter.",
          "В окне задать источник (слой), при желании фильтр, и столбцы (Заголовок | Выражение). "
          "По умолчанию подставлен базовый шаблон спецификации — его можно править.",
          "Указать столбец группировки и порядок сортировки → «Построить».",
          "Указать точку вставки таблицы.",
      ]),
      Paragraph("Пересчёт при правках:", h2),
      bullets([
          "После правки блоков (длина, марка, добавление/удаление) таблица обновляется <b>сама</b> "
          "по завершении команды — отдельных действий не требуется.",
          "Нужно пересчитать немедленно или принудительно — команда " + MONO("ATSPECUPDATE") + ": "
          "выбрать отчётные таблицы (или нажать Enter без выбора — пересчитаются все) → готово.",
          "Скопировали готовую таблицу в другой чертёж с такими же блоками → " + MONO("ATSPECUPDATE") +
          " подтянет значения из нового чертежа.",
      ])]

# ── 6. Границы ──
S += [Paragraph("6. Границы (важно понимать)", h1),
      bullets([
          "Модуль <b>не записывает</b> результат обратно в формат таблиц СПДС и не редактирует их «живьём» — "
          "он строит собственную таблицу AutoCAD из блоков.",
          "Авто-пересчёт срабатывает по <b>завершении команды</b>. Правки в обход команд "
          "(скрипты/LISP/сторонние надстройки) могут не подхватиться сразу — тогда выполните " + MONO("ATSPECUPDATE") + ".",
          "На очень больших чертежах (тысячи блоков) авто-пересчёт после каждой команды может слегка "
          "подтормаживать — это ожидаемо и будет оптимизировано.",
          "Готовую таблицу AutoCAD дальше можно править штатными средствами (двойной клик, формулы в ячейках, "
          "добавление строк/столбцов, ручки ширины).",
      ], style=note)]

# ── 7. Дальше ──
S += [Paragraph("7. Дальнейшие этапы", h1),
      bullets([
          "Двухсекционная таблица в окне: ручные строки («Данные») и автоген («Отчёт») в одной таблице.",
          "Ввод <b>нескольких шаблонов</b> (крышка + прижим в одном отчёте) прямо в окне.",
          "«Пипетка» для фильтра — взять значение прямо с объекта.",
          "Полевая проверка на реальных КМД.",
      ]),
      HRFlowable(width="100%", color=LINE, spaceBefore=12, spaceAfter=6),
      Paragraph("Команды: " + MONO("ATSPEC") + " — быстрая ведомость; " + MONO("ATSPECREPORT") +
                " — свой отчёт по формулам; " + MONO("ATSPECUPDATE") + " — пересчитать отчётные таблицы. "
                "Репозиторий и релизы: " + MONO("github.com/zinchenko-denis/AutoCAD") + ".", small)]

doc.build(S)
print("PDF готов")
