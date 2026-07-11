#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# Инструкция для проектировщиков по обновлению 11.07 (по замечаниям Алексея с КМД):
# чистый построитель штапиков (фильтры ШТ_СТЫК скрыты), терморазрыв-секции сеются
# только когда терморазрыв есть на чертеже, ▼-список полей у «Выражения».
import os
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib import colors
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, KeepTogether
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.environ.get('SESSION_OUT', os.path.join(HERE, 'ATableSpec_session_1107.pdf'))

FD = '/usr/share/fonts/truetype/dejavu/'
pdfmetrics.registerFont(TTFont('DV', FD + 'DejaVuSans.ttf'))
pdfmetrics.registerFont(TTFont('DVB', FD + 'DejaVuSans-Bold.ttf'))
pdfmetrics.registerFont(TTFont('DVM', FD + 'DejaVuSansMono.ttf'))

title = ParagraphStyle('t', fontName='DVB', fontSize=15, leading=19, spaceAfter=2)
sub = ParagraphStyle('s', fontName='DV', fontSize=9, leading=12,
                     textColor=colors.HexColor('#555555'), spaceAfter=10)
h1 = ParagraphStyle('h1', fontName='DVB', fontSize=12.5, leading=16, spaceBefore=13, spaceAfter=5)
body = ParagraphStyle('b', fontName='DV', fontSize=9.5, leading=13.4, spaceAfter=4)
num = ParagraphStyle('num', fontName='DV', fontSize=9.5, leading=13.4,
                     leftIndent=7 * mm, spaceAfter=2)
mono = ParagraphStyle('m', fontName='DVM', fontSize=8.8, leading=12,
                      backColor=colors.HexColor('#f2f2f2'), borderPadding=4,
                      leftIndent=2 * mm, spaceBefore=2, spaceAfter=6)
note = ParagraphStyle('n', fontName='DV', fontSize=9, leading=12.6,
                      textColor=colors.HexColor('#333333'),
                      backColor=colors.HexColor('#fff6df'), borderPadding=5,
                      spaceBefore=4, spaceAfter=6)

S = []

S.append(Paragraph('ATableSpec — обновление 11.07: по вашим замечаниям от 11 июля', title))
S.append(Paragraph('Чистый построитель штапиков · лишние отчёты терморазрыва не создаются · '
                   'выпадающий список полей у «Выражения»', sub))

# ── 1. Установка ──
S.append(Paragraph('1. Установка обновления', h1))
S.append(Paragraph('Закрыть AutoCAD. Скачать свежий пакет (ссылка одной строкой, вокруг неё ничего '
                   'не дописывать):', body))
S.append(Paragraph('https://github.com/zinchenko-denis/AutoCAD/releases/download/latest/ATableSpec.bundle.zip', mono))
S.append(Paragraph('Если ссылка не открывается — страница релизов, там тот же файл:', body))
S.append(Paragraph('https://github.com/zinchenko-denis/AutoCAD/releases', mono))
S.append(Paragraph('Распаковать архив с заменой в папку плагинов (папка '
                   '<b>ATableSpec.bundle</b> должна лечь целиком):', body))
S.append(Paragraph('%APPDATA%\\Autodesk\\ApplicationPlugins\\', mono))
S.append(Paragraph('Запустить AutoCAD — команды и кнопки прежние, перенастраивать ничего не нужно. '
                   'Уже вставленные таблицы продолжают жить и пересчитываться.', body))

# ── 2. Чистый построитель штапиков ──
S.append(KeepTogether([
    Paragraph('2. Спецификация штапиков: служебные строки убраны из построителя', h1),
    Paragraph('Строки вида «=Object.«ШТ_СТЫК» = 0», которые шаблон «Штапики» показывал в таблице '
              'условий каждого отчёта («служебная информация… не понятно что это и для чего»), '
              'из таблицы построителя убраны.', body),
    Paragraph('Само условие продолжает работать: проверить его можно в строке-сводке под таблицей — '
              '«Фильтр: ШТ_СТЫК = 0». Разделение «в зонах без терморазрыва / с терморазрывом» '
              'считается как раньше, таблица не меняется.', body),
    Paragraph('Свои фильтры добавляются как обычно — строками «Выражение | Условие | Значение».', note)]))

# ── 3. Терморазрыв-отчёты только когда есть терморазрыв ──
S.append(KeepTogether([
    Paragraph('3. Отчёты 3 и 4 («…с терморазрывом») создаются только когда терморазрыв есть', h1),
    Paragraph('Программа теперь сама смотрит по геометрии чертежа (по выбранным «Стойкам»), есть ли '
              'стыки стоек — те самые терморазрывы:', body),
    Paragraph('— на витраже <b>без</b> терморазрыва шаблон «Штапики» создаёт только 2 отчёта '
              '(«…в зонах без терморазрыва») — лишних карточек больше нет;', num),
    Paragraph('— на КМД <b>с</b> терморазрывом — все 4 отчёта, как раньше;', num),
    Paragraph('— «Взять с табл.» в раскрое так же пропускает пустые терморазрыв-отчёты, даже если '
              'исходная спецификация построена старой версией и несёт все 4 секции (в конце будет '
              'заметка «терморазрыва на чертеже нет — пропущена»).', num),
    Paragraph('Нюанс: проверка делается в момент выбора шаблона по слою из поля «Стойки» (обычно '
              'RF-стойки подставляется сам). Если поменяли «Стойки» уже после — просто выберите '
              'шаблон «Штапики» заново. Если программа не уверена (нет стоек и т.п.), она создаёт '
              'все 4 отчёта, как раньше — пустые секции в таблицу всё равно не печатаются.', note)]))

# ── 4. Выпадающий список полей ──
S.append(KeepTogether([
    Paragraph('4. Список полей у «Выражения» — теперь кнопкой ▼', h1),
    Paragraph('Подсказка, которая раньше появлялась только при наборе «=Object.«…», теперь доступна '
              'сразу: у правого края ячейки «Выражение» появился значок ▼ — клик открывает список '
              '(=row, =Count, все поля блоков источника: =Object.«Ширина», =Object.«ШТ_РАЗМЕР» и '
              'т.д.), выбранное вписывается в строку.', body),
    Paragraph('Набор руками с подсказкой тоже работает, как вы и нашли. Список зависит от '
              'выбранного «Источника» отчёта.', note)]))

# ── 5. Быстрая проверка ──
S.append(KeepTogether([
    Paragraph('5. Как быстро проверить обновление', h1),
    Paragraph('1) Шаблон «Штапики» на витраже без терморазрыва → в построителе 2 отчёта, строк '
              'ШТ_СТЫК в таблицах условий нет, «Фильтр: ШТ_СТЫК = 0» виден в сводке под таблицей.', num),
    Paragraph('2) Тот же шаблон на КМД с терморазрывом → 4 отчёта, как раньше.', num),
    Paragraph('3) «Взять с табл.» из готовой спецификации штапиков (на чертеже без терморазрыва) → '
              'отчёты 3 и 4 не появляются, суммы количеств сходятся со спецификацией.', num),
    Paragraph('4) В любой ячейке «Выражение» — значок ▼ справа, клик открывает список полей.', num)]))

doc = SimpleDocTemplate(OUT, pagesize=A4,
                        leftMargin=16 * mm, rightMargin=16 * mm,
                        topMargin=14 * mm, bottomMargin=14 * mm,
                        title='ATableSpec — обновление 11.07')
doc.build(S)
print('OK:', OUT)
