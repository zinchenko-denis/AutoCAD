# -*- coding: utf-8 -*-
"""Разбор DWG без AutoCAD (среда Cowork): aspose-cad с PyPI.

Установка (грабля-инструкция, проверено 20.07):
  pip install aspose-cad --break-system-packages
  # .NET-рантайму внутри нужен OpenSSL 1.1 (в Ubuntu 24 его нет):
  curl -sO http://security.ubuntu.com/ubuntu/pool/main/o/openssl/libssl1.1_1.1.1f-1ubuntu2.24_amd64.deb
  dpkg -i libssl1.1_*.deb
  # и запуск с DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 (ICU .NET не находит)

Что умеет: блоки/сущности/ATTDEF/вставки/классы. Динамика (имена
BLOCKLINEARPARAMETER/BLOCKVISIBILITYPARAMETER) НЕ извлекается — прокси-
объекты aspose не разбирает; имена динсвойств читать в рантайме C# с
образца (DynamicBlockReferencePropertyCollection) или спрашивать у людей.
Каст обёрток: Cls._cast_as(obj) → .success/.item (не _cast_from!).

Запуск: DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 python3 tools/dwg_probe.py file.dwg
"""
import sys


def main(path):
    import aspose.cad as cad
    import aspose.cad.fileformats.cad as fc
    import aspose.cad.fileformats.cad.cadobjects as co
    import aspose.cad.fileformats.cad.cadobjects.attentities as ae

    img = cad.Image.load(path)
    r = fc.CadImage._cast_as(img)
    if not r.success:
        print("не CadImage"); return
    ci = r.item
    print("=== классы (динамика видна здесь):")
    for c in ci.class_entities:
        if "BLOCK" in (c.name or "") or "EVAL" in (c.name or ""):
            print("  ", c.name)
    ks = list(ci.block_entities.keys_typed)
    vs = list(ci.block_entities.values_typed)
    for k, b in zip(ks, vs):
        ents = list(b.entities)
        if not ents and k.startswith("*Paper"):
            continue
        bp = b.base_point
        print("=== BLOCK %r base=(%.2f, %.2f) entities=%d" %
              (k, bp.x, bp.y, len(ents)))
        for e in ents:
            tn = int(getattr(e, "type_name", -1))
            ar = ae.CadAttDef._cast_as(e)
            if ar.success:
                o = ar.item
                print("  ATTDEF tag=%r prompt=%r default=%r h=%s" %
                      (o.id, o.prompt_string, o.default_string,
                       o.text_height))
                continue
            pr = co.CadLwPolyline._cast_as(e)
            if pr.success:
                pts = list(pr.item.coordinates)
                xs = [p.x for p in pts]; ys = [p.y for p in pts]
                print("  LWPOLY n=%d bbox=(%.2f, %.2f)-(%.2f, %.2f)" %
                      (len(pts), min(xs), min(ys), max(xs), max(ys)))
                continue
            ir = co.CadInsertObject._cast_as(e)
            if ir.success:
                o = ir.item
                print("  INSERT %r at (%.2f, %.2f) rot=%s" %
                      (o.name, o.insertion_point.x, o.insertion_point.y,
                       getattr(o, "rotation_angle", 0)))
                continue
            print("  type_name=%d" % tn)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "in.dwg")
