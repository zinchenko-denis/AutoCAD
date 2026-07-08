#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# PDF из TZ_filters_stands_0807.md (reportlab + DejaVu, как прочие docs-генераторы).
import os, re
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.enums import TA_LEFT
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, 'TZ_filters_stands_0807.md')
OUT = os.environ.get('TZ_OUT', os.path.join(HERE, 'ATableSpec_TZ_filters.pdf'))

FD = '/usr/share/fonts/truetype/dejavu/'
pdfmetrics.registerFont(TTFont('DV', FD + 'DejaVuSans.ttf'))
pdfmetrics.registerFont(TTFont('DVB', FD + 'DejaVuSans-Bold.ttf'))
pdfmetrics.registerFont(TTFont('DVM', FD + 'DejaVuSansMono.ttf'))

S = dict(
    title=ParagraphStyle('t', fontName='DVB', fontSize=15, leading=19, spaceAfter=2),
    sub=ParagraphStyle('s', fontName='DV', fontSize=9, leading=12, textColor='#555555', spaceAfter=10),
    h1=ParagraphStyle('h1', fontName='DVB', fontSize=12.5, leading=16, spaceBefore=12, spaceAfter=5),
    h2=ParagraphStyle('h2', fontName='DVB', fontSize=10.5, leading=14, spaceBefore=9, spaceAfter=3),
    body=ParagraphStyle('b', fontName='DV', fontSize=9.5, leading=13.2, alignment=TA_LEFT, spaceAfter=4),
    li=ParagraphStyle('li', fontName='DV', fontSize=9.5, leading=13.2, leftIndent=7 * mm,
                      bulletIndent=2.5 * mm, spaceAfter=2),
    num=ParagraphStyle('num', fontName='DV', fontSize=9.5, leading=13.2, leftIndent=7 * mm, spaceAfter=2),
)

def esc(t):
    t = t.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
    t = re.sub(r'\*\*(.+?)\*\*', r'<b>\1</b>', t)
    t = re.sub(r'`([^`]+)`', r'<font face="DVM" size="8.7">\1</font>', t)
    return t

story, buf = [], []

def flush():
    if buf:
        story.append(Paragraph(esc(' '.join(buf)), S['body'])); buf[:] = []

lines = open(SRC, encoding='utf-8').read().splitlines()
for ln in lines:
    t = ln.rstrip()
    if not t.strip():
        flush(); continue
    if t.startswith('# '):
        flush(); story.append(Paragraph(esc(t[2:]), S['title'])); continue
    if t.startswith('## '):
        flush(); story.append(Paragraph(esc(t[3:]), S['h1'])); continue
    if t.startswith('### '):
        flush(); story.append(Paragraph(esc(t[4:]), S['h2'])); continue
    if re.match(r'^[-•] ', t.strip()):
        flush(); story.append(Paragraph(esc(t.strip()[2:]), S['li'], bulletText='—')); continue
    m = re.match(r'^(\d+\.|[СПЭ]\d+\.|[СПЭ]\d+ —)\s+(.*)', t.strip())
    if m:
        flush(); story.append(Paragraph('<b>' + esc(m.group(1)) + '</b> ' + esc(m.group(2)), S['num'])); continue
    if t.strip().startswith('Проект на согласование'):
        flush(); story.append(Paragraph(esc(t.strip()), S['sub'])); continue
    if t.startswith('  ') and buf:                    # висячий перенос списка/абзаца
        buf.append(t.strip()); continue
    buf.append(t.strip())
flush()

doc = SimpleDocTemplate(OUT, pagesize=A4,
                        leftMargin=17 * mm, rightMargin=15 * mm,
                        topMargin=14 * mm, bottomMargin=14 * mm,
                        title='ATableSpec — ТЗ: Стойки и многослойные фильтры')
doc.build(story)
print('PDF:', OUT)
