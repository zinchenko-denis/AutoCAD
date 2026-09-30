"""Read-only engine regression probes, audit 30 September 2026.
Failing assertions describe desired behaviour; no production source is changed.
Run: PYTHONDONTWRITEBYTECODE=1 python3 tools/review_3009/test_spec_review.py
"""
import sys
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "ATableSpec/engine"))
from atspec_report import run_report


def block(mark, length):
    return {"name": "Profile", "layer": "RF-стойки", "attributes": {"ИМЯ": mark, "ДЛИНА": str(length)}}


def report(records, group=1):
    return run_report(records, {"sections": [{"header": ["Марка", "Длина", "Кол."],
        "columns": ["=Object.«ИМЯ»", "=Object.«Длина»", "=Count"],
        "group_by": group, "sort_by": [group, "asc"]}]})


class ReportReview(unittest.TestCase):
    def test_equivalent_numeric_lengths_form_one_row(self):
        # Known open issue: raw dynamic values bypass NumClean during recompute.
        rows = report([block("С1", "1500"), block("С1", "1499.99999999998")])["rows"]
        print("double-tail rows:", rows)
        self.assertEqual(len(rows), 1, rows)
        self.assertEqual(rows[0][2], 2)

    def test_trimmed_marks_form_one_row(self):
        rows = report([block("С1", 1500), block("С1 ", 1500)], group=0)["rows"]
        print("space rows:", rows)
        self.assertEqual(len(rows), 1, rows)

    def test_all_empty_sections_remain_to_clear_previous_table(self):
        # Negative control: suspected stale-table path is NOT a defect here.
        result = report([])
        self.assertEqual(result["rows"], [])
        self.assertEqual(len(result["sections"]), 1)
        self.assertEqual(result["sections"][0]["rows"], [])

    def test_clean_quantities(self):
        self.assertEqual(report([block("С1", 1500), block("С1", 1500)])["rows"], [["С1", 1500, 2]])


if __name__ == "__main__":
    unittest.main(verbosity=2)
