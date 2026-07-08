#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# Инструкция для проектировщиков по обновлению 08.07:
# установка, штапики (фикс), «Стойки» по контексту, многослойные фильтры «через ;».
import os
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib import colors
from reportlab.platypus import (SimpleDocTemplate, Paragraph, Spacer, Table,
                                TableStyle, KeepTogether)
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.environ.get('SESSION_OUT', os.path.join(HERE, 'ATableSpec_session_0807.pdf'))

FD = '/usr/share/fonts/truetype/dejavu/'
pdfmetrics.registerFont(TTFont('DV', FD + 'DejaVuSans.ttf'))
pdfmetrics.registerFont(TTFont('DVB', FD + 'DejaVuSans-Bold.ttf'))
pdfmetrics.registerFont(TTFont('DVM', FD + 'DejaVuSansMono.ttf'))

title = ParagraphStyle('t', fontName='DVB', fontSize=15, leading=19, spaceAfter=2)
sub = ParagraphStyle('s', fontName='DV', fontSize=9, leading=12,
                     textColor=colors.HexColor('#555555'), spaceAfter=10)
h1 = ParagraphStyle('h1', fontName='DVB', fontSize=12.5, leading=16, spaceBefore=13, spaceAfter=5)
h2 = ParagraphStyle('h2', fontName='DVB', fontSize=10.5, leading=14, spaceBefore=8, spaceAfter=3)
body = ParagraphStyle('b', fontName='DV', fontSize=9.5, leading=13.4, spaceAfter=4)
li = ParagraphStyle('li', fontName='DV', fontSize=9.5, leading=13.4,
                    leftIndent=7 * mm, bulletIndent=2.5 * mm, spaceAfter=2)
num = ParagraphStyle('num', fontName='DV', fontSize=9.5, leading=13.4,
                     leftIndent=7 * mm, spaceAfter=2)
mono = ParagraphStyle('m', fontName='DVM', fontSize=8.8, leading=12,
                      backColor=colors.HexColor('#f2f2f2'), borderPadding=4,
                      leftIndent=2 * mm, spaceBefore=2, spaceAfter=6)
note = ParagraphStyle('n', fontName='DV', fontSize=9, leading=12.6,
                      textColor=colors.HexColor('#333333'),
                      backColor=colors.HexColor('#fff6df'), borderPadding=5,
                      spaceBefore=4, spaceAfter=6)

M = lambda t: '<font face="DVM" size="8.8">' + t + '</font>'
B = lambda t: '<b>' + t + '</b>'
S = []

S.append(Paragraph('ATableSpec — обновление 08.07: установка и нововведения', title))
S.append(Paragraph('Инструкция для проектировщиков · штапики · поле «Стойки» · фильтры «через ;»', sub))

# ── 1. Установка ──
S.append(Paragraph('1. Установка обновления (важно выполнить точно)', h1))
S.append(Paragraph('Ссылка на свежий пакет — одной строкой, вокруг неё ничего не дописывать:', body))
S.append(Paragraph('https://github.com/zinchenko-denis/AutoCAD/releases/download/latest/ATableSpec.bundle.zip', mono))
S.append(Paragraph('Если ссылка не открывается — зайдите на страницу '
                   + M('https://github.com/zinchenko-denis/AutoCAD/releases')
                   + ' и скачайте ' + M('ATableSpec.bundle.zip') + ' из релиза «latest» вручную.', body))
for i, t in enumerate([
    'Закройте AutoCAD.',
    'ПКМ по скачанному zip → «Свойства» → поставьте галку ' + B('«Разблокировать»') + ' → ОК. '
    'Без этого Windows блокирует библиотеки и плагин не загрузится.',
    'Удалите старую папку ' + M('ATableSpec.bundle') + ' из '
    + M('%APPDATA%\\Autodesk\\ApplicationPlugins\\') + '.',
    'Распакуйте новый zip туда же. Внутри должна лежать папка ' + M('ATableSpec.bundle')
    + ' — ' + B('без «папки в папке»') + '.',
    'Запустите AutoCAD — плагин подхватится сам.'], 1):
    S.append(Paragraph(B(str(i) + '.') + ' ' + t, num))
S.append(Paragraph(B('Как убедиться, что версия свежая: ') +
                   'в построителе (' + M('ATSPECREPORT') + ') выберите в «Заголовке» шаблон «Штапики» — '
                   'появится вопрос «Заменить все секции заготовкой шаблона?». Если вопроса нет и ничего '
                   'не меняется — установилась старая версия, повторите шаги 2–4.', note))

# ── 2. Штапики ──
S.append(Paragraph('2. Спецификация штапиков — теперь работает из шаблона', h1))
S.append(Paragraph('Выбор шаблона «Штапики» в предыдущих версиях не срабатывал (ошибка исправлена). '
                   'Рабочий сценарий:', body))
for i, t in enumerate([
    M('ATSPECREPORT') + ' → рамкой выделить ' + B('весь витраж, включая стойки')
    + ' (без стоек программа не увидит стыки).',
    'В «Заголовке» выбрать ' + B('«Штапики»') + ' → на вопрос о замене секций — «Да».',
    'Появятся 4 отчёта («Отчёт 1 из 4» … «Отчёт 4 из 4», ниже по прокрутке): горизонтальные и '
    'вертикальные штапики, отдельно для зон без терморазрыва и с ним. Поле «Стойки: RF-стойки» '
    'заполняется само.',
    'Нажать «Построить» → указать точку вставки.'], 1):
    S.append(Paragraph(B(str(i) + '.') + ' ' + t, num))
