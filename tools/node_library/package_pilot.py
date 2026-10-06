"""Package one private native-author pilot. Does not build DLLs, publish releases or claim a DWG."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import zipfile
from pathlib import Path

import generate


RELEASE_ASSETS = ("AFacades.bundle.zip", "AClad.bundle.zip", "AFrame.bundle.zip",
                  "Install_AutoCAD_2024.docx", "Facades_User_Manual.docx")


def release_links(release_build):
    if release_build is None:
        return {}
    if isinstance(release_build, bool) or not isinstance(release_build, int) or release_build < 110:
        raise ValueError("The node commands require an explicitly selected build 110 or newer")
    base = f"https://github.com/zinchenko-denis/AutoCAD/releases/download/build-{release_build}/"
    return {name: base + name for name in RELEASE_ASSETS}


def make_package(source, guide, fixes, diagram, out, code_sha, ci_url, release_build=None):
    downloads = release_links(release_build)
    if not re.fullmatch(r"[0-9a-f]{40}", code_sha):
        raise ValueError("code_sha must identify the exact tested commit")
    data = json.loads(source.read_text(encoding="utf-8-sig"))
    if data["node_id"] != "vector1_2015_type1_4_2_1_training":
        raise ValueError("This handout packages only the explicitly agreed first training node")
    lsp = generate.render(data).encode("utf-8-sig")
    root = Path(__file__).resolve().parents[2]
    files = {
        "vector1_2015_4_2_1_training.lsp": lsp,
        "Node_Pilot_Guide_2026-10-06.docx": guide.read_bytes(),
        "Fixes_2026-10-05_06.txt": fixes.read_bytes(),
        "Before_After.png": diagram.read_bytes(),
        "Facade_18x12_15windows.dxf": (root / "docs/pilot/assets/Facade_18x12_15windows.dxf").read_bytes(),
        "source/vector1_2015_4_2_1_training.json": source.read_bytes(),
    }
    if downloads:
        # Include the existing instructions: GitHub DOCX links were inaccessible
        # in the customer's session. This does not rebuild either document.
        for name in ("Install_AutoCAD_2024.docx", "Facades_User_Manual.docx"):
            files[name] = (root / "docs/user" / name).read_bytes()
        plugin_instructions = f"""ДЛЯ КОМАНД ПЛАГИНА И ФАСАДНЫХ ИСПРАВЛЕНИЙ — ВЫПУСК №{release_build}
Обе общие инструкции DOCX вложены в этот ZIP; скачивать их по ссылкам не нужно.
Если модули ещё не установлены, откройте Install_AutoCAD_2024.docx и установите
AFacades, AClad и AFrame из одного номера. Прямые ссылки закреплены за
build-{release_build}; вход GitHub для скачивания не нужен:
""" + "\n".join(f"{name}\n{url}" for name, url in downloads.items()) + f"""
После установки у всех трёх модулей в F2 должен быть номер {release_build}.
Если выпуск №{release_build} уже установлен, ради исправления LISP переустановка
трёх модулей не нужна. Загрузите новый LISP из этого архива через APPLOAD.
ATFNODEIMPORT/EDIT/DEMO/TEST проверяются с этим комплектом.
Установка модулей не подтверждает создание нативного DWG: маршрут автора
и живой опыт «три из пяти» в AutoCAD ещё нужно выполнить.
"""
    else:
        plugin_instructions = """ATFNODEIMPORT/EDIT/DEMO/TEST требуют новых фасадных DLL. В №108 их нет.
Номер установочного выпуска для этого автономного пакета не указан.
LISP-автор и ручной опыт через Properties можно проверить без новых DLL.
"""
    readme = f"""ТЕСТОВЫЙ УЗЕЛ ДЛЯ AUTOCAD 2024 — ИСПРАВЛЕНИЕ ЗАПУСКА 06.10.2026

Начните с Node_Pilot_Guide_2026-10-06.docx.
Распакуйте весь архив в отдельную папку. В полном AutoCAD 2024:
новый пустой несохранённый чертёж → APPLOAD →
vector1_2015_4_2_1_training.lsp → ATFNATIVEBUILD.

В этой редакции изменение вида/окна в пустом новом DWG больше не вызывает
ложный отказ DBMOD. Реальные изменения объектов и базы по-прежнему проверяются.
Причина остановки и исходные настройки печатаются в F2. При активной чужой
группе UNDO команда не закрывает её автоматически; следуйте инструкции узла.

