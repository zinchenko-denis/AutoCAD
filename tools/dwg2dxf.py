# -*- coding: utf-8 -*-
"""DWG -> DXF конверсия через aspose-cad (паттерн dwg_probe)."""
import sys

def main(src, dst):
    import aspose.cad as cad
    from aspose.cad.imageoptions import DxfOptions
    img = cad.Image.load(src)
    opts = DxfOptions()
    img.save(dst, opts)
    print("OK:", dst)

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
