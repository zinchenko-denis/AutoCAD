#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ABlockGen: первая инструкция для Алексея — установка, полный функционал
# первой сборки (ATVITRAGEAR по АР + ATVITRAGE по проёму), сценарий приёмки
# на «Проба 3», ограничения v1. Паттерн — make_session_* из ATableSpec.
import os
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib import colors
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, KeepTogether
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.environ.get('GUIDE_OUT', os.path.join(HERE, 'ABlockGen_guide_1307.pdf'))

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

S.append(Paragraph('ABlockGen — генератор витражей из блоков. Первая сборка (13.07)', title))
S.append(Paragraph('Новая отдельная программа: строит каркас витража (стойки + ригели) прямо '
                   'по чертежу АР — или сетку по габаритам проёма. Результат — обычные блоки '
                   'на слоях RF-*, спецификации и раскрой по ним делает ATableSpec как всегда.', sub))

# ── 1. Установка ──
S.append(Paragraph('1. Установка', h1))
S.append(Paragraph('Закрыть AutoCAD. Скачать пакет (ссылка одной строкой, вокруг неё ничего '
                   'не дописывать):', body))
S.append(Paragraph('https://github.com/zinchenko-denis/AutoCAD/releases/download/latest/ABlockGen.bundle.zip', mono))
S.append(Paragraph('Если ссылка не открывается — страница релизов, файл ABlockGen.bundle.zip там же:', body))
S.append(Paragraph('https://github.com/zinchenko-denis/AutoCAD/releases', mono))
S.append(Paragraph('Распаковать архив; папку <b>ABlockGen.bundle</b> положить ЦЕЛИКОМ в папку '
                   'плагинов (ту же, где уже лежит ATableSpec.bundle):', body))
S.append(Paragraph('%APPDATA%\\Autodesk\\ApplicationPlugins\\', mono))
S.append(Paragraph('Запустить AutoCAD. В командной строке появится сообщение «ABlockGen загружен…» — '
                   'значит, программа встала. Больше ничего настраивать не нужно.', body))
S.append(Paragraph('Это ОТДЕЛЬНАЯ программа: ATableSpec она не трогает и не обновляет, '
                   'работают они независимо, лежат рядом. Требования те же: AutoCAD 2013–2024, '
                   'Windows 64. Внутри пакета есть расчётный движок vitrage_engine.exe — '
                   'это нормальная часть программы (как dxf_spec.exe у ATableSpec); '
                   'если антивирус спросит — разрешить.', note))

# ── 2. Что умеет ──
S.append(Paragraph('2. Что умеет первая сборка', h1))
S.append(Paragraph('Две команды (вводятся в командной строке, кнопок пока нет):', body))
S.append(Paragraph('— <b>ATVITRAGEAR</b> — построить каркас ПО ЧЕРТЕЖУ АР: указываете образец '
                   'стойки, образец ригеля и АР-графику витража — программа распознаёт сетку '
                   'импостов и строит каркас из ваших блоков. Это ваш сценарий из файла «Проба 3».', num))
S.append(Paragraph('— <b>ATVITRAGE</b> — построить сетку витража ПО ПРОЁМУ: две точки '
                   'прямоугольника + окно параметров (шаг стоек, отметки ригелей, ярусы) — '
                   'программа ставит стойки, ригели и заполнения без всякого АР.', num))
S.append(Paragraph('Обе команды вставляют ВХОЖДЕНИЯ существующих блоков чертежа — своих блоков '
                   'программа не выдумывает. Стойки ложатся на слой RF-стойки, ригели — на '
                   'RF-ригеля (заполнения ATVITRAGE — на RF-заполнения). Атрибуты заполняются: '
                   'ИМЯ (марка), ПРОФ, ДЛИНА — у заполнений МАРКИРОВКА и РАЗМЕР_ЗАП. '
                   'Длина блока ставится динамическим параметром «Длина» — блок реально '
                   'растягивается до нужного размера.', body))
