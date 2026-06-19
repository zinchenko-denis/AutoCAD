# -*- coding: utf-8 -*-
"""Гайд по Этапу 1 ATableSpec для конструкторов: что просили / что сделано / как
пользоваться — по каждому пожеланию. Кириллица через DejaVu. reportlab/Platypus.
Запуск: python3 make_stage1_guide.py [выходной.pdf]"""
import sys
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.platypus import (SimpleDocTemplate, Paragraph, Spacer, Table,
                                TableStyle, ListFlowable, ListItem, HRFlowable, KeepTogether)
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
GREEN = colors.HexColor("#e7f1e8")
LINE = colors.HexColor("#c9d3df")
WARN = colors.HexColor("#fff4e5")


def st(name, **kw):
    base = dict(fontName="DejaVu", fontSize=10, leading=14, textColor=colors.black)
    base.update(kw); return ParagraphStyle(name, **base)


title = st("title", fontName="DejaVu-Bold", fontSize=20, leading=24, textColor=NAVY)
sub = st("sub", fontSize=11, leading=15, textColor=GREY)
h1 = st("h1", fontName="DejaVu-Bold", fontSize=13.5, leading=17, textColor=NAVY, spaceBefore=13, spaceAfter=5)
lead = st("lead", fontName="DejaVu-Bold", fontSize=10, leading=13, textColor=colors.black, spaceBefore=3)
body = st("body", spaceAfter=3)
small = st("small", fontSize=9, leading=12, textColor=GREY)
cell = st("cell", fontSize=9, leading=12)
cellm = st("cellm", fontName="DejaVu-Mono", fontSize=8.5, leading=11)


def MONO(s): return f'<font name="DejaVu-Mono">{s}</font>'


def bullets(items, style=body):
    return ListFlowable([ListItem(Paragraph(t, style), leftIndent=6) for t in items],
                        bulletType="bullet", bulletColor=NAVY, leftIndent=14, bulletFontSize=8)


def panel(flowables, bg=LIGHT, pad=6):
    """Обёртка-плашка: один кадр с фоном вокруг набора flowables."""
    inner = Table([[flowables]] if not isinstance(flowables, list) else [[x] for x in flowables],
                  colWidths=[172 * mm])
    inner.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), bg),
        ("LEFTPADDING", (0, 0), (-1, -1), pad), ("RIGHTPADDING", (0, 0), (-1, -1), pad),
        ("TOPPADDING", (0, 0), (-1, -1), pad), ("BOTTOMPADDING", (0, 0), (-1, -1), pad),
        ("LINEBELOW", (0, 0), (-1, -2), 3, colors.white),  # разрядка между строками
        ("BOX", (0, 0), (-1, -1), 0.4, LINE),
    ]))
    return inner


def wish(num, head, asked, done, howto, note=None, note_bg=WARN):
    """Секция «пожелание»: заголовок + Что просили / Что сделано / Как пользоваться [+ примечание]."""
    blk = [Paragraph(f"{num}. {head}", h1),
           Paragraph("Что просили", lead), Paragraph(asked, body),
           Paragraph("Что сделано", lead), Paragraph(done, body),
           Paragraph("Как пользоваться", lead)]
    blk += [howto] if not isinstance(howto, str) else [Paragraph(howto, body)]
    if note:
        blk += [Spacer(1, 3), panel(Paragraph(note, st("n", fontSize=9.5, leading=13,
                                                       textColor=colors.HexColor("#7a3b00"))), bg=note_bg)]
    return blk


OUT = sys.argv[1] if len(sys.argv) > 1 else "/home/claude/ATableSpec_stage1.pdf"
doc = SimpleDocTemplate(OUT, pagesize=A4,
                        leftMargin=19 * mm, rightMargin=19 * mm, topMargin=17 * mm, bottomMargin=15 * mm,
                        title="ATableSpec — Этап 1: что нового и как пользоваться", author="ATableSpec")
S = []

# ── шапка ──
S += [Paragraph("ATableSpec — Этап 1", title),
      Paragraph("Что нового и как этим пользоваться — по каждому вашему пожеланию", sub),
      Spacer(1, 3),
      Paragraph("Доработка построителя отчёта и автопересчёта · обновление от июня 2026", small),
      HRFlowable(width="100%", color=LINE, spaceBefore=8, spaceAfter=8),
      Paragraph("Ниже — все изменения этого этапа: коротко «что просили», «что сделано» и подробно "
                "«как пользоваться». Команды в чертеже: " + MONO("ATSPEC") + " (быстрая ведомость), "
                + MONO("ATSPECREPORT") + " (свой отчёт по формулам), " + MONO("ATSPECUPDATE") + " (пересчёт), "
                + MONO("ATSPECDUMP") + " (диагностика блока). Построитель отчёта открывается командой "
                + MONO("ATSPECREPORT") + " — большинство новых настроек именно там.", body)]

