# -*- coding: utf-8 -*-
"""ATableSpec — инструкция по сессии 22.06: финальный построитель отчёта.
Что сделано в сессии + как пользоваться новым функционалом (для конструкторов).
Стиль/шрифты — как в make_session_2106.py (кириллица через DejaVu)."""
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
mono  = st("mono", fontName="DejaVu-Mono", fontSize=9, leading=13)

def bullets(items, gap=3):
    li = [ListItem(Paragraph(t, body), leftIndent=6, value="•") for t in items]
    return ListFlowable(li, bulletType="bullet", start="•", leftIndent=12,
                        bulletColor=NAVY, spaceBefore=2, spaceAfter=gap)

def steps(items, gap=4):
    li = [ListItem(Paragraph(t, body), leftIndent=6) for t in items]
    return ListFlowable(li, bulletType="1", leftIndent=14, bulletColor=NAVY,
                        spaceBefore=2, spaceAfter=gap)

def rule():
    return HRFlowable(width="100%", thickness=0.8, color=LINE, spaceBefore=8, spaceAfter=8)

def exprtable(rows):
    data = [[Paragraph(f"<font name='DejaVu-Mono'>{e}</font>", mono), Paragraph(d, body)] for e, d in rows]
    t = Table(data, colWidths=[78*mm, 90*mm])
    t.setStyle(TableStyle([
        ("FONTNAME", (0, 0), (-1, -1), "DejaVu"),
        ("FONTSIZE", (0, 0), (-1, -1), 9),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LINEBELOW", (0, 0), (-1, -1), 0.4, LINE),
        ("ROWBACKGROUNDS", (0, 0), (-1, -1), [colors.white, LIGHT]),
        ("TOPPADDING", (0, 0), (-1, -1), 3),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
        ("LEFTPADDING", (0, 0), (-1, -1), 5),
    ]))
    return t

S = []
S.append(Paragraph("ATableSpec — что нового и как пользоваться", title))
S.append(Paragraph("Сессия 22.06.2026 · финальный построитель отчёта · сборка с ветки "
                   "feat/auto-reactor (HEAD 3ead274) · по фидбэку Алексея с видео", sub))
S.append(rule())
S.append(Paragraph(
    "Эта сессия завершает построитель отчёта. Окно стало нагляднее: шаблон, фильтр и "
    "группировка теперь настраиваются прямо в одной таблице. Остальное — многосекционные "
    "отчёты, шаблон «Заполнения» (Тип/Площадь/сумма), объединение шапки, масштаб, "
    "авто-пересчёт при правке блоков — работает как раньше.", body))

S.append(Paragraph("Коротко: что изменилось в этой сессии", h1))
S.append(bullets([
    "<b>Шаблон выбирается прямо в поле «Заголовок»</b> — отдельного поля «Шаблон» больше нет.",
    "<b>«Выражение» — свободный ввод.</b> Можно печатать своё (например, длину со смещением), "
    "и текст сохраняется. Подсказки — автодополнением по мере ввода и правым кликом «Вставить выражение».",
    "<b>Фильтр — столбцами в таблице</b> («Условие» и «Значение»), без отдельной строки фильтра.",
    "<b>Группировка — тоже столбцом в таблице</b> («Группа»): сразу видно, что и где группируется.",
    "<b>Шаблон «Раскрой» открывается без заголовка таблицы</b> (галка «Скрыть заголовок» уже стоит).",
    "<b>Из списков полей убраны служебные имена деталировки</b> (DOBL, DOBR, KLL, KLR, L, R, UGL, UGR).",
]))

S.append(rule())
S.append(Paragraph("1. Как открыть и общий порядок", h1))
S.append(steps([
    "Команда <font name='DejaVu-Mono'>ATSPECREPORT</font> — откроется окно «построитель отчёта».",
    "Выберите шаблон (в поле «Заголовок»), при необходимости задайте источник-слой, поправьте столбцы.",
    "Нажмите «Построить» — таблица вставится в чертёж.",
    "Дальше таблица пересчитывается сама при изменении блоков; вручную — <font name='DejaVu-Mono'>ATSPECUPDATE</font>.",
]))