S.append(Paragraph('Вся вставка — ОДНО действие отмены: если результат не понравился, '
                   'Ctrl+Z убирает весь каркас разом.', note))

# ── 3. ATVITRAGEAR ──
S.append(Paragraph('3. ATVITRAGEAR — каркас по чертежу АР (главное для проверки)', h1))
S.append(Paragraph('Пошагово, на вашем файле «Проба 3»:', body))
S.append(Paragraph('1. Ввести команду ATVITRAGEAR.', num))
S.append(Paragraph('2. «Укажите блок стоек (образец)» — кликнуть блок стойки '
                   '(в файле — в жёлтой рамке).', num))
S.append(Paragraph('3. «Укажите блок ригелей (образец)» — кликнуть блок ригеля.', num))
S.append(Paragraph('4. «Укажите блок ВЕРХНЕГО ригеля (в свету) &lt;Enter — обычный&gt;» — '
                   'если самый верхний ряд ригелей у вас идёт другим профилем и режется '
                   'В СВЕТУ между стойками (как Р31/Р33 из профиля стойки в вашей зелёной '
                   'конструкции) — кликнуть его образец. Если такой ряд не нужен — просто Enter, '
                   'весь каркас пойдёт обычным ригелем.', num))
S.append(Paragraph('5. «Укажите конструкцию (АР-графику)» — выделить рамкой АР-витраж '
                   '(красная рамка). Лишнее в выборе НЕ мешает: тексты, размеры, значки '
                   'створок, стрелки программа отбрасывает сама; готовые RF-блоки пропускает.', num))
S.append(Paragraph('6. «Точка вставки конструкции &lt;Enter — на месте АР&gt;» — указать, куда '
                   'поставить каркас (точка = левый нижний угол). Enter — построить прямо '
                   'поверх АР-подложки.', num))
S.append(Paragraph('Как программа думает', h2))
S.append(Paragraph('Она находит в выборе прямоугольные полосы импостов (толщиной 40–120 мм). '
                   'Вертикальные полосы одной оси склеиваются по высоте в ЦЕЛЬНУЮ стойку — '
                   'даже если АР рисует её кусками между ригелями. Толщину профиля программа '
                   'берёт из самих полос (в «Проба 3» — 50 мм). У крайних полос с зазором-откосом '
                   '(75 = 50 тело + 25 зазор) ось ставится по телу, прижатому внутрь витража. '
                   'Отметки ригелей — по осям горизонтальных полос; ригель ставится в каждом '
                   'пролёте, который полоса реально пересекает. Дверь программа узнаёт по имени '
                   'панели АР и НЕ ставит ригель-порог под ней. Размерные цепочки АР вообще '
                   'не читаются — вся информация берётся из геометрии полос, поэтому «как '
                   'образмерил архитектор» роли не играет.', body))
S.append(Paragraph('Что должно получиться на «Проба 3»', h2))
S.append(Paragraph('4 стойки с ДЛИНА = 4185.00 и 11 ригелей: по крайним пролётам 445.00, '
                   'в среднем 1710.00; внизу среднего пролёта (под дверью) ригеля НЕТ. '
                   'Если на шаге 4 указан образец верхнего ригеля — верхний ряд станет '
                   '395.00 / 1660.00 (в свету). Это в точности ваша зелёная конструкция: '
                   'на ней и сверено, совпадение до 0.5 мм.', body))
S.append(Paragraph('После построения в командной строке печатается сводка (сколько стоек/ригелей) '
                   'и заметки — например «тело профиля выведено из АР: 50.0 мм», «порогов под '
                   'дверями пропущено: 1». Эти строки — главная диагностика, при вопросах '
                   'пришлите их вместе со скриншотом.', note))

# ── 4. ATVITRAGE ──
S.append(Paragraph('4. ATVITRAGE — сетка по проёму (без АР)', h1))
S.append(Paragraph('1. Ввести ATVITRAGE, указать два противоположных угла проёма.', num))
S.append(Paragraph('2. В окне задать параметры (все размеры — мм чертежа):', num))
S.append(Paragraph('— <b>Шаг стоек</b> ИЛИ <b>число долей</b> (равные пролёты). Крайние стойки '
                   'встают телом в край проёма, остаток уходит в последний пролёт;', num))
