"""Actual project/zone WinForms workflows; native display required, no AutoCAD claim."""
import importlib.util
from pathlib import Path

BASE = Path(__file__).resolve().parents[1] / "solution_catalog_0110/test_solution_ui.py"
spec = importlib.util.spec_from_file_location("solution_ui_runner", BASE)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)

if __name__ == "__main__":
    raise SystemExit(runner.main(probe=Path(__file__).with_name("ProjectUiProbe.cs"), runner_path=Path(__file__).resolve()))
