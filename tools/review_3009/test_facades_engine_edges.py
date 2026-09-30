"""Independent edge-case reproductions, review 30.09.2026.

Expected behaviour assertions intentionally expose outstanding defects at e2a4e4d.
Run: python3 tools/review_3009/test_facades_engine_edges.py -v
No product files are modified.
"""
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Facades" / "engine"))
import facades_engine as fe
import facade_zones as fz


class IndependentFacadesEdges(unittest.TestCase):
    def test_parapet_can_be_processed_without_creating_another_facade_zone(self):
        parapet = {"id": "P", "closed": True,
                   "pts": [[0, 3000], [5000, 3000], [5000, 3200], [0, 3200]]}
        # Numeric oracle: a horizontal 5 m coping, already valid in the same engine.
        self.assertEqual(fz.parapet_report(parapet)["top_m"], 5.0)
        out = fe.run({"op": "zones", "units": "mm", "contours": [], "parapets": [parapet]})
        self.assertTrue(out["ok"], out)
        self.assertEqual(out["zones"], [])
        self.assertEqual(out["summary"]["parapets_top_m"], 5.0)

    def test_open_parapet_without_a_facade_zone(self):
        out = fe.run({"op": "zones", "units": "mm", "contours": [],
                      "parapets": [{"id": "P-open", "closed": False, "pts": [[0, 0], [3000, 4000]]}]})
        self.assertTrue(out["ok"], out)
        self.assertEqual(out["zones"], [])
        self.assertEqual(out["summary"]["parapets_top_m"], 5.0)

    def test_merged_label_is_invariant_under_metre_millimetre_conversion(self):
        contours = [
            {"id": "A", "pts": [[0, 0], [4, 0], [4, 3.4], [0, 3.4]]},
            {"id": "B", "pts": [[5, 0], [9, 0], [9, 3], [5, 3]]},
        ]
        metres = fe.run({"op": "zones", "units": "m", "merge": True, "contours": contours})
        millimetres = fe.run({"op": "zones", "units": "mm", "merge": True, "contours": [
            dict(c, pts=[[x * 1000, y * 1000] for x, y in c["pts"]]) for c in contours
        ]})
        self.assertEqual(metres["summary"]["area_net_total_m2"], 25.6)
        self.assertEqual(millimetres["summary"]["area_net_total_m2"], 25.6)
        self.assertEqual(metres["zones"][0]["label_pt"],
                         [v / 1000 for v in millimetres["zones"][0]["label_pt"]])


if __name__ == "__main__":
    unittest.main()
