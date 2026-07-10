#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# Инструкция для проектировщиков по обновлению 10.07 (первый реальный КМД):
# сквозная нумерация, пустые секции, артикул сам в шапке раскроя + одна шапка
# на артикул, служебные фильтры убраны из грида (видны в сводке).
import os
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib import colors
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, KeepTogether
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.environ.get('SESSION_OUT', os.path.join(HERE, 'ATableSpec_session_1007.pdf'))

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

B = lambda t: '<b>' + t + '</b>'
S = []

S.append(Paragraph('ATableSpec — обновление 10.07: по замечаниям с реального КМД', title))
S.append(Paragraph('Нумерация · пустые секции · раскрой из спецификации штапиков · чистый построитель', sub))

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
S.append(Paragraph('Запустить AutoCAD — команды и кнопки прежние, ничего перенастраивать не нужно. '
                   'Уже вставленные таблицы продолжают жить и пересчитываться.', body))

# ── 2. Нумерация ──
S.append(KeepTogether([
    Paragraph('2. Нумерация № п/п — сквозная через отчёты без заголовка', h1),
    Paragraph('Раньше каждый отчёт в построителе нумеровал строки с 1. Теперь правило простое:', body),
    Paragraph('— «Заголовок секции» <b>пуст</b> → нумерация продолжает предыдущий отчёт '
              '(стойки 1–8, ригеля дальше 9–14 — как в СПДС);', num),
    Paragraph('— «Заголовок секции» <b>заполнен</b> → отсчёт этого отчёта начинается с 1.', num),
    Paragraph('Ничего включать не надо: нужен сквозной счёт — оставьте заголовок секции пустым, '
              'нужен свой отсчёт — впишите заголовок.', note)]))

# ── 3. Пустые секции ──
S.append(KeepTogether([
    Paragraph('3. Пустые отчёты в таблицу не печатаются', h1),
    Paragraph('Если по фильтрам отчёта на чертеже не нашлось ни одной строки (например, секции '
              '«…в зонах с терморазрывом» на витраже, где терморазрыва нет), в таблицу такая секция '
              'не попадает вовсе — ни заголовка, ни шапки столбцов.', body),
    Paragraph('В построителе карточка такого отчёта остаётся — это нормально: появятся на чертеже '
              'подходящие блоки, и таблица сама вернёт секцию при пересчёте. Удалять карточку не '
              'обязательно.', note)]))

# ── 4. Раскрой из спецификации штапиков ──
S.append(KeepTogether([
    Paragraph('4. «Взять с табл.»: артикул встаёт сам, один артикул — одна шапка', h1),
    Paragraph('Работает в шаблоне «Раскрой» по кнопке «Взять с табл.», как раньше. Два улучшения:', body),
    Paragraph('1) Если в исходной спецификации в столбце «Артикул» вписан текст (например '
              '<b>17_01_04</b> — просто текстом, без «=»), он сам встаёт в шапку раскроя '
              '«17_01_04  8  6000». Вписывать артикул в раскрой вручную больше не нужно.', num),
    Paragraph('2) Отчёты с <b>одинаковым артикулом</b> собираются под одной шапкой: горизонтальный '
              'и вертикальный штапик 17_01_04 теперь один блок раскроя — у продолжений автоматически '
              'ставится галка «Скрыть шапку столбцов». Хочется снова раздельно — снимите галку.', num),
    Paragraph('3) <b>Количество</b> теперь тоже переносится из исходной спецификации вместе с '
              'выражением (у штапиков «=Count*2» — по два на заполнение). Раньше раскрой считал '
              'голым Count и занижал штуки вдвое — исправлено; количества хлыстов теперь сходятся '
              'со спецификацией.', num),
    Paragraph('Плейсхолдер «Артикул» (когда в исходнике артикул не заполнен) ведёт себя как раньше: '
              'в шапке остаётся «Артикул», в конце появится заметка «впишите вручную».', note)]))

# ── 5. Чистый построитель ──
S.append(KeepTogether([
    Paragraph('5. Служебные строки фильтров убраны из построителя', h1),
    Paragraph('Фильтры, которые приезжают вместе с «Взять с табл.» (МАРКИРОВКА, ШТ_СТЫК, '
              '«ПРОФ = 17_07_04» при развороте по артикулам), больше не показываются строками в '
              'таблице построителя — то самое «что это?».', body),
    Paragraph('Они продолжают работать: проверить их можно в строке-сводке под таблицей — '
              '«Фильтр: МАРКИРОВКА не содержит вр и ШТ_СТЫК = 0». Свои фильтры добавляются как '
              'обычно, строками «Выражение | Условие | Значение».', body),
    Paragraph('Если такой скрытый фильтр понадобится изменить — постройте отчёт заново с нужными '
              'условиями (или напишите нам: сделаем редактирование, если будет нужно на практике).', note)]))

# ── 6. Быстрая проверка ──
S.append(KeepTogether([
    Paragraph('6. Как быстро проверить обновление', h1),
    Paragraph('1) Спецификация стоек + ригелей, у ригелей заголовок секции пуст → номера идут '
              '9, 10, … без сброса.', num),
    Paragraph('2) Штапики на витраже без терморазрыва → секций «…с терморазрывом» в таблице нет.', num),
    Paragraph('3) «Взять с табл.» из спецификации штапиков → одна шапка «17_01_04  8  6000», '
              'артикул подставился сам, служебных строк в гриде нет, фильтр виден в сводке.', num),
    Paragraph('4) Суммарное количество в таблице раскроя равно количеству в спецификации '
              '(раньше у штапиков было вдвое меньше).', num)]))

doc = SimpleDocTemplate(OUT, pagesize=A4,
                        leftMargin=16 * mm, rightMargin=16 * mm,
                        topMargin=14 * mm, bottomMargin=14 * mm,
                        title='ATableSpec — обновление 10.07')
doc.build(S)
print('OK:', OUT)
