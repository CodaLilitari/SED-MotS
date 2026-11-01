# Blender tests for the JK level importer: scene, things, materials, cameras, renders (bpy + Blender-Sith).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Run with Blender's Python and Blender-Sith:

    BLENDER_SITH_PATH=/path/to/blender-sith <python with bpy> -B -m unittest tools/blender/tests/test_bpy_import.py -v

Every test is skipped when bpy or Blender-Sith is unavailable. All data is synthetic.
"""
import json
import math
import os
import subprocess
import sys
import tempfile
import unittest
import warnings
from pathlib import Path

TESTS = Path(__file__).resolve().parent
if str(TESTS) not in sys.path:
    sys.path.insert(0, str(TESTS))

import fixtures  # noqa: E402

try:
    import bpy
except ImportError:  # pragma: no cover - plain Python
    bpy = None

SITH = os.environ.get("BLENDER_SITH_PATH", "")
SKIP = None
if bpy is None:
    SKIP = "bpy is not available"
elif not (SITH and (Path(SITH) / "sith" / "__init__.py").is_file()):
    SKIP = "BLENDER_SITH_PATH does not point at a folder containing Blender-Sith's sith/"

PREVIEW = TESTS.parent / "jkl_preview.py"
FULL = (200, 100, 40)          # palette index 1
DARK = (10, 20, 30)            # palette index 2
GREEN565 = 0x07E0


def _surface(mat, corners, light=3, intensity=0.8, extra=0.0, adjoin=-1, flags=0x4, geo=4):
    pairs = " ".join(f"{v},{k}" for k, v in enumerate(corners))
    lights = " ".join(f"{intensity:.6f}" for _ in corners)
    return f"{mat} {flags:#x} 0x0 {geo} {light} 1 {adjoin} {extra:.6f} {len(corners)} {pairs} {lights}"


def build_level_gob(path: Path):
    """Two rooms side by side with a visible adjoin pair, one reversed wall, three things, a player."""
    vertices = fixtures.box_room(0, 0, 0, 2, 2, 1) + [(4, 0, 0), (4, 2, 0), (4, 0, 1), (4, 2, 1)]
    room_a = [0, 1, 2, 3, 4, 5, 6, 7]
    room_b = [1, 8, 9, 2, 5, 10, 11, 6]
    surfaces, normals = [], []
    for room, mat, floor_light, floor_intensity in ((room_a, 0, 0, 0.0), (room_b, 2, 3, 0.5)):
        for face, (corners, normal) in enumerate(fixtures.ROOM_FACES):
            mapped = [room[c] for c in corners]
            kwargs = {}
            if face == 0:
                kwargs = {"light": floor_light, "intensity": floor_intensity}
            if room is room_a and face == 3:            # east wall of A: visible adjoin into B
                surfaces.append(_surface(1, mapped, adjoin=0, flags=0x4))
            elif room is room_b and face == 5:          # west wall of B: its mirror
                surfaces.append(_surface(1, mapped, adjoin=1, flags=0x4))
            elif room is room_b and face == 4:          # north wall of B stored with reversed corners
                surfaces.append(_surface(mat, list(reversed(mapped))))
            else:
                surfaces.append(_surface(mat, mapped, **kwargs))
            normals.append(normal)
    templates = ["_thing none type=cog move=none model3d=Cube.3do",
                 "crate _thing", "ghostcrate _thing thingflags=0x10",
                 "walkplayer none type=player model3d=cube.3do"]
    things = ["walkplayer start 1.0 1.0 0.5 0 0 0 0",
              "crate c1 0.5 1.5 0.25 30 45 60 0",
              "crate c2 3.5 0.5 0.25 0 90 0 1",
              "ghostcrate h1 3.5 1.5 0.25 0 0 0 1",
              "crate h2 0.5 0.5 0.25 0 0 0 0 thingflags=0x10"]
    text = fixtures.jkl_text(materials=["Brick.mat", "glass.mat", "BRICK.MAT"], vertices=vertices,
                             texverts=[(0, 0), (2, 0), (2, 2), (0, 2)], surfaces=surfaces, normals=normals,
                             adjoins=["0x7 1 0.000000", "0x7 0 0.000000"],
                             sectors=[["FLAGS 0x0", "AMBIENT LIGHT 0.25", "EXTRA LIGHT 0.00", "COLORMAP 0", "SURFACES 0 6"],
                                      ["FLAGS 0x0", "AMBIENT LIGHT 0.50", "EXTRA LIGHT 0.00", "COLORMAP 0", "SURFACES 6 6"]],
                             templates=templates, things=things, material_capacity=64, thing_capacity=32)
    brick = fixtures.mat_bytes(fixtures.INDEXED, 0, fixtures.Texture(2, 2, 0, [bytes([1, 1, 1, 1])]))
    glass = fixtures.mat_bytes(fixtures.RGB565, 0, fixtures.Texture(2, 2, 0, [fixtures.u16(*[GREEN565] * 4)]))
    cube = fixtures.mat_bytes(fixtures.INDEXED, 0, fixtures.Texture(2, 2, 0, [bytes([2, 2, 2, 2])]))
    palette = fixtures.cmp_bytes({0: (255, 0, 255), 1: FULL, 2: DARK})
    return fixtures.write_gob(path, [("jkl\\twin.jkl", text.encode("latin-1")), ("mat\\brick.mat", brick),
                                     ("mat\\Glass.mat", glass), ("3do\\mat\\cube.mat", cube),
                                     ("misc\\cmp\\dflt.cmp", palette),
                                     ("3do\\cube.3do", fixtures.cube_3do("Cube.mat").encode("ascii"))])


@unittest.skipIf(SKIP, SKIP or "")
class ImporterTests(unittest.TestCase):
    def setUp(self):
        warnings.simplefilter("ignore", ResourceWarning)     # Blender-Sith leaves MAT/3DO files to the GC
        warnings.simplefilter("ignore", DeprecationWarning)   # and still sets Material.use_nodes

    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temp.name)
        cls.gob = build_level_gob(cls.root / "twin.gob")

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def build(self, light="flat", **overrides):
        from jk_level_importer import build, jkl, resources, sith_bridge
        bpy.ops.wm.read_factory_settings(use_empty=True)
        sith = sith_bridge.load(SITH)
        level_input = resources.open_level("twin.jkl", [self.gob])
        level = jkl.parse_bytes(level_input.data, level_input.stem)
        self.cache = tempfile.TemporaryDirectory()
        self.addCleanup(self.cache.cleanup)
        resolver = resources.Resolver(level_input.sources, self.cache.name)
        options = build.BuildOptions(light=light, width=320, height=240, **overrides)
        builder = build.LevelBuilder(level, resolver, sith, options, log=lambda text: None)
        return builder.build()

    def test_world_counts_adjoins_and_winding(self):
        builder = self.build()
        counts = builder.counts
        self.assertEqual((counts["surfaces"], counts["drawn"], counts["windingFlipped"]), (12, 12, 1))
        mesh = builder.world_object.data
        ids = [0] * len(mesh.polygons)
        mesh.attributes["jk_surface"].data.foreach_get("value", ids)
        self.assertEqual(sorted(ids), list(range(12)))
        level = builder.level
        for polygon, surface_id in zip(mesh.polygons, ids):
            stored = level.surfaces[surface_id].normal
            self.assertGreater(sum(a * b for a, b in zip(polygon.normal, stored)), 0.99, surface_id)
        pair = [mesh.polygons[ids.index(3)], mesh.polygons[ids.index(11)]]
        self.assertAlmostEqual(pair[0].center.x, 2.0, places=6)
        self.assertAlmostEqual(pair[1].center.x, 2.0, places=6)
        self.assertLess(pair[0].normal.dot(pair[1].normal), -0.99)
        self.assertEqual(len(mesh.vertices), sum(len(p.vertices) for p in mesh.polygons))   # per-corner vertices
        self.assertEqual(tuple(builder.world_object.color), (0.0, 0.0, 0.0, 1.0))

    def test_one_material_per_mat_and_true_colour(self):
        builder = self.build()
        keys = sorted(m["jk_mat"] for m in bpy.data.materials if "jk_mat" in m)
        self.assertEqual(keys, ["brick.mat", "cube.mat", "glass.mat"])
        self.assertNotIn("BRICK.MAT", bpy.data.materials)
        self.assertEqual(builder.counts["materials"], 3)
        self.assertEqual(builder.counts["materialsTrueColor"], 1)
        self.assertEqual(builder.master["name"], "dflt.cmp")
        brick = builder.materials["brick.mat"]
        image = next(n.image for n in brick.node_tree.nodes if n.type == "TEX_IMAGE")
        self.assertEqual([round(v * 255) for v in image.pixels[:4]], [*FULL, 255])
        glass = builder.materials["glass.mat"]
        image = next(n.image for n in glass.node_tree.nodes if n.type == "TEX_IMAGE")
        self.assertEqual([round(v * 255) for v in image.pixels[:4]], [0, 255, 0, 255])
        for obj in builder.prototypes["cube.3do"].objects:
            if obj.type == "MESH":
                self.assertEqual([m.name for m in obj.data.materials], [builder.materials["cube.mat"].name])

    def test_things_orientation_and_skips(self):
        from jk_level_importer import orient
        builder = self.build()
        counts = builder.counts
        self.assertEqual((counts["things"], counts["thingsPlaced"], counts["thingsSkippedPlayer"],
                          counts["thingsSkippedHidden"], counts["prototypes"]), (5, 2, 1, 2, 1))
        bpy.context.view_layer.update()
        empty = bpy.data.objects["JK thing 1 c1"]
        self.assertEqual(empty.rotation_mode, "YXZ")
        expected = orient.world_matrix((0.5, 1.5, 0.25), (30, 45, 60))
        for i in range(4):
            for j in range(4):
                self.assertAlmostEqual(empty.matrix_world[i][j], expected[i][j], places=6)
        rvec = [empty.matrix_world[i][0] for i in range(3)]
        self.assertAlmostEqual(rvec[1], 0.659739608441, places=6)
        self.assertEqual(empty.instance_type, "COLLECTION")
        proto = builder.prototypes["cube.3do"]
        self.assertEqual(tuple(proto.base.location), (0.0, 0.0, 0.0))          # insert offset not applied
        self.assertNotIn(proto.collection.name, [c.name for c in bpy.context.scene.collection.children_recursive])

    def test_baked_light_attributes_and_copies(self):
        builder = self.build(light="baked")
        mesh = builder.world_object.data
        values = [0.0] * (len(mesh.loops) * 4)
        mesh.color_attributes["jk_light"].data.foreach_get("color", values)
        ids = [0] * len(mesh.polygons)
        mesh.attributes["jk_surface"].data.foreach_get("value", ids)
        floor_a = mesh.polygons[ids.index(0)]
        floor_b = mesh.polygons[ids.index(6)]
        self.assertEqual(values[floor_a.loop_start * 4], 1.0)                 # light mode 0
        self.assertAlmostEqual(values[floor_b.loop_start * 4], 0.5, places=6)  # 0.5 intensity, no extra
        copies = [o for o in bpy.data.objects if o.parent and o.parent.name == "JK thing 2 c2"]
        self.assertTrue(copies)
        self.assertAlmostEqual(copies[0].color[0], 0.5, places=6)               # sector 1 ambient + extra
        bpy.context.view_layer.update()
        self.assertTrue(all((c.matrix_world.translation - bpy.data.objects["JK thing 2 c2"].matrix_world.translation).length < 1e-6
                            for c in copies if c.type == "EMPTY"))
        # The hierarchy keeps Blender-Sith's node transforms: node 1 sits 1 unit above node 0, turned 30 degrees.
        from jk_level_importer import orient
        def ancestors(obj):
            while obj.parent is not None:
                obj = obj.parent
                yield obj.name
        top = next(c for c in bpy.data.objects if c.name.startswith("no1_top") and "JK thing 2 c2" in ancestors(c))
        thing = orient.world_matrix((3.5, 0.5, 0.25), (0, 90, 0))
        child = orient.world_matrix((0.0, 0.0, 1.0), (0, 30, 0))
        expected = [[sum(thing[i][k] * child[k][j] for k in range(4)) for j in range(4)] for i in range(4)]
        for i in range(3):
            for j in range(4):
                self.assertAlmostEqual(top.matrix_world[i][j], expected[i][j], places=5)
        cube_mesh = next(o.data for o in builder.prototypes["cube.3do"].objects if o.type == "MESH")
        model_values = [0.0] * (len(cube_mesh.loops) * 4)
        cube_mesh.color_attributes["jk_light"].data.foreach_get("color", model_values)
        self.assertAlmostEqual(model_values[0], 0.25, places=6)                 # vertex intensity 0.25, Gouraud

    def test_cameras_follow_wren_framing(self):
        from jk_level_importer import framing
        builder = self.build()
        box = builder.box
        self.assertEqual((box.min, box.max), ((0.0, 0.0, 0.0), (4.0, 2.0, 1.0)))
        overhead = framing.frame(box, "overhead", 320, 240)
        cam = bpy.data.objects["JK overhead"]
        self.assertEqual(cam.data.type, "ORTHO")
        self.assertAlmostEqual(cam.data.ortho_scale, overhead.blender_ortho_scale, places=6)
        self.assertAlmostEqual(cam.data.clip_start, overhead.near, places=6)
        self.assertAlmostEqual(cam.data.clip_end, overhead.far, places=5)
        self.assertEqual(tuple(round(v, 6) for v in cam.location), tuple(round(v, 6) for v in overhead.position))
        three = framing.frame(box, "threequarter", 320, 240)
        cam = bpy.data.objects["JK threequarter"]
        self.assertEqual((cam.data.lens, cam.data.sensor_width, cam.data.sensor_fit), (35.0, 36.0, "AUTO"))
        bpy.context.view_layer.update()
        forward = -cam.matrix_world.col[2].xyz
        direction = [t - p for t, p in zip(three.target, three.position)]
        length = math.sqrt(sum(d * d for d in direction))
        for f, d in zip(forward, direction):
            self.assertAlmostEqual(f, d / length, places=5)
        projection = cam.calc_matrix_camera(bpy.context.evaluated_depsgraph_get(), x=320, y=240)
        self.assertAlmostEqual(math.degrees(2 * math.atan(1 / projection[1][1])), three.fov_y_degrees, places=4)

    def test_operator_imports_from_a_gob(self):
        import jk_level_importer
        jk_level_importer.register()
        self.addCleanup(jk_level_importer.unregister)
        bpy.ops.wm.read_factory_settings(use_empty=True)
        os.environ.setdefault("BLENDER_SITH_PATH", SITH)
        result = bpy.ops.import_scene.jk_level(filepath=str(self.gob), light="BAKED")
        self.assertEqual(result, {"FINISHED"})
        self.assertIn("JK level twin", bpy.data.objects)
        self.assertIn("JK overhead", bpy.data.objects)

    def run_cli(self, out, *extra):
        env = dict(os.environ, BLENDER_SITH_PATH=SITH)
        command = [sys.executable, "-B", str(PREVIEW), "--jkl", "twin.jkl", "--archive", str(self.gob), "--out", str(out),
                   "--width", "320", "--height", "240", *extra]
        return subprocess.run(command, env=env, capture_output=True, text=True, timeout=600)

    def test_cli_flat_and_baked_renders(self):
        out = self.root / "cli"
        flat = self.run_cli(out)
        self.assertEqual(flat.returncode, 0, flat.stdout + flat.stderr)
        report = json.loads((out / "twin_preview.json").read_text())
        self.assertEqual([v["name"] for v in report["views"]], ["overhead", "threequarter"])
        self.assertTrue(all(Path(v["file"]).is_file() and v["nonBackgroundFraction"] > 0.05 for v in report["views"]))
        self.assertIn("Wren", report["credits"])
        again = self.run_cli(out)
        self.assertEqual(again.returncode, 2)                                   # create-only
        self.assertIn("refusing to overwrite", again.stdout)
        first = [v["sha256"] for v in report["views"]]
        repeat = self.run_cli(out, "--overwrite")
        self.assertEqual(repeat.returncode, 0, repeat.stdout + repeat.stderr)
        report = json.loads((out / "twin_preview.json").read_text())
        self.assertEqual([v["sha256"] for v in report["views"]], first)         # no date or time in the PNGs
        for engine in ("auto", "cycles"):
            with self.subTest(engine=engine):
                self.check_baked_pixels(self.root / f"cli-baked-{engine}", engine)

    def check_baked_pixels(self, baked_out, engine):
        blend = baked_out / "twin.blend"
        baked = self.run_cli(baked_out, "--light", "baked", "--views", "overhead", "--engine", engine, "--blend", str(blend))
        self.assertEqual(baked.returncode, 0, baked.stdout + baked.stderr)
        report = json.loads((baked_out / "twin_preview.json").read_text())
        self.assertEqual(report["engine"], "CYCLES" if engine == "cycles" else "BLENDER_EEVEE")
        self.assertTrue(blend.is_file())
        image = bpy.data.images.load(str(baked_out / "twin_overhead.png"))
        width, height = image.size
        pixels = list(image.pixels[:])

        def at(x, y):   # world x, y to the PNG's pixel (Blender rows start at the bottom)
            view = report["views"][0]
            scale = view["orthographicHeight"] / height
            px = int(round(width / 2 + (x - view["target"][0]) / scale))
            py = int(round(height / 2 + (y - view["target"][1]) / scale))
            i = (py * width + px) * 4
            return [round(v * 255) for v in pixels[i:i + 3]]
        for value, expected in zip(at(1.5, 0.5), FULL):         # fully lit floor of room A
            self.assertLessEqual(abs(value - expected), 1)
        for value, expected in zip(at(2.5, 1.5), (100, 50, 20)):  # L = 0.5 floor of room B
            self.assertLessEqual(abs(value - expected), 1)

    def test_extension_install_registers_the_operator(self):
        import zipfile
        out = self.root / "extension"
        result = subprocess.run([sys.executable, "-B", str(TESTS.parent / "build_extension.py"), "--out", str(out)],
                                capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stderr)
        ours = next(out.glob("jk_level_importer-*.zip"))
        self.assertIn("blender_manifest.toml", zipfile.ZipFile(ours).namelist())
        sith_zip = out / "sith-copy.zip"          # an unmodified copy of Coda's package, zipped for the install
        with zipfile.ZipFile(sith_zip, "w") as archive:
            for path in sorted((Path(SITH) / "sith").rglob("*")):
                if path.is_file() and "__pycache__" not in path.parts:
                    archive.write(path, path.relative_to(Path(SITH) / "sith").as_posix())
        script = ("import bpy, sys\n"
                  "for p in sys.argv[-2:]:\n"
                  "    assert bpy.ops.extensions.package_install_files(filepath=p, repo='user_default', enable_on_install=True) == {'FINISHED'}\n"
                  "assert bpy.ops.import_scene.jk_level(filepath=sys.argv[-3]) == {'FINISHED'}\n"
                  "print('IMPORTED', 'JK level twin' in bpy.data.objects)\n")
        env = {k: v for k, v in os.environ.items() if k != "BLENDER_SITH_PATH"}
        env["BLENDER_USER_RESOURCES"] = str(self.root / "blender-user")
        result = subprocess.run([sys.executable, "-B", "-c", script, str(self.gob), str(sith_zip), str(ours)],
                                env=env, capture_output=True, text=True, timeout=600)
        self.assertIn("IMPORTED True", result.stdout, result.stdout[-2000:] + result.stderr[-2000:])

    def test_cli_cache_stays_out_of_the_level_sources(self):
        # A loose level laid out like a GOB: a --cache inside or over it would overwrite the author's MATs.
        from jk_level_importer.gob import GobArchive
        level = self.root / "loose-level"
        archive = GobArchive(self.gob)
        for key in archive.names():
            target = level / key
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.read(key))
        before = {p: p.read_bytes() for p in level.rglob("*") if p.is_file()}
        env = dict(os.environ, BLENDER_SITH_PATH=SITH)

        def preview(cache, out):
            command = [sys.executable, "-B", str(PREVIEW), "--jkl", str(level / "jkl" / "twin.jkl"), "--out", str(out),
                       "--cache", str(cache), "--views", "overhead", "--width", "64", "--height", "48"]
            return subprocess.run(command, env=env, capture_output=True, text=True, timeout=600)
        for n, cache in enumerate((level, level / "cache", level / "mat")):
            with self.subTest(cache=str(cache)):
                result = preview(cache, self.root / f"cache-refused-{n}")
                self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
                self.assertIn("--cache", result.stdout)
                self.assertFalse((self.root / f"cache-refused-{n}").exists())
        self.assertEqual({p: p.read_bytes() for p in level.rglob("*") if p.is_file()}, before)
        kept = self.root / "kept-cache"
        result = preview(kept, self.root / "cache-ok")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((kept / "mat" / "brick.mat").is_file())
        self.assertEqual({p: p.read_bytes() for p in level.rglob("*") if p.is_file()}, before)
        again = preview(kept, self.root / "cache-again")
        self.assertEqual(again.returncode, 2, again.stdout + again.stderr)      # a used cache is not reused
        self.assertIn("not empty", again.stdout)

    def test_cli_strict_and_missing_resources(self):
        out = self.root / "strict"
        result = self.run_cli(out, "--strict", "--no-things", "--views", "overhead")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        broken = fixtures.write_gob(self.root / "broken.gob", [("jkl\\lonely.jkl", fixtures.jkl_text(
            materials=["nowhere.mat"], vertices=fixtures.box_room(0, 0, 0, 1, 1, 1), texverts=[(0, 0)],
            surfaces=["0 0x4 0x0 4 3 1 -1 0.0 4 0,0 1,0 2,0 3,0 1 1 1 1"], normals=[(0, 0, 1)],
            sectors=[["SURFACES 0 1"]]).encode("latin-1"))])
        env = dict(os.environ, BLENDER_SITH_PATH=SITH)
        command = [sys.executable, "-B", str(PREVIEW), "--jkl", "lonely.jkl", "--archive", str(broken),
                   "--out", str(self.root / "strict2"), "--strict", "--views", "overhead", "--width", "64", "--height", "48"]
        result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=600)
        self.assertEqual(result.returncode, 4, result.stdout + result.stderr)
        report = json.loads((self.root / "strict2" / "lonely_preview.json").read_text())
        self.assertIn("missing MAT nowhere.mat", report["strictFailures"])
        self.assertTrue(any("No master palette" in p for p in report["masterColormap"]["problems"]))


if __name__ == "__main__":
    unittest.main()
