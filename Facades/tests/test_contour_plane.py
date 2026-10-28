"""ATFZONE: контуры вне плоскости XY с Z = 0 (снимок Германа 28.10).

Исполняет настоящие AFacadesPlugin.ContourPlane и ZoneGeometryGuard на CAD doubles
(поднятые, развёрнутые, вертикальные и наклонные полилинии), затем подаёт
прочитанные для движка контуры в настоящий facades_engine и сравнивает зоны
с тем же контуром на месте. AutoCAD не запускается.
Запуск из корня: python3 Facades/tests/test_contour_plane.py
"""
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/quantities_3009"))
sys.path.insert(0, str(ROOT / "Facades/engine"))
from probe_runtime import available, command as run_command, compile_probe  # noqa: E402
from facades_engine import op_zones  # noqa: E402

SOURCES = ["Facades/tests/ContourPlaneCheck.cs", "Facades/src/AFacadesPlugin/ContourPlane.cs",
           "tools/fixes_3009/test_zone_geometry_cad_doubles.cs", "Common/GeometryFingerprint.cs",
           "Common/ZoneGeometryGuard.cs", "Common/LayoutGeometryGuard.cs"]

# Исходная фигура в системе объекта — те же числа, что в ContourPlaneCheck.cs.
OCS_PTS = [[0.0, 0.0], [6000.0, 0.0], [6000.0, 3000.0], [0.0, 3000.0]]
OCS_BULGES = [0.0, 0.0, 0.25, 0.0]

failed = 0


def check(name, ok, detail=""):
    global failed
    print(("PASS " if ok else "FAIL ") + name + ("" if ok or not detail else ": " + str(detail)))
    failed += 0 if ok else 1


def zone(pts, bulges):
    res = op_zones({"op": "zones", "units": "mm", "cladding": "Тест", "zone_prefix": "Т-",
                    "contours": [{"id": "1", "pts": pts, "bulges": bulges}]})
    assert res.get("ok"), res
    assert len(res["zones"]) == 1, res
    return res["zones"][0]


def inside_bbox(point, pts, top_allowance):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return (min(xs) - 1e-6 <= point[0] <= max(xs) + 1e-6 and
            min(ys) - 1e-6 <= point[1] <= max(ys) + top_allowance + 1e-6)


def main():
    if not available():
        raise SystemExit("mono/mcs or Windows dotnet is required; checks were not run")
    with tempfile.TemporaryDirectory(prefix="contour_plane_") as temp:
        temp = Path(temp)
        executable = temp / "ContourPlaneCheck.exe"
        built = compile_probe([ROOT / p for p in SOURCES], ["System.Web.Extensions"], executable)
        if built.returncode:
            raise SystemExit(built.stdout + built.stderr)
        dump = temp / "read.json"
        # Вывод проверки не перехватываем: кодировка консоли Windows не UTF-8.
        run = subprocess.run(run_command(executable) + [str(dump)])
        if run.returncode:
            raise SystemExit("C# contour plane checks failed")
        cases = json.loads(dump.read_text(encoding="utf-8"))

    # Движок получает контуры в мировых XY: поднятые совпадают с контуром на месте,
    # развёрнутые — его зеркальная копия; площадь и марка должны это отражать.
    base = cases["на месте"]
    ref = zone(base["pts"], base["bulges"])
    area = ref["report"]["area_net_m2"]
    sagitta = 0.25 * 6000 / 2
    check("движок: контур на месте — площадь больше прямоугольника на сегмент дуги", area > 18.0, area)
    for name, case in cases.items():
        if name == "на месте":
            continue
        z = zone(case["pts"], case["bulges"])
        check(name + ": движок — та же площадь", abs(z["report"]["area_net_m2"] - area) <= 1e-6,
              (z["report"]["area_net_m2"], area))
        check(name + ": движок — марка внутри фактического контура",
              inside_bbox(z["label_pt"], case["pts"], sagitta), (z["label_pt"], case["pts"]))
        if not name.startswith("нормаль вниз"):
            check(name + ": движок — та же геометрия, что на месте", case["pts"] == base["pts"] and
                  case["bulges"] == base["bulges"])

    # Прежнее чтение развёрнутого контура (GetPoint2dAt) отдавало координаты его
    # собственной системы: в мире фигура занимает X от −6000 до 0, а марка
    # оказывалась у X = 6000 — вне контура. Новое чтение это исправляет.
    old = zone(OCS_PTS, OCS_BULGES)
    world = cases["нормаль вниз"]["pts"]
    check("прежнее чтение развёрнутого контура ставило марку вне контура",
          not inside_bbox(old["label_pt"], world, sagitta), (old["label_pt"], world))

    print("Contour plane engine checks: " + ("PASS" if failed == 0 else "FAILED (%d)" % failed) +
          "; AutoCAD runtime: NOT RUN")
    if failed:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
