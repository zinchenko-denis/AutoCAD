"""Render actual exported drawing primitives for visual review, without CAD.

This SVG is a synthetic layout preview. It does not emulate AutoCAD text
metrics, insertion, printing or the native DWG display.
"""
import argparse
import hashlib
import html
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("target", type=Path)
    args = parser.parse_args()
    exported = json.loads(args.source.read_text(encoding="utf-8"))
    drawing = exported["drawing"]
    primitives = drawing["primitives"]
    x0, y0 = drawing["min_x"] - 12, -drawing["max_y"] - 20
    width = drawing["max_x"] - drawing["min_x"] + 24
    height = drawing["max_y"] - drawing["min_y"] + 36
    colors = {"insulation": "#008044", "profile": "#b12b28", "cladding": "#006f8c"}
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{x0} {y0} {width} {height}" width="1400" height="{round(1400*height/width)}">',
             '<title>Синтетическая схема ATFNODE — просмотр примитивов без AutoCAD</title>',
             f'<rect x="{x0}" y="{y0}" width="{width}" height="{height}" fill="white"/>',
             f'<text x="0" y="{y0+8}" font-family="Arial, Liberation Sans, sans-serif" font-size="3.2" fill="#555">Учебные числа; предварительный просмотр примитивов, не снимок AutoCAD</text>']
    for p in primitives:
        color = colors.get(p["role"], "#242b30")
        if p["kind"] == "line":
            parts.append(f'<line x1="{p["x1"]}" y1="{-p["y1"]}" x2="{p["x2"]}" y2="{-p["y2"]}" stroke="{color}" stroke-width="0.25"/>')
        elif p["kind"] == "text":
            h = p["text_height"]
            parts.append(f'<text x="{p["x1"]}" y="{-p["y1"]+h}" font-family="Arial, Liberation Sans, sans-serif" font-size="{h}" fill="{color}">')
            for i, line in enumerate(p["text"].splitlines()):
                parts.append(f'<tspan x="{p["x1"]}" dy="{0 if i==0 else h*1.4}">{html.escape(line)}</tspan>')
            parts.append('</text>')
        else:
            raise ValueError("Unknown actual drawing primitive")
    parts.append('</svg>')
    args.target.parent.mkdir(parents=True, exist_ok=True)
    args.target.write_text('\n'.join(parts)+'\n', encoding='utf-8')
    print(json.dumps({"source_sha256": hashlib.sha256(args.source.read_bytes()).hexdigest(),
                      "drawing_digest": drawing["digest"], "primitives": len(primitives),
                      "svg": str(args.target), "live_autocad_checked": False}))


if __name__ == "__main__":
    main()
