"""Checks that runtime evidence cannot promote a fallback into Mac GUI success."""
import importlib.util
from pathlib import Path
import struct
import sys
import unittest

TOOLS = Path(__file__).resolve().parents[1]
sys.path.append(str(TOOLS))
DRIVER = TOOLS / "test_macos_runtime.py"


class RuntimeEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(DRIVER.is_file(), "Native Mac runtime verifier is not implemented")
        spec = importlib.util.spec_from_file_location("sed_mac_runtime_test", DRIVER)
        self.runtime = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.runtime)

    def test_scene_requires_positive_pixels_and_expected_dimensions(self):
        self.runtime.validate_scene_evidence("Wrote /tmp/scene.png — lit pixels 123/307200", (640, 480))
        for log, dimensions in (("Vulkan unavailable; fallback", (640, 480)),
                                ("lit pixels 0/307200", (640, 480)),
                                ("lit pixels 400000/307200", (640, 480)),
                                ("lit pixels 123/307200", (1, 1))):
            with self.subTest(log=log, dimensions=dimensions), self.assertRaises(RuntimeError):
                self.runtime.validate_scene_evidence(log, dimensions)

    def test_window_must_belong_to_exact_child_and_be_visible_editor_size(self):
        window = {"pid": 123, "windowID": 9, "onScreen": True, "layer": 0, "width": 900, "height": 650}
        self.assertEqual(9, self.runtime.owned_window([window], 123)["windowID"])
        for changed in (dict(window, pid=999), dict(window, onScreen=False),
                        dict(window, layer=2), dict(window, width=20)):
            self.assertIsNone(self.runtime.owned_window([changed], 123))

    def test_process_survival_alone_cannot_satisfy_gui_evidence(self):
        self.assertFalse(self.runtime.gui_qualified({"processStarted": True, "windowObserved": False, "screenshotObserved": False}))
        self.assertFalse(self.runtime.gui_qualified({"processStarted": True, "windowObserved": True, "screenshotObserved": False}))
        self.assertTrue(self.runtime.gui_qualified({"processStarted": True, "windowObserved": True, "screenshotObserved": True}))


if __name__ == "__main__":
    unittest.main()