S.append(Paragraph('Вертикальные штапики напротив стыка стоек программа сама режет на две части — '
                   'размеры считаются как перехлёст заполнения с нижней и верхней стойкой '
                   '(примечание «м/э зона»). Плановые правки под профиль (например '
                   + M('+20') + ' к горизонтальным, ' + M('−1') + ' к вертикальным) дописываются '
                   'прямо в выражение Длины: ' + M('=Object.«Ширина»+20') + '. «Артикул» — '
                   'заглушка, замените на свой.', body))

# ── 3. Стойки ──
S.append(Paragraph('3. Поле «Стойки» больше не мешает', h1))
S.append(Paragraph('Поле «Стойки:» появляется ' + B('только') + ' в шаблоне «Штапики» (и при правке '
                   'готовой штапиковой таблицы через ' + M('ATSPECEDIT') + '). В спецификациях, '
                   'раскрое и заполнениях его нет — оно там не участвует. Значение подставляется '
                   'само («RF-стойки»); если такого слоя в чертеже нет — выберите свой слой стоек '
                   'из списка.', body))

# ── 4. Фильтры ──
S.append(Paragraph('4. Фильтры: несколько значений и несколько слоёв', h1))
S.append(Paragraph(B('Всё новое правило: строки условий — «И»; несколько вариантов внутри «Значения» '
                     'пишутся через «;» — это «ИЛИ».'), body))
S.append(Paragraph('Для «=» и «содержит» список значит «любое из». '
                   'Для «≠» и «не содержит» — «ни одно из» (читается как «кроме этих»). '
                   'Для «&gt;», «&lt;», «≥», «≤» список не допускается — программа подскажет.', body))

S.append(Paragraph('Источник из нескольких слоёв', h2))
S.append(Paragraph('В поле «Источник» можно перечислить слои через «;» — вручную или кнопкой '
                   + B('«…»') + ' рядом с пипеткой (окно со списком слоёв и галочками). '
                   'Номера по порядку, группировка и ИТОГ работают по всем слоям сразу.', body))

S.append(Paragraph('Примеры «хочу → как настроить»', h2))
tbl = Table([
    [Paragraph(B('Хочу'), body), Paragraph(B('Как настроить'), body)],
    [Paragraph('Стойки, ригеля и створки одной таблицей со сквозной нумерацией', body),
     Paragraph('Источник: ' + M('RF-стойки; RF-ригеля; RF-створки') + ' (или кнопка «…»)', body)],
    [Paragraph('Все элементы, кроме служебных слоёв', body),
     Paragraph('Источник пустой + строка условия: Выражение ' + M('=Object.«Слой»')
               + ', Условие ' + M('≠') + ', Значение ' + M('0;_АР_ВИТРАЖ_БЛОКИ'), body)],
    [Paragraph('Только марки Сп1, Сп2 и Вр1', body),
     Paragraph('Выражение ' + M('=Object.«МАРКИРОВКА»') + ', Условие ' + M('=')
               + ', Значение ' + M('Сп1;Сп2;Вр1'), body)],
    [Paragraph('Исключить вентрешётки и багеты одной строкой', body),
     Paragraph('Условие ' + M('не содержит') + ', Значение ' + M('Вр;Бг'), body)],
    [Paragraph('Доборники и крышки одного артикула в общий раскрой', body),
     Paragraph('Источник: ' + M('RF-доборники; RF-крышки') + ' + фильтр по артикулу', body)],
], colWidths=[62 * mm, 116 * mm])
tbl.setStyle(TableStyle([
    ('FONTNAME', (0, 0), (-1, -1), 'DV'),
    ('BACKGROUND', (0, 0), (-1, 0), colors.HexColor('#e8e8e8')),
    ('GRID', (0, 0), (-1, -1), 0.5, colors.HexColor('#999999')),
    ('VALIGN', (0, 0), (-1, -1), 'TOP'),
    ('LEFTPADDING', (0, 0), (-1, -1), 5), ('RIGHTPADDING', (0, 0), (-1, -1), 5),
    ('TOPPADDING', (0, 0), (-1, -1), 3), ('BOTTOMPADDING', (0, 0), (-1, -1), 3),
]))
S.append(KeepTogether(tbl))
S.append(Spacer(1, 4))
S.append(Paragraph('Пробелы вокруг «;» не важны. Правка готовой таблицы (' + M('ATSPECEDIT')
                   + ') возвращает списки в поля как были. Уже построенные таблицы обновление '
                   'не меняет.', body))

# ── 5. Если что-то не так ──
S.append(Paragraph('5. Если что-то пошло не так', h1))
S.append(Paragraph('Окна программы теперь говорящие: пустой отчёт, конфликт настроек и потеря фильтра '
                   'объясняются текстом, а не молчанием. Пришлите скрин окна и файл '
                   + M('ATableSpec_last_def.json') + ' из папки временных файлов '
                   '(Win+R → ' + M('%TEMP%') + ' → Enter) — по ним причина видна точно.', body))

doc = SimpleDocTemplate(OUT, pagesize=A4,
                        leftMargin=17 * mm, rightMargin=15 * mm,
                        topMargin=14 * mm, bottomMargin=14 * mm,
                        title='ATableSpec — обновление 08.07: установка и нововведения')
doc.build(S)
print('PDF:', OUT)