S.append(Paragraph('— <b>Отметки ригелей</b> от низа, осевые, через точку с запятой: '
                   'например «45; 595; 955». Пусто — без ригелей;', num))
S.append(Paragraph('— <b>Ярусы стоек</b> длинами через «;» (например «2715; 2990») и <b>зазор</b> '
                   'между ними (терморазрыв, по умолчанию 10). Пусто — одна стойка на всю высоту;', num))
S.append(Paragraph('— <b>Блоки</b> стойки / ригеля / заполнения — выпадающие списки блоков '
                   'ЭТОГО чертежа (свои имена подставляются, если найдены). Заполнение можно '
                   'оставить пустым — тогда ставятся только стойки и ригели;', num))
S.append(Paragraph('— <b>Тело стойки/ригеля</b> и <b>заход в фальц</b>: из них считается '
                   'РАЗМЕР_ЗАП заполнений = свет + заход (по умолчанию 15), пишется как '
                   '«666Х519» с русской Х — как в ваших чертежах;', num))
S.append(Paragraph('— <b>Марки</b>: шаблоны С{n} / Р{n} / Сп{n}. Одинаковый типоразмер '
                   'получает одну марку, нумерация с 1.', num))
S.append(Paragraph('3. «Построить» — программа ставит стойки, ригели по отметкам в каждом '
                   'пролёте и заполнения в ячейки (щели уже 50 мм пропускаются).', num))

# ── 5. Связка с ATableSpec ──
S.append(Paragraph('5. Проверка связки с ATableSpec', h1))
S.append(Paragraph('Построенный каркас — обычные блоки, поэтому сразу: выделить его рамкой → '
                   'ATSPECREPORT → шаблон «Спецификация». Стойки и ригели с их длинами должны '
                   'попасть в таблицу как всегда. Это и есть смысл связки: сгенерировал '
                   'витраж — сразу получил спецификацию.', body))

# ── 6. Ограничения ──
S.append(Paragraph('6. Ограничения первой сборки (честно)', h1))
S.append(Paragraph('— Марки всегда начинаются с 1 (С1, Р1…). Продолжение сквозной нумерации '
                   'проекта пока не делается;', num))
S.append(Paragraph('— деталировка (DOBL/DOBR/UGL/UGR/KLL/KLR) не заполняется — в атрибутах нули, '
                   'обвес добавляете как обычно;', num))
S.append(Paragraph('— двери, окна, створки, стеклопакеты каркас НЕ вставляет — их ставят ваши '
                   'утилиты. Двери АР учитываются только чтобы пропустить порог;', num))
S.append(Paragraph('— АР «голыми линиями» (без блоков) программа тоже понимает — полосы '
                   'собираются из пар параллельных отрезков, — но вживую этот путь ещё не '
                   'гонялся. Попадётся такой чертёж — пришлите, проверим;', num))
S.append(Paragraph('— если в чертеже повёрнута ПСК, построение идёт в мировых осях '
                   '(программа предупредит в командной строке).', num))

# ── 7. Обратная связь ──
S.append(Paragraph('7. Если что-то пошло не так', h1))
S.append(Paragraph('Прислать: (1) на каком шаге и что произошло (скриншот или видео), '
                   '(2) текст из командной строки — сводку и заметки после построения, '
                   '(3) по возможности сам DXF или его кусок. Этого достаточно, чтобы '
                   'разобраться без гаданий.', body))

doc = SimpleDocTemplate(OUT, pagesize=A4,
                        leftMargin=17 * mm, rightMargin=15 * mm,
                        topMargin=14 * mm, bottomMargin=14 * mm,
                        title='ABlockGen — установка и первая проверка',
                        author='ABlockGen')
doc.build(S)
print('OK:', OUT)
