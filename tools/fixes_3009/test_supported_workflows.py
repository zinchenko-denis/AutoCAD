"""Positive controls: safety refusals must not disable the supported product."""
import pathlib
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "AFrame/engine"))
from frame_plan import frame_plan


class SupportedFrames(unittest.TestCase):
    def test_calculated_schemes_produce_real_frames(self):
        for scheme in ("vertical", "interfloor", "ortho"):
            with self.subTest(scheme=scheme):
                result = frame_plan({
                    "system": "Межэтажная" if scheme == "interfloor" else "Вектор-1",
                    "sub_type": scheme, "cladding": "porcelain", "nsp_type": "НСП-1",
                    "contours": [{"outer": [[0, 0], [6000, 0], [6000, 6000], [0, 6000]]}],
                    "joints_x": list(range(0, 6001, 600)),
                    "rows_y": list(range(600, 6000, 600)),
                    "floors_y": [0, 3000, 6000],
                    "calc": {"wind_region": "II", "terrain": "B", "height": 30,
                             "q_clad": 25, "offset": 230, "na_max": 3000},
                })
                self.assertTrue(result.get("ok"), result.get("error"))
                self.assertTrue(result["rails"])
                self.assertTrue(result["brackets"])
                self.assertTrue(result["calc_report"])
                self.assertGreater(result["calc_report"]["steps"]["main"], 0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