S.append(Paragraph("2. Шапка окна: Заголовок, Скрыть заголовок, Масштаб", h1))
S.append(Paragraph(
    "<b>Заголовок</b> — это и заголовок таблицы, и выбор шаблона. Раскройте список и выберите шаблон "
    "(Спецификация / Раскрой / Заполнения / Ручное) — столбцы заполнятся заготовкой и подставится "
    "заголовок по умолчанию (у «Раскрой» — пустой). Свой заголовок можно вписать руками.", body))
S.append(Paragraph(
    "<b>Скрыть заголовок таблицы</b> — галка убирает строку заголовка из таблицы (для «Раскрой» стоит сразу). "
    "<b>Масштаб</b> — масштаб вставляемой таблицы; запоминается для следующего раза.", body))

S.append(Paragraph("3. Таблица «Столбцы, фильтр и группировка»", h1))
S.append(Paragraph(
    "Главная таблица окна. Каждая строка — один столбец будущего отчёта. Пять колонок: "
    "<b>Заголовок | Выражение | Условие | Значение | Группа</b>.", body))
S.append(Paragraph("Заголовок", h2))
S.append(Paragraph(
    "Текст шапки столбца в таблице. Если оставить пустым, столбец в таблицу не попадёт — "
    "такую строку удобно использовать только для фильтра (см. ниже).", body))
S.append(Paragraph("Выражение — что подставить в столбец", h2))
S.append(Paragraph(
    "Свободный ввод: начните печатать — появятся подсказки; либо правый клик по таблице → "
    "«Вставить выражение». Примеры:", body))
S.append(exprtable([
    ("=Object.«ИМЯ»", "атрибут блока (например, маркировка)"),
    ("=Object.Name", "имя блока (часто это артикул)"),
    ("=Object.«Длина»", "длина из свойства/атрибута блока"),
    ("=Object.«Длина»-20", "длина со смещением (например, ригель −20 мм)"),
    ("=Count", "количество одинаковых в группе"),
    ("=row", "номер по порядку"),
    ("=«шт.»", "просто текст (литерал)"),
    ('="Опора "+Object.«ИМЯ»', "склейка текста и атрибута"),
    ("=Object.«Ширина»*Object.«Высота»*Count/1000000", "площадь, м² (для заполнений)"),
]))
S.append(Paragraph(
    "ВАЖНО: точка обязательна — пишите <font name='DejaVu-Mono'>=Object.«…»</font> (с точкой), "
    "а не <font name='DejaVu-Mono'>=Object «…»</font>. Со старым «пробельным» синтаксисом при "
    "пересчёте ячейка станет пустой.", note))
S.append(Paragraph("Условие / Значение — фильтр", h2))
S.append(bullets([
    "Условие фильтрует по полю из «Выражение» этой же строки (по «=Object.«ИМЯ»» — фильтр по ИМЯ и т.д.).",
    "Несколько строк с условиями работают вместе (логическое И).",
    "Если у строки есть условие, но «Заголовок» пуст — строка только фильтрует, столбца в таблице не будет.",
    "«Значение» — выпадушка реальных значений поля (например, для Visibility1 — Тип 1 / Тип 2 …); "
    "можно и вписать своё.",
    "Если «Выражение» — не поле (=Count, =row, текст), Условие и Значение в этой строке неактивны (серые).",
]))
S.append(Paragraph("Группа — группировка и сортировка", h2))
S.append(Paragraph(
    "Выпадушка: <i>пусто / по возрастанию / по убыванию / без сортировки</i>. Непустое значение "
    "означает «группировать по этому столбцу»: одинаковые значения сводятся в одну строку, а "
    "<font name='DejaVu-Mono'>=Count</font> покажет их количество; строки идут в выбранном порядке. "
    "Группа одна на отчёт — выберете в другой строке, с прежней снимется автоматически.", body))

