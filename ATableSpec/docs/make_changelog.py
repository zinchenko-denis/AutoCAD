# -*- coding: utf-8 -*-
"""PDF «Что нового в ATableSpec» — изменения с редакции 1.4 руководства.
Тот же стиль/шрифты, что и make_manual.py (кириллица через DejaVu)."""
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

title = st("title", fontName="DejaVu-Bold", fontSize=20, leading=24, textColor=NAVY)
sub   = st("sub", fontSize=11, leading=15, textColor=GREY)
h1    = st("h1", fontName="DejaVu-Bold", fontSize=14, leading=18, textColor=NAVY,
          spaceBefore=14, spaceAfter=6)
h2    = st("h2", fontName="DejaVu-Bold", fontSize=11.5, leading=15, textColor=colors.black,
          spaceBefore=8, spaceAfter=3)
body  = st("body", spaceAfter=4)
small = st("small", fontSize=9, leading=12, textColor=GREY)
cell  = st("cell", fontSize=9, leading=12)
cellm = st("cellm", fontName="DejaVu-Mono", fontSize=8.5, leading=11)
note  = st("note", fontSize=9.5, leading=13, textColor=colors.HexColor("#7a3b00"))
why   = st("why", fontSize=9.5, leading=13, textColor=GREY, spaceAfter=6, leftIndent=14)

def MONO(s): return f'<font name="DejaVu-Mono">{s}</font>'

doc = SimpleDocTemplate("/home/claude/ATableSpec_changelog.pdf", pagesize=A4,
                        leftMargin=20*mm, rightMargin=18*mm, topMargin=18*mm, bottomMargin=16*mm,
                        title="ATableSpec — что нового", author="ATableSpec")
S = []

def bullets(items, style=body):
    return ListFlowable([ListItem(Paragraph(t, style), leftIndent=6) for t in items],
                        bulletType="bullet", bulletColor=NAVY, leftIndent=14, bulletFontSize=8)

def WHY(t):
    return Paragraph("<b>Зачем:</b> " + t, why)

# ── шапка ──
S += [Paragraph("ATableSpec — что нового", title),
      Paragraph("Изменения с прошлого руководства (редакция 1.4)", sub),
      Spacer(1, 4),
      Paragraph("Перечень доработок по фасадным КМД-чертежам · июнь 2026 · доступно в релизе latest", small),
      HRFlowable(width="100%", color=LINE, spaceBefore=8, spaceAfter=10),
      Paragraph("Ниже — всё, что добавлено и исправлено с момента последнего PDF-руководства. "
                "Прежнее поведение (команды " + MONO("ATSPEC") + ", " + MONO("ATSPECREPORT") + ", "
                + MONO("ATSPECUPDATE") + ", ядро выражений, авто-пересчёт) сохранено; перечисленное "
                "его расширяет. Команды не изменились.", body)]

# ── 1. Построитель: несколько секций ──
S += [Paragraph("1. Построитель отчёта: несколько секций в одной таблице", h1),
      bullets([
          "Окно " + MONO("ATSPECREPORT") + " переработано: отчёт — это одна или <b>несколько секций</b>. "
          "У каждой секции свои столбцы, свой источник (слой), свой фильтр и своя группировка/сортировка. "
          "Кнопка <b>«+ Добавить отчёт»</b> добавляет ещё одну секцию, стрелки ↑/↓ и ✕ — порядок и удаление.",
          "Источник, Фильтр и Группировка вынесены в отдельные <b>всплывающие окна</b> (кнопки на карточке "
          "секции) — само окно стало компактным, в гриде остаются только столбцы (Заголовок | Выражение).",
          "<b>Объединение шапки столбцов</b>: выделить столбцы (строки в гриде слева) → ПКМ → "
          "«Объединить шапку выделенных столбцов»; «Разъединить шапку» — снять. Для общего заголовка "
          "над группой колонок.",
          "В гриде столбцов теперь можно <b>удалять строки</b>: клавиша Delete (вне режима правки ячейки) "
          "или ПКМ → «Удалить строку». Раньше строка не убиралась — оставалась пустой.",
          "Введённое вручную <b>выражение сразу видно в ячейке</b> и запоминается в выпадающем списке "
          "для повторного выбора (раньше его приходилось открывать заново, чтобы увидеть).",
      ]),
      WHY("делать спецификацию на стойки И ригеля (и любые другие группы) в ОДНОЙ таблице с раздельными "
          "шапками — как в СПДС; и свободно править набор столбцов прямо в окне.")]

# ── 2. Выбор шаблона ──
S += [Paragraph("2. Выбор шаблона при запуске", h1),
      Paragraph("Перед построителем открывается окно выбора шаблона:", body),
      bullets([
          "<b>Ручное</b> — пустой отчёт, все столбцы задаёте сами.",
          "<b>Спецификация</b> — №, наименование, артикул, длина, кол-во, ед. изм.",
          "<b>Раскрой</b> — профиль / длина / кол-во (черновик, дорабатывается по примерам).",
          "<b>Заполнения</b> — заявка на заполнения (см. п. 3).",
      ]),
      Paragraph("Выбранный шаблон <b>засевает</b> готовые столбцы выражениями — всё остаётся "
                "редактируемым (столбцы, источник, фильтр, группа, число секций).", body),
      WHY("не набирать типовые столбцы вручную при каждом построении — выбрал шаблон и правишь под задачу.")]

