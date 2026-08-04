# -*- coding: utf-8 -*-
"""DWG → дамп вставок/полилиний/линий в МИРОВЫХ координатах (aspose-cad).

Зачем отдельно от dwg_probe.py: тот печатает содержимое ОПРЕДЕЛЕНИЙ
блоков, а для диф-сверки нужен разложенный чертёж — что и где реально
стоит в модели.

ГРАБЛЯ (04.08, стоила часа): `ci.entities` отдаёт лишь верхушку
(в полигоне 99 сущностей из 6225). Вся геометрия модели лежит в
БЛОКЕ с именем `*Model_Space` — обходить надо
`ci.block_entities['*Model_Space']`, рекурсивно разворачивая вставки
с накоплением поворота и смещения.

Установка aspose — грабля-22 (шапка dwg_probe.py):
  pip install aspose-cad --break-system-packages
  + libssl1.1 deb + DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

Запуск:
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
      python3 tools/dwg_probe_tree.py file.dwg out.json

Выход: {"inserts":[{n,x,y,r,lay,par,d}], "polys":[{lay,pts,par,cl}],
"lines":[{lay,p,q,par}]}; par — имя родительского блока ("MS" =
модель), d — глубина вложенности.
"""

import sys, json, collections, math
import aspose.cad as cad
import aspose.cad.fileformats.cad as fc
import aspose.cad.fileformats.cad.cadobjects as co

path = sys.argv[1]
out_path = sys.argv[2] if len(sys.argv) > 2 else 'dump_tree.json'
ci = fc.CadImage._cast_as(cad.Image.load(path)).item
blocks = {}
ks = list(ci.block_entities.keys_typed); vs = list(ci.block_entities.values_typed)
for k, b in zip(ks, vs):
    blocks[str(k)] = b

out_ins, out_poly, out_line = [], [], []

def walk(ents, ox, oy, rot, chain, depth):
    if depth > 6: return
    ca, sa = math.cos(math.radians(rot)), math.sin(math.radians(rot))
    def W(x, y):
        return (ox + x*ca - y*sa, oy + x*sa + y*ca)
    for e in ents:
        lay = str(getattr(e, 'layer_name', ''))
        ir = co.CadInsertObject._cast_as(e)
        if ir.success:
            o = ir.item
            nm = str(o.name)
            wx, wy = W(o.insertion_point.x, o.insertion_point.y)
            wr = (rot + float(getattr(o, 'rotation_angle', 0) or 0)) % 360
            out_ins.append(dict(n=nm, x=round(wx,2), y=round(wy,2), r=wr,
                                lay=lay, par=chain[-1] if chain else 'MS',
                                d=depth))
            sub = blocks.get(nm)
            if sub is not None and depth < 6:
                walk(list(sub.entities), wx, wy, wr, chain+[nm], depth+1)
            continue
        pr = co.CadLwPolyline._cast_as(e)
        if pr.success:
            pts = [list(W(p.x, p.y)) for p in pr.item.coordinates]
            out_poly.append(dict(lay=lay, pts=[[round(a,2),round(b,2)] for a,b in pts],
                                 par=chain[-1] if chain else 'MS',
                                 cl=bool(int(getattr(pr.item,'flag',0) or 0) & 1)))
            continue
        lr = co.CadLine._cast_as(e)
        if lr.success:
            o = lr.item
            p = W(o.first_point.x, o.first_point.y); q = W(o.second_point.x, o.second_point.y)
            out_line.append(dict(lay=lay, p=[round(p[0],2),round(p[1],2)],
                                 q=[round(q[0],2),round(q[1],2)],
                                 par=chain[-1] if chain else 'MS'))
            continue

walk(list(blocks['*Model_Space'].entities), 0.0, 0.0, 0.0, [], 0)
print('inserts:', len(out_ins), 'polys:', len(out_poly), 'lines:', len(out_line))
json.dump(dict(inserts=out_ins, polys=out_poly, lines=out_line),
          open(out_path,'w'), ensure_ascii=False)
print('AFRAME_*:', sum(1 for i in out_ins if i['n'].startswith('AFRAME_')))
print('родители AFRAME_*:', collections.Counter(i['par'] for i in out_ins if i['n'].startswith('AFRAME_')))