S.append(Paragraph("4. Источник, несколько отчётов, прочее", h1))
S.append(bullets([
    "<b>Источник…</b> — слой, с которого берутся блоки для этой секции.",
    "<b>+ Добавить отчёт</b> — ещё одна секция в той же таблице (свои столбцы, фильтр, группа, источник). "
    "Стрелки ↑ ↓ меняют порядок секций, ✕ удаляет секцию.",
    "<b>Строка ИТОГ (сумма)</b> — добавляет снизу строку с суммой числовых столбцов (подписана «сумма»).",
    "<b>Объединение шапки</b> — выделите 2+ строки-столбца, правый клик → «Объединить шапку выделенных столбцов».",
]))

S.append(rule())
S.append(Paragraph("5. Заготовки шаблонов (что получите сразу)", h1))
S.append(Paragraph("Спецификация", h2))
S.append(Paragraph(
    "№ п/п (=row) · Наименование (=Object.«ИМЯ») · Артикул (=Object.Name) · Длина (=Object.«Длина») · "
    "Колич. (=Count) · Ед. изм. (=«шт.»). Группа — по «Наименование».", small))
S.append(Paragraph("Раскрой", h2))
S.append(Paragraph(
    "Длина (=Object.«Длина») · Колич. (=Count). Группа — по «Длина», заголовок таблицы скрыт. "
    "Если профиль режется со смещением — поправьте выражение длины, например "
    "<font name='DejaVu-Mono'>=Object.«Длина»-20</font> для ригеля; для других профилей задайте своё смещение.", small))
S.append(Paragraph("Заполнения", h2))
S.append(Paragraph(
    "№ · Тип (=Object.«Visibility1») · Марка · Ширина · Высота · Колич. (=Count) · "
    "Площадь, м². Группа — по «Марка», внизу строка суммы. Ширина/Высота берутся из «РАЗМЕР_ЗАП».", small))

S.append(rule())
S.append(Paragraph("Установка обновления", h1))
S.append(steps([
    "Закройте AutoCAD.",
    "Скачайте архив сборки (ATableSpec.bundle.zip) и распакуйте папку ATableSpec.bundle в "
    "%APPDATA%\\Autodesk\\ApplicationPlugins\\, заменив старую.",
    "Запустите AutoCAD — плагин загрузится сам.",
]))
S.append(Paragraph(
    "Команды: <font name='DejaVu-Mono'>ATSPECREPORT</font> — отчёт по формулам (это окно); "
    "<font name='DejaVu-Mono'>ATSPEC</font> — быстрая ведомость; "
    "<font name='DejaVu-Mono'>ATSPECUPDATE</font> — пересчитать таблицу вручную; "
    "<font name='DejaVu-Mono'>ATSPECDUMP</font> — показать точные имена атрибутов/параметров блока "
    "(пригодится, если поле названо иначе, например «Видимость1» вместо «Visibility1»).", small))
S.append(Spacer(1, 4))
S.append(Paragraph(
    "Сборка проверена автосборкой на компиляцию; ядро отчётов — модульными тестами и сквозным "
    "прогоном (спецификация, раскрой со смещением, заполнения с суммой и фильтром). Поведение в живом "
    "AutoCAD (подсказки, ввод выражений, вид таблиц) просьба проверить и прислать замечания.", small))
S.append(Spacer(1, 6))
S.append(Paragraph(
    "Сборка: HEAD 3ead274 · ветка feat/auto-reactor · ATableSpec.bundle.zip ≈ 24,2 МБ · 22.06.2026.", small))

doc = SimpleDocTemplate("ATableSpec_session_2206.pdf", pagesize=A4,
                        leftMargin=20*mm, rightMargin=18*mm,
                        topMargin=16*mm, bottomMargin=16*mm,
                        title="ATableSpec — сессия 22.06: финальный построитель")
doc.build(S)
print("PDF готов: ATableSpec_session_2206.pdf")
