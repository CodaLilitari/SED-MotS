# Tests for the tolerant JKL parser of the JK level importer (stdlib only).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
import unittest

import fixtures
from jk_level_importer import jkl


def room_level(**overrides):
    vertices = fixtures.box_room(0, 0, 0, 2, 2, 1)
    texverts = [(0, 0), (64, 0), (64, 64), (0, 64)]
    surfaces = []
    for corners, _ in fixtures.ROOM_FACES:
        pairs = " ".join(f"{v},{k}" for k, v in enumerate(corners))
        surfaces.append(f"0 0x4 0x0 4 3 1 -1 0.000000 4 {pairs} 0.5 0.5 0.5 0.5")
    args = dict(materials=["Wall.mat"], vertices=vertices, texverts=texverts, surfaces=surfaces,
                normals=[n for _, n in fixtures.ROOM_FACES],
                sectors=[["FLAGS 0x0", "AMBIENT LIGHT 0.30", "EXTRA LIGHT 0.10", "COLORMAP 0", "TINT 0 0 0",
                          "SURFACES 0 6"]])
    args.update(overrides)
    return fixtures.jkl_text(**args)


class JklTests(unittest.TestCase):
    def test_capacity_lists_end_at_end_and_exact_lists_are_exact(self):
        level = jkl.parse(room_level(materials=["a.mat", "b.mat", "c.mat"], material_capacity=640,
                                     thing_capacity=256, template_capacity=192), "room")
        self.assertEqual([m[0] for m in level.materials], ["a.mat", "b.mat", "c.mat"])
        self.assertEqual(len(level.vertices), 8)
        self.assertEqual(len(level.texverts), 4)
        self.assertEqual(len(level.surfaces), 6)
        self.assertEqual(len(level.normals), 6)
        self.assertEqual(level.surfaces[1].normal, (0.0, 0.0, -1.0))
        self.assertEqual(level.colormaps, ["dflt.cmp"])
        self.assertEqual(level.kind, "jk")
        self.assertEqual(level.surfaces[0].verts, [0, 1, 2, 3])
        self.assertEqual(level.surfaces[0].texverts, [0, 1, 2, 3])
        self.assertEqual(level.surfaces[0].surfflags, 0x4)
        self.assertEqual(level.surfaces[0].mono, [0.5] * 4)
        self.assertEqual(level.header.ceiling_sky_z, 3.0)
        self.assertEqual(level.header.horizon_ppr, 768.0)
        self.assertEqual(level.warnings, [])

    def test_sector_surfaces_and_light(self):
        level = jkl.parse(room_level())
        sector = level.sectors[0]
        self.assertEqual((sector.ambient, sector.extra, sector.first_surface, sector.surface_count), (0.3, 0.1, 0, 6))
        self.assertTrue(all(surface.sector == 0 for surface in level.surfaces))

    def test_mots_rows_carry_mono_and_rgb(self):
        surfaces = ["0 0x4 0x0 4 3 1 -1 0.1 3 0,0 1,1 2,2 0.5 0.4 0.3 0.2 0.6 0.7 0.8 0.9 1 1 1 1"]
        level = jkl.parse(room_level(vertices=[(0, 0, 0), (1, 0, 0), (1, 1, 0)], surfaces=surfaces, normals=[(0, 0, 1)],
                                     sectors=[["SURFACES 0 1"]]))
        surface = level.surfaces[0]
        self.assertTrue(surface.has_rgb)
        self.assertEqual(level.kind, "mots")
        self.assertEqual(surface.mono, [0.5, 0.6, 1.0])
        self.assertEqual(surface.rgb, [(0.4, 0.3, 0.2), (0.7, 0.8, 0.9), (1.0, 1.0, 1.0)])
        self.assertEqual(surface.extralight, 0.1)

    def test_templates_chain_override_and_cycle(self):
        templates = ["_base none type=actor thingflags=0x400 model3d=base.3do",
                     "child _base model3d=child.3do size=0.5",
                     "loopa loopb x=1", "loopb loopa y=2",
                     "orphan missingparent z=3"]
        things = ["child one 1.0 2.0 3.0 10.0 20.0 30.0 0 thingflags=0x10",
                  "loopa two 0 0 0 0 0 0 0",
                  "orphan three 0 0 0 0 0 0 0"]
        level = jkl.parse(room_level(templates=templates, things=things))
        params, chain, problem = jkl.thing_params(level, level.things[0])
        self.assertEqual(chain, ["child", "_base"])
        self.assertIsNone(problem)
        self.assertEqual(params["model3d"], "child.3do")
        self.assertEqual(params["type"], "actor")
        self.assertEqual(params["size"], "0.5")
        self.assertEqual(jkl.thing_flags(params), 0x10)  # the thing's own value overrides the template
        self.assertEqual(level.things[0].position, (1.0, 2.0, 3.0))
        self.assertEqual(level.things[0].pyr, (10.0, 20.0, 30.0))
        _, _, problem = jkl.thing_params(level, level.things[1])
        self.assertIn("cycle", problem)
        _, chain, problem = jkl.thing_params(level, level.things[2])
        self.assertEqual(chain, ["orphan"])
        self.assertIn("missing template 'missingparent'", problem)

    def test_caesar_stray_parenthesis_is_kept_verbatim(self):
        things = ["walkplayer start 0 0 0.1 0 0 0 0",
                  "ghost cam 8.05 8.35 6.92 0 270 0 0 frame=(8.05/8.35/6.92:0/270/0)70/0) frame=(1/2/3:0/0/0)",
                  "walkplayer after 1 1 1 0 90 0 0"]
        templates = ["walkplayer none type=player model3d=ky.3do", "ghost none type=ghost"]
        level = jkl.parse(room_level(things=things, templates=templates))
        self.assertEqual(len(level.things), 3)
        self.assertEqual(level.things[1].params["frame"], "(1/2/3:0/0/0)")  # last value of a repeated key
        self.assertEqual(level.things[2].name, "after")

    def test_comments_blank_lines_and_heading_case(self):
        text = room_level().replace("World Colormaps 1", "# a comment\n\nWorld colormaps 1   # trailing")
        text = text.replace("World vertices", "WORLD VERTICES")
        level = jkl.parse(text)
        self.assertEqual(level.colormaps, ["dflt.cmp"])
        self.assertEqual(len(level.vertices), 8)

    def test_damaged_rows_warn_instead_of_raising(self):
        surfaces = ["0 0x4 0x0 4 3 1 -1 0.0 4 0,0 1,1 2,2 3,3 0.5 0.5 0.5 0.5",
                    "0 zz 0x0 4 3 1 -1 0.0 4 0,0 1,1",
                    "0 0x4 0x0 4 3 1 -1 0.0 2 0,0 1,1 0.5 0.5"]
        level = jkl.parse(room_level(surfaces=surfaces, normals=[(0, 0, 1)] * 3, sectors=[["SURFACES 0 3"]]))
        self.assertEqual(len(level.surfaces), 3)
        self.assertIsNotNone(level.surfaces[1].error)
        self.assertEqual(level.surfaces[1].geo, 0)
        self.assertEqual(level.surfaces[2].verts, [0, 1])
        self.assertTrue(any("surface 1 is unreadable" in w for w in level.warnings))

    def test_short_exact_list_warns(self):
        text = room_level().replace("World vertices 8", "World vertices 9")
        level = jkl.parse(text)
        self.assertEqual(len(level.vertices), 8)
        self.assertTrue(any("World vertices lists 9 entries but only 8" in w for w in level.warnings))

    def test_sky_flags(self):
        surfaces = ["0 0x404 0x0 4 0 1 -1 0.0 4 0,0 1,1 2,2 3,3 1 1 1 1"]
        level = jkl.parse(room_level(surfaces=surfaces, normals=[(0, 0, 1)], sectors=[["SURFACES 0 1"]]))
        self.assertTrue(level.surfaces[0].is_sky)
        self.assertEqual(jkl.parse_hex("0x80005"), 0x80005)
        self.assertEqual(jkl.parse_hex("401"), 0x401)


if __name__ == "__main__":
    unittest.main()