# ── 3. Шаблон «Заполнения» ──
S += [Paragraph("3. Шаблон «Заполнения» — доведён до вида заявки", h1),
      Paragraph("Шаблон «Заполнения» теперь формирует таблицу со столбцами:", body)]

rows = [
    [Paragraph("Столбец", cell), Paragraph("Откуда / формула", cell)],
    [Paragraph("№ п/п", cell), Paragraph("автонумерация", cell)],
    [Paragraph("Тип заполнения", cell), Paragraph("параметр видимости блока " + MONO("Visibility1") + " (выбирается из списка)", cell)],
    [Paragraph("Марка", cell), Paragraph("атрибут " + MONO("МАРКИРОВКА"), cell)],
    [Paragraph("Ширина, мм", cell), Paragraph("из " + MONO("РАЗМЕР_ЗАП") + " («ШиринаХВысота») автоматически", cell)],
    [Paragraph("Высота, мм", cell), Paragraph("из " + MONO("РАЗМЕР_ЗАП") + " автоматически", cell)],
    [Paragraph("Колич.", cell), Paragraph("количество в группе (по марке)", cell)],
    [Paragraph("Площадь, м²", cell), Paragraph("Ширина · Высота · Кол-во / 10⁶ — 2 знака, запятая", cell)],
]
t = Table(rows, colWidths=[40*mm, 127*mm])
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
      bullets([
          "Внизу — <b>строка ИТОГ</b>: автоматически суммируются количество и площадь "
          "(под № / шириной / высотой — пусто).",
          "Площадь округляется до 2 знаков с запятой, как в СПДС/Excel (округление половин вверх): "
          "напр. 0,62 · 4,28 · 0,57 · 5,33, итог 10,8 м².",
          "При выборе шаблона «Заполнения» <b>источник сразу ставится</b> на слой " + MONO("RF-заполнения") + ".",
          "Тип заполнения берётся из параметра видимости блока. Если в конкретном чертеже параметр назван "
          "иначе (например, «Видимость1») — выберите его из выпадающего списка выражений столбца.",
      ]),
      WHY("получать заявку на заполнение прозрачных зон прямо из блоков — с площадями по каждой марке и "
          "общим итогом, без ручного счёта.")]

# ── 4. Масштаб ──
S += [Paragraph("4. Масштаб таблицы", h1),
      bullets([
          "В окне построителя — поле <b>«Масштаб»</b>: множитель размеров итоговой таблицы "
          "(высота текста, строки, столбцы).",
          "Заданный масштаб <b>запоминается</b> — новые таблицы создаются с последним применённым.",
      ]),
      WHY("подогнать таблицу под масштаб листа один раз — дальше она создаётся уже нужного размера.")]

# ── 5. Ширины столбцов ──
S += [Paragraph("5. Ширины столбцов — по содержимому", h1),
      Paragraph("Столбцы итоговой таблицы получают ширину <b>по содержимому</b> (№ — узкий, "
                "наименование/площадь — шире), как в таблицах СПДС, вместо одинаковой ширины у всех. "
                "Ручную правку ширины пересчёт сохраняет.", body),
      WHY("таблица сразу выглядит аккуратно и читаемо, без ручной подгонки ширины каждого столбца.")]

# ── 6. Бережный пересчёт ──
S += [Paragraph("6. «Живые» таблицы: бережный пересчёт", h1),
      bullets([
          "При правке блоков таблица пересчитывается, <b>сохраняя оформление и ручные правки</b> "
          "(объединения ячеек, ширины столбцов) — меняются только значения.",
          "Реактор читает <b>динамические параметры</b> блоков (длина доборника, длина створки и т.п.), "
          "а не только атрибуты — при пересчёте длины больше не обнуляются.",
          "Числовые длины и количества приводятся к <b>целым</b> (дробная часть от ручек-параметров "
          "отбрасывается с округлением).",
      ]),
      WHY("пересчёт по правке блока не ломает уже оформленную таблицу и корректно тянет длины из ручек блоков.")]

# ── что дальше ──
S += [Paragraph("Что дальше", h1),
      bullets([
          "Двухсекционная таблица в окне: ручные строки («Данные») и автоген («Отчёт») вместе.",
          "«Пипетка» для фильтра — взять значение условия прямо с объекта чертежа.",
          "Доводка шаблона «Раскрой» по реальным примерам раскроя.",
          "Полевая проверка на боевых КМД-файлах.",
      ]),
      HRFlowable(width="100%", color=LINE, spaceBefore=12, spaceAfter=6),
      Paragraph("Команды (без изменений): " + MONO("ATSPEC") + " — быстрая ведомость; "
                + MONO("ATSPECREPORT") + " — свой отчёт по формулам; " + MONO("ATSPECUPDATE") +
                " — пересчитать отчётные таблицы; " + MONO("ATSPECDUMP") + " — диагностика имён "
                "атрибутов/параметров блока. Установка/обновление — заменить папку "
                + MONO("ATableSpec.bundle") + " новой из релиза. Репозиторий: "
                + MONO("github.com/zinchenko-denis/AutoCAD") + ".", small)]

doc.build(S)
print("changelog PDF готов")
