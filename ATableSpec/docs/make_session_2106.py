# -*- coding: utf-8 -*-
"""ATableSpec — детальная инструкция изменений сессии (правка 21.06):
четыре изменения по двум видео Алексея + как этим пользоваться.
Стиль/шрифты — как в make_changelog.py (кириллица через DejaVu)."""
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
WARM = colors.HexColor("#7a3b00")

def st(name, **kw):
    base = dict(fontName="DejaVu", fontSize=10, leading=14, textColor=colors.black)
    base.update(kw); return ParagraphStyle(name, **base)

title = st("title", fontName="DejaVu-Bold", fontSize=20, leading=24, textColor=NAVY)
sub   = st("sub", fontSize=11, leading=15, textColor=GREY)
h1    = st("h1", fontName="DejaVu-Bold", fontSize=14, leading=18, textColor=NAVY,
          spaceBefore=14, spaceAfter=6)
h2    = st("h2", fontName="DejaVu-Bold", fontSize=11.5, leading=15, textColor=colors.black,
          spaceBefore=8, spaceAfter=3)
body  = st("body", spaceAfter=4)
small = st("small", fontSize=9, leading=12, textColor=GREY)
note  = st("note", fontSize=9.5, leading=13, textColor=WARM)
mono  = st("mono", fontName="DejaVu-Mono", fontSize=9, leading=12)

def bullets(items, gap=3):
    li = [ListItem(Paragraph(t, body), leftIndent=6, value="•") for t in items]
    return ListFlowable(li, bulletType="bullet", start="•", leftIndent=12,
                        bulletColor=NAVY, spaceBefore=2, spaceAfter=gap)

def steps(items, gap=4):
    li = [ListItem(Paragraph(t, body), leftIndent=6) for t in items]
    return ListFlowable(li, bulletType="1", leftIndent=14, bulletColor=NAVY,
                        spaceBefore=2, spaceAfter=gap)

def rule():
    return HRFlowable(width="100%", thickness=0.8, color=LINE,
                      spaceBefore=8, spaceAfter=8)

S = []
S.append(Paragraph("ATableSpec — что нового и как пользоваться", title))
S.append(Paragraph("Правка 21.06.2026 · по двум видео Алексея · сборка с ветки feat/auto-reactor", sub))
S.append(rule())
S.append(Paragraph(
    "В эту правку вошли четыре изменения: три по большому видео (окно шаблона, "
    "ввод выражения, фильтр) и одно по короткому (ведомость раскроя). Остальной "
    "функционал — многосекционные отчёты, шаблон «Заполнения» (Тип/Площадь/ИТОГ), "
    "объединение шапки, масштаб, авто-пересчёт — без изменений.", body))

# 1
S.append(Paragraph("1. Шаблон выбирается прямо в окне (без отдельного окна выбора)", h1))
S.append(Paragraph("Что изменилось", h2))
S.append(Paragraph(
    "Отдельное окно выбора шаблона, которое раньше выскакивало первым, убрано. "
    "Команда сразу открывает построитель отчёта. Шаблон теперь выбирается "
    "выпадающим списком «Шаблон» в верхней части окна.", body))
S.append(Paragraph("Как пользоваться", h2))
S.append(steps([
    "Запустите команду <font name='DejaVu-Mono'>ATSPECREPORT</font> и выберите блоки — сразу открывается построитель.",
    "Вверху окна в списке «Шаблон» выберите нужный: «Ручное (пусто)», «Спецификация», «Раскрой» или «Заполнения».",
    "При выборе шаблона столбцы засеваются заготовкой. Программа спросит подтверждение, если в секциях уже что-то набрано — чтобы случайно не затереть.",
    "Кнопка «+ Добавить отчёт» добавляет ещё одну секцию (например, отдельно стойки и отдельно ригеля в одной таблице).",
]))
S.append(Paragraph(
    "По умолчанию открывается «Спецификация». Любой столбец, источник, фильтр и "
    "группировка остаются полностью редактируемыми.", small))

# 2
S.append(Paragraph("2. Выражение в столбце вводится свободно и сразу сохраняется", h1))
S.append(Paragraph("Что изменилось", h2))
S.append(Paragraph(
    "Раньше при ручной правке выражения (например, <font name='DejaVu-Mono'>=Object «Длина»-100</font>) "
    "ячейка не принимала введённый текст и возвращала прежнее значение — приходилось "
    "выбирать «подправленное» значение из выпадающего списка. Теперь столбец «Выражение» — "
    "обычное текстовое поле: что напечатали, то и осталось.", body))
S.append(Paragraph("Как пользоваться", h2))
S.append(steps([
    "Печатайте выражение прямо в ячейке «Выражение» — арифметику тоже (<font name='DejaVu-Mono'>=Object «Длина»-100</font>, <font name='DejaVu-Mono'>=Object «Длина»-20</font> и т.п.).",
    "При вводе появляются подсказки готовых вставок (<font name='DejaVu-Mono'>=Object «…»</font>, <font name='DejaVu-Mono'>=row</font>, <font name='DejaVu-Mono'>=Count</font>) — можно выбрать из них или дописать своё.",
    "Правый клик по таблице столбцов → «Вставить выражение» → выбрать поле — оно подставится в текущую строку.",
]))
S.append(Paragraph(
    "Напоминание по синтаксису: <font name='DejaVu-Mono'>=Object «ИМЯ»</font> — атрибут; "
    "<font name='DejaVu-Mono'>=Object.Name</font> — имя блока (артикул); "
    "<font name='DejaVu-Mono'>=Count</font> — количество в группе; <font name='DejaVu-Mono'>=row</font> — нумерация; "
    "ячейка без ведущего «=» считается текстом-литералом.", small))

