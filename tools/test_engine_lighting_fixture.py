"""Pure original-fixture tests; no engine or real sed-agent processes.

Run: python -m unittest discover -s tools -p test_engine_lighting_fixture.py -v
The real agent preparation gate is separately executed by new_engine_lighting_fixture.py.
"""
import json
import math
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

import new_engine_lighting_fixture as fixture


class EngineLightingFixtureTests(unittest.TestCase):
    def test_textured_material_has_engine_texinfo_extension_and_exact_pixels(self):
        pixels = bytes((255, 192, 128, 224))
        data = fixture.material(pixels)
        self.assertEqual(data[:4], b"MAT ")
        self.assertEqual(struct.unpack_from("<6I", data, 4), (0x32, 2, 1, 1, 0, 8))
        self.assertEqual(struct.unpack_from("<I", data, 76)[0], 8)
        self.assertEqual(struct.unpack_from("<6I", data, 116), (2, 2, 0, 0, 0, 1))
        self.assertEqual(data[140:], pixels)

    def test_palette_panel_is_neutral_room_is_colored_and_full_ramps_are_present(self):
        data = fixture.colormap()
        self.assertEqual(len(data), 64 + 768 + 64 * 256)
        for index in (128, 192, 224, 255):
            self.assertEqual(data[64 + 3 * index:67 + 3 * index], bytes([index] * 3))
            self.assertEqual(data[832 + 63 * 256 + index], index)
        self.assertEqual(data[814:817], bytes((40, 100, 70)))

    def test_gob_manifest_resolves_exact_original_bytes(self):
        entries = {"3do/archpanel.3do": fixture.model(), "mat/archpanel.mat": fixture.material(bytes([255] * 4)),
                   "3do/archspawn.3do": fixture.spawn_model()}
        archive = fixture.gob(entries)
        self.assertEqual(struct.unpack_from("<III", archive, 4), (0x14, 12, 3))
        for index, (name, data) in enumerate(entries.items()):
            offset, length, field = struct.unpack_from("<II128s", archive, 16 + index * 136)
            self.assertEqual(field.rstrip(b"\0").decode(), name.replace("/", "\\"))
            self.assertEqual(archive[offset:offset + length], data)

    def test_model_fixed_order_sections_radius_and_hierarchy_are_engine_compatible(self):
        lines = fixture.model().decode().splitlines()
        self.assertEqual(lines[:4], ["SECTION: HEADER", "3DO 2.1", "SECTION: MODELRESOURCE", "MATERIALS 1"])
        self.assertNotIn("END", lines)
        self.assertEqual(lines[lines.index("NAME original_asymmetric_panel") + 1], "RADIUS 2")
        self.assertEqual(len(lines[-1].split()), 18)
        self.assertEqual(lines[-1].split()[1:8], ["0x0", "0x10", "0", "-1", "-1", "-1", "0"])

    def test_runtime_seed_has_full_header_sector_bounds_player_and_spare_slots(self):
        seed = fixture.seed_level().decode()
        header = seed.split("SECTION: HEADER\n", 1)[1].split("\nEND", 1)[0].splitlines()
        self.assertEqual(len(header), 11)
        self.assertEqual(header[1], "WORLD GRAVITY 0")
        self.assertIn("BOUNDBOX -2 -2 0 2 2 3\nCENTER 0 0 1.5\nRADIUS 4", seed)
        self.assertIn("World things 512", seed)
        self.assertIn("sedarchspawn none type=player move=none collide=0", seed)
        self.assertIn("1: sedarchspawn OriginalSpawn 1.5 -1.5 1.5 0 0 0 0 -1", seed)
        self.assertNotIn("cog=", seed)

    def test_player_uses_separate_original_model_and_explicit_external_soundclass(self):
        seed = fixture.seed_level().decode()
        spawn = fixture.spawn_model().decode()
        template = next(line for line in seed.splitlines() if line.startswith("sedarchspawn "))
        self.assertIn("model3d=archspawn.3do soundclass=ky.snd", template)
        self.assertNotIn("thingflags=0x8", template)
        self.assertIn("0: archpanel.3do\n1: archspawn.3do\nEND", seed)
        self.assertIn("SECTION: SOUNDCLASS\nWorld soundclasses 512\n0: ky.snd\nEND", seed)
        self.assertIn("NAME original_off_camera_spawn\nRADIUS 0.03", spawn)
        self.assertIn("0: archroom.mat", spawn)
        self.assertNotEqual(fixture.model(), fixture.spawn_model())
        points = spawn.split("VERTICES 4\n", 1)[1].split("TEXTURE VERTICES", 1)[0].splitlines()
        actual = [tuple(map(float, point.split()[1:4])) for point in points]
        self.assertEqual(actual, list(fixture.SPAWN_VERTICES))
        # Independent frustum check: this yaw-zero camera has world X as right.
        camera = fixture.camera()
        direction = [b - a for a, b in zip(camera["position"], camera["target"])]
        length = math.sqrt(sum(value * value for value in direction))
        forward = [value / length for value in direction]
        for point in actual:
            relative = [local + offset - eye for local, offset, eye in zip(point, fixture.SPAWN_POSITION, camera["position"])]
            depth = sum(a * b for a, b in zip(relative, forward))
            self.assertGreater(depth, 0)
            self.assertGreater(abs(relative[0]), depth * math.tan(math.radians(camera["horizontalFov"]) / 2))

    def test_copyright_section_has_exact_original_rows_required_by_qol_engine(self):
        seed = fixture.seed_level()
        rows = seed.split(b"SECTION: COPYRIGHT\n", 1)[1].split(b"\nEND\n", 1)[0].splitlines()
        self.assertEqual(len(rows), 34)
        self.assertTrue(all(len(row) == 32 and row.startswith(b"SED ORIGINAL FIXTURE ROW ") for row in rows))
        self.assertEqual(len(set(rows)), 34)
        self.assertNotIn(b"LucasArts", b"".join(rows))
        self.assertLess(seed.index(b"SECTION: COPYRIGHT"), seed.index(b"SECTION: HEADER"))

    def test_bootstrap_capacities_are_not_populated_counts_and_shrinkage_refuses(self):
        seed = fixture.seed_level()
        capacities = fixture.assert_bootstrap_capacities(seed)
        self.assertEqual(len(capacities), 12)
        self.assertEqual(set(capacities.values()), {512})
        for section in ("SOUNDS", "KEYFRAMES", "ANIMCLASS", "AICLASS", "COGSCRIPTS", "COGS", "SPRITES"):
            block = seed.split(b"SECTION: " + section.encode() + b"\n", 1)[1].split(b"\nEND", 1)[0]
            self.assertEqual(block.splitlines(), [f"World {fixture.BOOTSTRAP_CAPACITIES[section]} 512".encode()])
        for declaration in (b"World models 512", b"World materials 512", b"World scripts 512", b"World things 512"):
            with self.subTest(declaration=declaration), self.assertRaises(ValueError):
                fixture.assert_bootstrap_capacities(seed.replace(declaration, declaration.rsplit(b" ", 1)[0] + b" 1"))

    def test_independent_first_sample_and_channel_contrast(self):
        baked = fixture.expected_bake(0.8, 0.4)
        swapped = fixture.expected_bake(0.8, 0.4, (0.25, 0.5, 1))
        white_zero = fixture.expected_bake(0, 0.4)
        # First vertex is exactly one unit below the source, range3.
        self.assertAlmostEqual(baked[0]["mono"], 0.8 * 4 / 9)
        self.assertAlmostEqual(baked[0]["rgb"][0], 0.4 * 4 / 9)
        self.assertGreater(len({s["mono"] for s in baked}), 1)
        for original, swap, white in zip(baked, swapped, white_zero):
            self.assertEqual(original["mono"], swap["mono"])
            self.assertEqual(original["rgb"], list(reversed(swap["rgb"])))
            self.assertEqual(original["rgb"], white["rgb"])
            self.assertEqual(white["mono"], 0)

    def test_sample_readback_rejects_channel_collapse_and_nan(self):
        wanted = fixture.expected_bake(0.8, 0.4)
        fixture.check_samples(wanted, wanted)
        wrong = json.loads(json.dumps(wanted))
        wrong[0]["rgb"][0] = wrong[0]["mono"]
        with self.assertRaises(ValueError):
            fixture.check_samples(wrong, wanted)
        wrong[0]["mono"] = math.nan
        with self.assertRaises(ValueError):
            fixture.check_samples(wrong, wanted)

    def test_unbound_control_rejects_live_zero_arch_and_residual_provenance(self):
        thing = {"archLightingIndex": -1, "architecture": {"status": "unbound", "provenance": None,
                 "samplesSha256": None, "meshVertexCounts": None}}
        fixture.assert_unbound_readback(thing)
        zero_arch = {"archLightingIndex": 0, "architecture": {"status": "reset", "provenance": {"origin": "reset"},
                     "samplesSha256": "0" * 64, "meshVertexCounts": [4]}}
        with self.assertRaisesRegex(ValueError, "zero ARCH is not unbound"):
            fixture.assert_unbound_readback(zero_arch)
        for field, value in (("provenance", {"origin": "bound"}), ("samplesSha256", "f" * 64), ("meshVertexCounts", [4])):
            wrong = json.loads(json.dumps(thing))
            wrong["architecture"][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                fixture.assert_unbound_readback(wrong)

    def test_extension_loss_control_preserves_all_other_bytes_and_records_removed_bytes(self):
        engine = fixture.seed_level()
        source = engine + b"SECTION: SED_SOURCE_LIGHTS\nVERSION 1\nEND\n\nSECTION: SED_ARCH_AUTHORING\nVERSION 1\nEND\n"
        derived, removed = fixture.omit_extensions(source)
        self.assertEqual(derived, engine + b"\n")
        for section in removed:
            data = bytes.fromhex(section["dataHex"])
            self.assertEqual(source[section["offset"]:section["offset"] + section["bytes"]], data)
            self.assertEqual(fixture.sha(data), section["sha256"])
        self.assertEqual([section["name"] for section in removed], list(fixture.EXTENSIONS))

    def test_extension_loss_control_refuses_duplicate_or_missing_terminator(self):
        good = b"SECTION: SED_SOURCE_LIGHTS\nEND\nSECTION: SED_ARCH_AUTHORING\nEND\n"
        for source in (good + b"SECTION: SED_SOURCE_LIGHTS\nEND\n", good.replace(b"LIGHTS\nEND", b"LIGHTS", 1)):
            with self.assertRaises(ValueError):
                fixture.omit_extensions(source)

    def test_interleaved_control_keeps_later_things_arch_and_exact_extension_bytes(self):
        source = fixture.seed_level() + b"SECTION: SED_SOURCE_LIGHTS\nVERSION 1\nEND\nSECTION: SED_ARCH_AUTHORING\nVERSION 1\nEND\n"
        derived, moved = fixture.interleave_extensions(source)
        self.assertLess(derived.index(b"SECTION: SED_SOURCE_LIGHTS"), derived.index(b"SECTION: THINGS"))
        self.assertLess(derived.index(b"SECTION: SED_ARCH_AUTHORING"), derived.index(b"SECTION: ARCHLIGHTING"))
        self.assertEqual(fixture.known_section_hashes(source), fixture.known_section_hashes(derived))
        for section in moved:
            self.assertEqual(derived.count(bytes.fromhex(section["dataHex"])), 1)

    def test_interleaved_writer_blank_lines_lowercase_end_and_final_eof_without_newline(self):
        # Real writer output uses blank lines, lowercase ordinary end markers,
        # and no final newline after the last extension's END.
        engine = fixture.seed_level().replace(b"SECTION: THINGS\n", b"SECTION: THINGS\n\n")
        engine = engine.replace(b"\nEND\n", b"\nend\n\n")
        source = engine + b"\nSECTION: SED_SOURCE_LIGHTS\nVERSION 1\nEND\n\nSECTION: SED_ARCH_AUTHORING\nVERSION 1\nEND"
        derived, moved = fixture.interleave_extensions(source)
        self.assertEqual(fixture.known_section_hashes(derived), fixture.known_section_hashes(source))
        self.assertIn(b"END\nSECTION: THINGS\n\n", derived)
        self.assertNotIn(b"ENDSECTION", derived)
        self.assertEqual(moved[-1]["insertedSeparatorAfterHex"], "0a")
        self.assertTrue(bytes.fromhex(moved[-1]["dataHex"]).endswith(b"END"))

    def test_stage_is_exact_allowlist_and_uses_engine_episode_layout(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            case = {"name": "baked"}
            fixture.stage_case(case, b"original-level", {"3do/archpanel.3do": fixture.model()}, root)
            self.assertEqual(len(case["files"]), 3)
            for item in case["files"]:
                self.assertEqual(fixture.sha(Path(item["source"]).read_bytes()), item["sha256"])
                self.assertTrue(item["destination"].startswith("episode/SEDARCH/"))
            self.assertEqual(Path(case["levelPath"]).read_bytes(), b"original-level")
            with self.assertRaises(FileExistsError):
                fixture.stage_case(case, b"replacement", {}, root)
            self.assertEqual(Path(case["levelPath"]).read_bytes(), b"original-level")

    def test_existing_output_refuses_before_agent_start_or_file_replacement(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            executable = root / "agent.exe"
            executable.write_bytes(b"not executed")
            with patch.object(fixture.subprocess, "run") as run:
                with self.assertRaises(FileExistsError):
                    fixture.generate(executable, root)
                run.assert_not_called()
            self.assertEqual(executable.read_bytes(), b"not executed")

    def test_failed_agent_keeps_failure_receipt_without_staging_runtime_case(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            executable = root / "agent.exe"
            executable.write_bytes(b"not executed")
            with patch.object(fixture.Agent, "invoke", side_effect=RuntimeError("original failure")):
                with self.assertRaisesRegex(RuntimeError, "original failure"):
                    fixture.generate(executable, root / "attempt")
            receipt = json.loads((root / "attempt/fixture-receipt.json").read_bytes())
            self.assertEqual(receipt["status"], "failed")
            self.assertFalse(receipt["engineLaunched"])
            self.assertEqual(receipt["cases"], [])

    def test_camera_meets_engine_minimum_and_preserves_vertical_horizontal_projection(self):
        camera = fixture.camera()
        self.assertEqual((camera["width"], camera["height"]), (640, 480))
        recovered_vertical = math.degrees(2 * math.atan(math.tan(math.radians(camera["horizontalFov"]) / 2) / (640 / 480)))
        self.assertAlmostEqual(recovered_vertical, camera["fieldOfViewDegrees"])
        self.assertAlmostEqual(camera["pitchYawRoll"][1], 0)
        self.assertLess(camera["pitchYawRoll"][0], 0)


if __name__ == "__main__":
    unittest.main()
