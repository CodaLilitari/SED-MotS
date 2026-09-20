import importlib.util
from pathlib import Path
import tempfile
import unittest

_spec = importlib.util.spec_from_file_location("oracle", Path(__file__).parents[1] / "compare_engine_lighting.py")
oracle = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(oracle)


class EngineImageOracleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.witness = {"position": [0, -3, 0], "target": [0, 0, 0], "verticalFov": 65,
                        "panelWorldVertices": [[-.9, 0, -.7], [.9, 0, -.7], [.7, 0, .7], [-.9, 0, .7]]}
        polygon, winding = oracle.project_panel(self.witness, 160, 120)
        self.inside, self.outside = oracle.masks(polygon, winding, 160, 120, margin=0)
        self.paths = {}
        self.images = {}
        for name, color in {"reset": [0, 0, 0], "unbound": [5, 5, 5], "baked": [100, 60, 40], "rgb-swapped": [40, 60, 100],
                            "white-only-change": [100, 60, 40], "extensions-omitted": [100, 60, 40], "repeat": [100, 60, 40]}.items():
            pixels = bytearray([80, 120, 90] * (160*120))
            for offset in self.inside:
                pixels[offset:offset+3] = bytes(color)
            self.images[name] = pixels
            self.write(name, pixels)

    def write(self, name, pixels):
        path = self.root / (name + ".ppm")
        path.write_bytes(b"P6\n160 120\n255\n" + pixels)
        self.paths[name] = path

    def test_expected_localized_channel_response_passes(self):
        result = oracle.compare(self.paths, self.witness)
        self.assertTrue(result["passed"], result)
        self.assertGreater(result["panelPixels"], 100)

    def test_ignored_arch_or_absent_panel_cannot_pass(self):
        for name in self.paths:
            self.write(name, self.images["reset"])
        result = oracle.compare(self.paths, self.witness)
        self.assertFalse(result["checks"]["bakeVisibleOnPanel"])
        self.assertFalse(result["checks"]["rgbSwapVisibleOnPanel"])

    def test_whole_frame_tint_is_not_panel_evidence(self):
        pixels = self.images["rgb-swapped"][:]
        for offset in self.outside:
            pixels[offset] += 20
        self.write("rgb-swapped", pixels)
        result = oracle.compare(self.paths, self.witness)
        self.assertTrue(result["checks"]["rgbSwapVisibleOnPanel"])
        self.assertFalse(result["checks"]["unchangedWorldOutsidePanel"])

    def test_unchanged_missing_world_is_not_visible_world_evidence(self):
        for name in self.images:
            pixels = self.images[name][:]
            for offset in self.outside:
                pixels[offset:offset+3] = bytes(3)
            self.write(name, pixels)
        result = oracle.compare(self.paths, self.witness)
        self.assertTrue(result["checks"]["unchangedWorldOutsidePanel"])
        self.assertFalse(result["checks"]["coloredWorldCoversOutsidePanel"])

    def test_unbound_is_not_a_zero_reset_or_colored_bake(self):
        for other, check in [("reset", "unboundFallbackDistinctFromZeroReset"),
                             ("baked", "unboundNeutralFallback")]:
            with self.subTest(other=other):
                self.write("unbound", self.images[other])
                self.assertFalse(oracle.compare(self.paths, self.witness)["checks"][check])

    def test_wrong_channel_direction_refuses(self):
        self.write("rgb-swapped", self.images["baked"])
        self.assertFalse(oracle.compare(self.paths, self.witness)["checks"]["rgbSwapVisibleOnPanel"])

    def test_opposite_green_errors_cannot_cancel_in_mean(self):
        pixels = self.images["rgb-swapped"][:]
        for index, offset in enumerate(self.inside):
            pixels[offset+1] += 30 if index % 2 else -30
        self.write("rgb-swapped", pixels)
        result = oracle.compare(self.paths, self.witness)
        self.assertFalse(result["checks"]["rgbSwapVisibleOnPanel"])
        self.assertEqual(result["swappedMinusBaked"]["maxAbsDeltaRgb"][1], 30)

    def test_opposite_red_blue_errors_cannot_hide_in_correct_mean(self):
        pixels = self.images["rgb-swapped"][:]
        for index, offset in enumerate(self.inside):
            if index % 5 < 2:
                pixels[offset:offset+3] = bytes([160, 60, 0])
        self.write("rgb-swapped", pixels)
        result = oracle.compare(self.paths, self.witness)
        self.assertLess(result["swappedMinusBaked"]["meanSignedDeltaRgb"][0], -2)
        self.assertGreater(result["swappedMinusBaked"]["meanSignedDeltaRgb"][2], 2)
        self.assertGreater(result["swapWrongDirectionPixels"], 100)
        self.assertFalse(result["checks"]["rgbSwapVisibleOnPanel"])

    def test_white_response_or_extension_effect_or_repeat_drift_refuses(self):
        for name, check in [("white-only-change", "whiteOnlyChangePreservesRgbFrame"),
                            ("extensions-omitted", "extensionRemovalPreservesFrame"), ("repeat", "repeatFrameMatches")]:
            with self.subTest(name=name):
                self.write(name, self.images["reset"])
                self.assertFalse(oracle.compare(self.paths, self.witness)["checks"][check])
                self.write(name, self.images[name])

    def test_independent_geometry_mask_refuses_wrong_location(self):
        shifted = dict(self.witness, panelWorldVertices=[[x+1.2, y, z] for x, y, z in self.witness["panelWorldVertices"]])
        self.assertFalse(oracle.compare(self.paths, shifted)["passed"])

    def test_partly_missing_panel_cannot_pass_on_remaining_average(self):
        for name in self.images:
            pixels = self.images[name][:]
            for offset in self.inside:
                if offset // (160 * 3) < 55:
                    pixels[offset:offset+3] = bytes(3)
            self.write(name, pixels)
        result = oracle.compare(self.paths, self.witness)
        self.assertTrue(result["checks"]["bakeVisibleOnPanel"])
        self.assertFalse(result["checks"]["bakeCoversProjectedPanel"])

    def test_truncated_trailing_and_oversized_ppm_refuse(self):
        for payload in [b"P6\n160 120\n255\nshort", b"P6\n99999 99999\n255\n", self.paths["baked"].read_bytes()+b"x"]:
            with self.subTest(size=len(payload)):
                path = self.root / "bad.ppm"
                path.write_bytes(payload)
                with self.assertRaises(ValueError):
                    oracle.read_ppm(path)

    def test_degenerate_or_behind_camera_witness_refuses(self):
        for change in [{"target": [0, -3, 0]}, {"panelWorldVertices": [[0, -4, 0], [1, -4, 0], [0, -4, 1]]},
                       {"panelWorldVertices": [[0, 0, 0], [0, 0, 0], [0, 0, 0]]}]:
            with self.subTest(change=change), self.assertRaises(ValueError):
                oracle.project_panel(dict(self.witness, **change), 160, 120)


if __name__ == "__main__":
    unittest.main()
