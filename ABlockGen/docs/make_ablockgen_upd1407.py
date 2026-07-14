#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ABlockGen: апдейт 14.07 по фидбэку Алексея — фикс поворота ригелей + размеры.
import os
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib import colors
from reportlab.platypus import SimpleDocTemplate, Paragraph
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'ABlockGen_upd_1407.pdf')

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
                      backColor=colors.HexColor('#fff6df'), borderPadding=5,
                      spaceBefore=4, spaceAfter=6)

S = []
S.append(Paragraph('ABlockGen — обновление 14.07: по вашим замечаниям', title))
S.append(Paragraph('Поворот ригелей исправлен · размеры строятся сами (габаритные + межосевые)', sub))

S.append(Paragraph('1. Установка обновления', h1))
S.append(Paragraph('Закрыть AutoCAD. Скачать свежий пакет (ссылка одной строкой):', body))
S.append(Paragraph('https://github.com/zinchenko-denis/AutoCAD/releases/download/latest/ABlockGen.bundle.zip', mono))
S.append(Paragraph('Распаковать С ЗАМЕНОЙ в ту же папку:', body))
S.append(Paragraph('%APPDATA%\\Autodesk\\ApplicationPlugins\\', mono))

S.append(Paragraph('2. Поворот ригелей — исправлен', h1))
S.append(Paragraph('Причина найдена: программа жёстко ставила ригелям поворот 270° — как у '
                   'вертикально нарисованных блоков-ригелей из другого проекта. Ваш обычный '
                   'ригель нарисован горизонтально, ему нужен 0°; а вот верхний (из профиля '
                   'стойки, Р31/Р33) — как раз 270°. Это видно и в вашей зелёной конструкции.', body))
S.append(Paragraph('Теперь программа НЕ угадывает: ATVITRAGEAR берёт поворот С УКАЗАННОГО '
                   'ОБРАЗЦА (какой поворот у кликнутого блока — такой и у всех вставляемых), '
                   'ATVITRAGE — «как принято в чертеже» (самый частый поворот существующих '
                   'вхождений выбранного блока). Ничего вручную вводить не нужно.', body))
S.append(Paragraph('Проверка: тот же сценарий на «Проба 3» — ригели должны лечь БЕЗ ручного '
                   'поворота, один в один с вашей конструкцией (включая верхний ряд, если '
                   'указан его образец).', note))

S.append(Paragraph('3. Размеры строятся сами', h1))
S.append(Paragraph('После построения каркаса обе команды сразу ставят размеры (как вы '
                   'предложили): ', body))
S.append(Paragraph('— внизу: цепочка МЕЖОСЕВЫХ по стойкам (445 | 1710 | 445) и ниже — '
                   'ГАБАРИТ ширины по телам крайних стоек;', num))
S.append(Paragraph('— справа: цепочка по ригелям СО СТАРТОМ ОТ ГАБАРИТА — от низа стойки '
                   'по осям ригелей до верха (300 | 2400 | 900 | 300 | 285 на «Проба 3») '
                   'и правее — габарит высоты;', num))
S.append(Paragraph('— размеры ложатся на слой «Размеры» текущим размерным стилем чертежа; '
                   'в цепочку попадают только реально поставленные ригели (порог под '
                   'дверью в цепочке не появляется);', num))
S.append(Paragraph('— всё это часть одного действия: Ctrl+Z убирает каркас вместе с размерами.', num))
S.append(Paragraph('Если вид размеров (стиль/отступы) захочется другой — скажите, что '
                   'поправить: сейчас отступы 400 и 800 мм от конструкции, стиль — текущий.', note))

S.append(Paragraph('4. Про АР других архитекторов', h1))
S.append(Paragraph('Присылайте «импровизации» — по каждому файлу разберём, что программа '
                   'видит, и научим распознавание. Чем разнообразнее образцы, тем быстрее '
                   'она перестанет требовать «натаскивания».', body))

doc = SimpleDocTemplate(OUT, pagesize=A4,
                        leftMargin=17 * mm, rightMargin=15 * mm,
                        topMargin=14 * mm, bottomMargin=14 * mm,
                        title='ABlockGen — обновление 14.07', author='ABlockGen')
doc.build(S)
print('OK:', OUT)