# ── 0. Обновление ──
S += [Paragraph("0. Сначала обновите плагин", h1),
      bullets([
          "Скачайте свежий <b>ATableSpec.bundle.zip</b>: " + MONO("github.com/zinchenko-denis/AutoCAD")
          + " → Releases → <b>latest</b> (ту же ссылку, что и раньше).",
          "Закройте AutoCAD. Распакуйте архив и <b>замените</b> папку " + MONO("ATableSpec.bundle") + " в "
          + MONO("%APPDATA%\\Autodesk\\ApplicationPlugins\\") + " новой.",
          "Запустите AutoCAD — плагин подхватится сам (ручной " + MONO("NETLOAD") + " не нужен). "
          "Новые возможности появятся в окне " + MONO("ATSPECREPORT") + ".",
      ])]

# ── 1 ──
S += wish(1, "Подсказка «Поля для Object» убрана из построителя",
          "Убрать строку-подсказку со списком полей: когда есть выпадающее меню, подсказка лишняя и мешает.",
          "Строка-подсказка удалена. Все поля блока и так доступны в выпадающем списке столбца "
          "«Выражение», поэтому держать их ещё и текстом не нужно.",
          bullets([
              "В построителе (" + MONO("ATSPECREPORT") + ") в колонке <b>«Выражение»</b> кликните по ячейке — "
              "раскроется список: служебные (" + MONO("=row") + ", " + MONO("=Count") + ", " + MONO("=«шт.»")
              + ") и все поля блока как " + MONO("=Object.«…»") + ".",
              "Любую формулу можно ввести и руками — список не ограничивает.",
          ]))

# ── 2 ──
S += wish(2, "Длина доборника и Ширина/Высота заполнений (СТП)",
          "Чтобы " + MONO("=Object.«Длина»") + " у доборника и " + MONO("=Object.«Ширина»") + " / "
          + MONO("=Object.«Высота»") + " у стеклопакета давали значение, а не пустую ячейку.",
          "Два улучшения: (1) поиск поля стал устойчив к <b>регистру и лишним пробелам</b> в имени "
          "параметра — " + MONO("=Object.«Длина»") + " теперь находит динамический параметр «Длина», даже если "
          "он записан как «Длина » или в другом регистре; (2) для заполнений Ширина/Высота, если параметр "
          "пуст, значение берётся из атрибута " + MONO("РАЗМЕР_ЗАП") + " вида «ШиринаХВысота» "
          "(разделитель — русская «Х»).",
          bullets([
              "Пишите как обычно: " + MONO("=Object.«Длина»") + ", " + MONO("=Object.«Ширина»") + ", "
              + MONO("=Object.«Высота»") + ".",
              "Для заполнений достаточно, чтобы был заполнен атрибут " + MONO("РАЗМЕР_ЗАП") + " (напр. «670Х1170»).",
          ]),
          note="Если длина доборника всё равно пустая — это значит, что параметр хранится под "
               "непривычным именем. Наберите " + MONO("ATSPECDUMP") + " (раздел 8), кликните доборник и "
               "пришлите Денису текст из командной строки. По нему точно настроим чтение длины "
               "(вплоть до расчёта по габариту блока).")

# ── 3 ──
S += wish(3, "Нумерация строк — с 1, а не с 0",
          "Чтобы столбец «№ п/п» начинался с 1.",
          "Формула столбца номера по умолчанию теперь " + MONO("=row") + " (даёт 1, 2, 3 …). "
          "Раньше было " + MONO("=row-1") + " (давало 0, 1, 2 …).",
          bullets([
              "В готовом шаблоне столбец «№ п/п» уже " + MONO("=row") + " — ничего делать не нужно.",
              "Если когда-то понадобится нумерация с нуля — поставьте " + MONO("=row-1") + ".",
          ]))