# 3
S.append(Paragraph("3. Фильтр — видимая таблица со значениями по выбранному слою", h1))
S.append(Paragraph("Что изменилось", h2))
S.append(Paragraph(
    "Фильтр больше не в отдельном всплывающем окне, а виден прямо в карточке секции "
    "в виде таблицы «Поле | Условие | Значение». Главное: и список полей, и список "
    "значений берутся ТОЛЬКО по выбранному источнику-слою — не вперемешку по всем "
    "элементам чертежа.", body))
S.append(Paragraph("Как пользоваться", h2))
S.append(steps([
    "Кнопкой «Источник…» задайте слой (например, RF-стойки).",
    "В таблице фильтра в столбце «Поле» выберите поле — в списке только поля блоков этого слоя (например, МАРКИРОВКА).",
    "В «Условие» выберите оператор: <font name='DejaVu-Mono'>=</font>, <font name='DejaVu-Mono'>≠</font>, «содержит», «не содержит», <font name='DejaVu-Mono'>&gt; &lt; ≥ ≤</font>.",
    "В «Значение» начните печатать — выпадут реальные значения этого поля у блоков слоя (например, С01, С02, С03). Можно выбрать из списка или вписать вручную.",
    "Несколько строк фильтра работают вместе по логике И (все условия одновременно).",
]))
S.append(Paragraph(
    "Пример: источник RF-стойки, фильтр «МАРКИРОВКА содержит С» — в отчёт попадут только "
    "стойки с маркировкой на С; элементы с другой буквой будут отброшены.", small))
S.append(Spacer(1, 2))
S.append(Paragraph(
    "Замечание. Фильтр сделан отдельной таблицей условий, а не двумя столбцами внутри "
    "таблицы столбцов отчёта. Причина: строка таблицы столбцов — это выходная колонка с "
    "произвольной формулой, и «фильтр по такой строке» был бы неоднозначен. Наглядность "
    "(условие и значение видны сразу) и значения по слою при этом полностью обеспечены. "
    "Если нужнее именно столбцы в самой таблице отчёта — скажите, переделаем.", note))

# 4
S.append(Paragraph("4. Ведомость раскроя — только «Длина» и «Количество»", h1))
S.append(Paragraph("Что изменилось", h2))
S.append(Paragraph(
    "Из шаблона «Раскрой» убраны столбцы «№ п/п» и «Профиль». Остались два столбца — "
    "«Длина, мм» и «Колич.». Группировка идёт по длине: одинаковые длины сводятся, "
    "рядом — их количество; сортировка по возрастанию длины.", body))
S.append(Paragraph("Как пользоваться", h2))
S.append(steps([
    "В списке «Шаблон» выберите «Раскрой».",
    "Кнопкой «Источник…» задайте слой профиля (например, RF-стойки или RF-ригеля).",
    "Постройте — получите ведомость «Длина | Количество», сгруппированную по длине.",
]))
S.append(Paragraph(
    "Шаблон «Заполнения» по этому видео не менялся; по нему остаётся только доработка "
    "фильтра, которая уже сделана (см. п. 3).", small))

S.append(rule())
S.append(Paragraph("Установка обновления", h1))
S.append(steps([
    "Закройте AutoCAD.",
    "Скачайте архив сборки (ATableSpec.bundle.zip) и распакуйте папку ATableSpec.bundle в %APPDATA%\\Autodesk\\ApplicationPlugins\\, заменив старую.",
    "Запустите AutoCAD — плагин загрузится сам.",
]))
S.append(Paragraph(
    "Команды: <font name='DejaVu-Mono'>ATSPECREPORT</font> — отчёт по формулам (это окно); "
    "<font name='DejaVu-Mono'>ATSPEC</font> — быстрая ведомость; "
    "<font name='DejaVu-Mono'>ATSPECUPDATE</font> — пересчитать таблицу вручную; "
    "<font name='DejaVu-Mono'>ATSPECDUMP</font> — показать точные имена атрибутов/параметров блока.", small))
S.append(Spacer(1, 4))
S.append(Paragraph(
    "Эти правки проверены на компиляцию автосборкой; поведение в живом AutoCAD "
    "(подсказки, подстановка значений фильтра, вид таблиц) просьба проверить и прислать замечания.", small))

doc = SimpleDocTemplate("ATableSpec_session_2106.pdf", pagesize=A4,
                        leftMargin=20*mm, rightMargin=18*mm,
                        topMargin=16*mm, bottomMargin=16*mm,
                        title="ATableSpec — изменения 21.06")
doc.build(S)
print("PDF готов: ATableSpec_session_2106.pdf")