Готовый DWG должен создать и проверить AutoCAD на вашем компьютере.
Здесь лежит авторский LISP, а не уже проверенный динамический DWG.
Значения A: утеплитель 100, вынос 230, КР2-70-200, ГП-40-40-1,2.
Значения B: утеплитель 150, вынос 280, КР2-70-250, ГП-60-40-1,2.
После успешной авторской проверки пять вставок сохраняются как 3B + 2A.
Дальнейшая ручная проверка через Properties описана в инструкции.

{plugin_instructions}
Этот ZIP не обновляет AFacades/AClad/AFrame и не заменяет установочный выпуск.
Facade_18x12_15windows.dxf — исходный учебный фасад для проверки исправлений
подсистемы после установки исправленных модулей, а не DWG динамического узла.

Fixes_2026-10-05_06.txt — ошибки и порядок их проверки.
Test_Results.txt — краткий бланк результата, заполняется проектной группой.
Передайте Денису заполненный бланк, F2/журнал, DWG и снимки до/после.
Монтажная пригодность и несущая способность этим опытом не подтверждаются.

Код: {code_sha}
Windows CI: {ci_url}
Успешная нативная приёмка: НЕ ЗАВЕРШЕНА. Прежняя редакция остановилась
на проверке состояния; эта исправленная редакция ещё требует запуска в AutoCAD.
Геометрия и первичные данные предназначены для проектной группы;
не переносите содержимое папки source и LISP в публичный репозиторий.
"""
    result = """ПРОТОКОЛ ПРОЕКТНОЙ ГРУППЫ — ТЕСТОВЫЙ УЗЕЛ И ИСПРАВЛЕНИЯ 05–06.10
Дата и исполнитель:
Версия/язык AutoCAD 2024:
Номер установленного выпуска трёх фасадных модулей (из F2):
Исходный DWG/шаблон:

Для каждой строки: ПОЛУЧИЛОСЬ / ОШИБКА / НЕ ПРОВЕРЯЛИ + наблюдение.
1. APPLOAD/ATFNATIVEBUILD, успешное создание и сохранение DWG:
2. Все пять приведены в A (100/230), контуры/размеры/марки:
3. Рамкой выбраны ровно три и переведены в B (150/280):
4. Две невыбранные сохранили геометрию, размеры и марки A:
5. Повтор B без накопления смещения; возврат в A:
6. Линейные ручки и Properties:
7. COPY: изменение копии сохраняет исходник:
8. Undo и сохранение/закрытие/открытие:
9. После обновления DLL: ATFNODEIMPORT/EDIT/DEMO/TEST:
10. По отдельному списку исправлений: номера проверенных пунктов:

Если есть ошибка:
Точная последовательность действий и введённые значения:
Ожидалось:
Получилось:
Полный текст F2, этап и последний запрос команды:
Приложены: DWG / F2 или лог / снимки / XLSX / файл зон JSON.
Отказ обычного сценария записывается как ошибка, не как успешная проверка.
"""
    files["README_FIRST.txt"] = readme.encode("utf-8-sig")
    files["Test_Results.txt"] = result.encode("utf-8-sig")
    manifest = {
        "schema": "af_node_pilot_package/1",
        "code_sha": code_sha, "ci_url": ci_url,
        "node_id": data["node_id"], "native_autocad_executed": False,
        "contains_native_dwg": False, "contains_plugin_bundles": False,
        "required_plugin_release": ({"build": release_build, "assets": downloads} if downloads else None),
        "files": {name: hashlib.sha256(blob).hexdigest() for name, blob in files.items()},
    }
    files["package-info.json"] = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    out.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, blob in files.items():
            info = zipfile.ZipInfo(name, date_time=(2026, 10, 6, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, blob)
    with zipfile.ZipFile(out) as archive:
        if archive.testzip() is not None or set(archive.namelist()) != set(files):
            raise ValueError("ZIP validation failed")
        for name, blob in files.items():
            if archive.read(name) != blob:
                raise ValueError("ZIP byte mismatch: " + name)
    return {"zip": str(out), "files": len(files), "bytes": out.stat().st_size,
            "sha256": hashlib.sha256(out.read_bytes()).hexdigest(), "native_dwg": False}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("source", "guide", "fixes", "diagram", "out"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--code-sha", required=True)
    parser.add_argument("--ci-url", required=True)
    parser.add_argument("--release-build", type=int,
                        help="Explicit published build 110 or newer; omit for the offline pilot without release links")
    args = parser.parse_args()
    print(json.dumps(make_package(**vars(args)), ensure_ascii=False))