# ── 4 ──
S += wish(4, "Флажки «Скрыть заголовок» и «Скрыть шапку столбцов»",
          "Возможность отключать верхний заголовок таблицы и/или строку с названиями столбцов.",
          "В окне построителя добавлены два флажка. Работают по отдельности и вместе; настройка хранится "
          "в самой таблице и сохраняется при автопересчёте.",
          bullets([
              "Перед «Построить» отметьте нужный флажок.",
              "<b>«Скрыть заголовок»</b> — убирает верхнюю строку-название (напр. «СПЕЦИФИКАЦИЯ ЭЛЕМЕНТОВ»).",
              "<b>«Скрыть шапку столбцов»</b> — убирает строку с названиями колонок (№, Наименование, …).",
              "Можно скрыть и то, и другое — тогда останутся только строки данных.",
          ]))

# ── 5 ──
S += wish(5, "Объединение ячеек шапки (Вариант А)",
          "Объединять шапку нескольких столбцов прямо в построителе: выделить столбцы → ПКМ → объединить. "
          "В таблице сливается строка-шапка, данные остаются раздельными; объединение сохраняется и "
          "переживает пересчёт; нужна и «разъединить».",
          "В построителе по таблице столбцов работает правый клик: «Объединить шапку выделенных столбцов» "
          "и «Разъединить шапку». В итоговой таблице объединяется <b>только строка-шапка</b>, строки данных "
          "не трогаются. Объединения хранятся в таблице и применяются при каждом пересчёте.",
          bullets([
              "В построителе столбцы будущей таблицы — это <b>строки слева</b> (одна строка = один столбец).",
              "Выделите 2+ строки: клик по серой ячейке-номеру слева, с <b>Ctrl</b>/<b>Shift</b> — несколько.",
              "ПКМ → <b>«Объединить шапку выделенных столбцов»</b>. Под таблицей появится строка-контроль "
              "вида «Объединение шапки: столбцы 2–4».",
              "Текст объединённой ячейки берётся из заголовка <b>левого</b> столбца диапазона — туда впишите "
              "общий заголовок.",
              "Снять объединение: выделите те же столбцы → ПКМ → <b>«Разъединить шапку»</b>.",
          ]),
          note="Объединение работает по строке шапки. Если шапка скрыта флажком (п. 4) — объединять нечего. "
               "Объединяется диапазон по краям выделения (если выделили столбцы 2 и 4, сольются 2–4).",
          note_bg=LIGHT)

# ── 6 ──
S += wish(6, "Масштаб таблицы при вставке",
          "Таблица вставляется слишком мелко. Сделать масштаб настраиваемым, по умолчанию 1000.",
          "В окне построителя добавлено поле <b>«Масштаб»</b> со значением <b>1000</b> по умолчанию. На это "
          "число умножается размер таблицы (текст, строки, столбцы). Масштаб хранится в таблице и "
          "не накапливается при повторных пересчётах.",
          bullets([
              "Перед «Построить» задайте «Масштаб» (по умолчанию 1000).",
              "Если таблица вышла слишком крупной или мелкой — постройте заново с другим числом "
              "(напр. 500 или 2000).",
              "Старые таблицы, построенные до обновления, остаются в прежнем размере.",
          ]),
          note="1000 — это намеренно крупная таблица (ваше число из пожелания). Под конкретный чертёж "
               "подберите значение поля «Масштаб».")

# ── 7 ──
S += wish(7, "Пустая строка снизу после пересчёта — убрана",
          "После пересчёта (удалили блок, строк стало меньше) внизу таблицы оставалась лишняя пустая строка.",
          "При пересчёте таблица приводится к точному числу строк — лишние пустые снизу удаляются.",
          bullets([
              "Ничего делать не нужно: удалите блок — таблица пересчитается сама (или " + MONO("ATSPECUPDATE")
              + "), хвостовая пустая строка исчезнет.",
          ]),
          note="Просьба проверить на реальном удалении блока, что пустая строка действительно пропадает: "
               "правка сделана под наиболее вероятную причину, и живая проверка это подтвердит.")

# ── 8. Диагностика ──
S += [Paragraph("8. Диагностика: команда ATSPECDUMP", h1),
      Paragraph("Зачем", lead),
      Paragraph("Помогает увидеть, под каким именем и с каким значением блок хранит свои параметры — "
                "особенно если формула " + MONO("=Object.«…»") + " даёт пустую ячейку (например, длина доборника).", body),
      Paragraph("Как пользоваться", lead),
      bullets([
          "Наберите " + MONO("ATSPECDUMP") + " → Enter, выберите блок(и) → Enter.",
          "Откройте текстовое окно команд (клавиша <b>F2</b>). По каждому блоку будет напечатано: слой, имя, "
          "поворот, габарит (Ш×В), список <b>атрибутов</b> и список <b>динамических параметров</b> с точными "
          "именами (в кавычках «», чтобы видно было пробелы), значениями и типами.",
          "Скопируйте этот текст и пришлите Денису — этого достаточно, чтобы донастроить чтение нужного поля.",
      ])]

