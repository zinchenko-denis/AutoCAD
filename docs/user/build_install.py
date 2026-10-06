#!/usr/bin/env python3
"""Build the short installation DOCX from INSTALL_AUTOCAD_2024.md.

Uses the existing handout typography; rendering is a separate QA step.
"""
from pathlib import Path
import importlib.util

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location(
    "handouts", ROOT.parent / "pilot_release_0110" / "build_pilot_documents.py")
handouts = importlib.util.module_from_spec(spec)
spec.loader.exec_module(handouts)


def main():
    document = handouts.new_document("Скачивание и установка фасадных модулей в AutoCAD 2024")
    document.core_properties.subject = "Установка трёх фасадных модулей для проектного отдела"
    document.core_properties.keywords = "AutoCAD 2024, GitHub, установка, обновление"
    document.core_properties.comments = ""
    footer = document.sections[0].footer.paragraphs[0]
    footer.clear()
    page = handouts.element("w:fldSimple", **{"w:instr": "PAGE"})
    run = handouts.element("w:r")
    value = handouts.element("w:t")
    value.text = "1"
    run.append(value)
    page.append(run)
    footer._p.append(page)
    handouts.markdown(document, (ROOT / "INSTALL_AUTOCAD_2024.md").read_text(encoding="utf-8"))
    for paragraph in document.paragraphs:
        if paragraph.text in ("Проверить первый запуск", "Если возникла проблема"):
            paragraph.paragraph_format.page_break_before = True
    target = ROOT / "Install_AutoCAD_2024.docx"
    document.save(target)
    print(target)


if __name__ == "__main__":
    main()