# ── памятка: выражения ──
S += [Paragraph("9. Памятка по выражениям (в столбце «Выражение»)", h1)]
rows = [
    [Paragraph("Выражение", cell), Paragraph("Что делает", cell)],
    [Paragraph(MONO("=Object.«ИМЯ»"), cellm), Paragraph("значение атрибута блока (например, марка)", cell)],
    [Paragraph(MONO("=Object.Name"), cellm), Paragraph("имя блока (у этих блоков совпадает с профилем/артикулом)", cell)],
    [Paragraph(MONO("=Object.«Длина»"), cellm), Paragraph("длина блока (атрибут или динамический параметр)", cell)],
    [Paragraph(MONO("=Object.«Ширина»") + " / " + MONO("«Высота»"), cellm),
     Paragraph("размер заполнения (из параметра или из РАЗМЕР_ЗАП «ШхВ»)", cell)],
    [Paragraph(MONO("=Object.«Длина»-150"), cellm), Paragraph("арифметика над полем: укоротить/удлинить (любое число)", cell)],
    [Paragraph(MONO("=«01 02 04»"), cellm), Paragraph("литерал — например, свой артикул", cell)],
    [Paragraph(MONO('="Опора "+Object.«ИМЯ»'), cellm), Paragraph("конкатенация строки и поля", cell)],
    [Paragraph(MONO("=Count"), cellm), Paragraph("количество элементов в группе", cell)],
    [Paragraph(MONO("=Iff(усл.; A; B)"), cellm), Paragraph("условие: A если истина, иначе B", cell)],
    [Paragraph(MONO("=row"), cellm), Paragraph("порядковый номер строки (с 1)", cell)],
]
t = Table(rows, colWidths=[60 * mm, 112 * mm])
t.setStyle(TableStyle([
    ("BACKGROUND", (0, 0), (-1, 0), NAVY),
    ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
    ("FONTNAME", (0, 0), (-1, 0), "DejaVu-Bold"),
    ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, LIGHT]),
    ("GRID", (0, 0), (-1, -1), 0.4, LINE),
    ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
    ("LEFTPADDING", (0, 0), (-1, -1), 5), ("RIGHTPADDING", (0, 0), (-1, -1), 5),
    ("TOPPADDING", (0, 0), (-1, -1), 3), ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
]))
S += [t]

# ── напоминание про автопересчёт ──
S += [Paragraph("10. Про автопересчёт (напоминание)", h1),
      bullets([
          "Построенная таблица «живая»: определение отчёта (формулы, источник, группировка, флажки, "
          "масштаб, объединения шапки) хранится в самой таблице.",
          "Изменили блок — длину, марку, добавили/удалили элемент — таблица обновляется <b>сама</b> по "
          "завершении команды.",
          "Нужно пересчитать немедленно/принудительно — " + MONO("ATSPECUPDATE") + " (выбрать таблицы или "
          "Enter без выбора — пересчитаются все).",
          "Готовую таблицу можно скопировать в другой чертёж с такими же блоками и пересчитать там.",
      ])]

# ── что проверить / обратная связь ──
S += [HRFlowable(width="100%", color=LINE, spaceBefore=12, spaceAfter=6),
      Paragraph("Что особенно просим проверить и сообщить", h1),
      bullets([
          "Заполнения: " + MONO("=Object.«Ширина»") + " / " + MONO("«Высота»") + " дают размеры на реальном слое.",
          "Доборник: " + MONO("=Object.«Длина»") + " даёт длину; если пусто — " + MONO("ATSPECDUMP")
          + " по доборнику и текст из командной строки Денису.",
          "Масштаб: подходит ли 1000, или подобрать другое.",
          "Флажки скрытия: прячут заголовок/шапку по отдельности и вместе; сохраняются после пересчёта.",
          "Объединение шапки: сливается как нужно и переживает правку блока.",
          "Пустая строка снизу: исчезает после удаления блока.",
      ], style=small),
      Spacer(1, 4),
      Paragraph("Сборка: " + MONO("github.com/zinchenko-denis/AutoCAD") + " → Releases → latest. "
                "Вопросы и замечания — Денису.", small)]

doc.build(S)
print("PDF готов:", OUT)
