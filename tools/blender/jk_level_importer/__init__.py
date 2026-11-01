# Blender extension/add-on entry: File > Import > JK level (.jkl / .gob).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""JK Level Importer (Wren previews).

Imports a Jedi Knight / Mysteries of the Sith level from a ``.jkl`` file or from a ``.gob``/``.goo``
archive into the current scene: world surfaces with their MATs, things with their 3DO models, optional
baked light from the JKL, and Wren's two auto-framed cameras. Needs Coda's Blender-Sith fork (installed
and enabled, or ``$BLENDER_SITH_PATH``). The stdlib modules (``gob``, ``resources``, ``jkl``,
``matfile``, ``orient``, ``framing``, ``light``) import without Blender.
"""

bl_info = {
    "name": "JK Level Importer (Wren previews)",
    "author": "Red (Claude) for Andrei; finishes Wren's preview script; uses Coda's Blender-Sith",
    "version": (0, 1, 0),
    "blender": (5, 0, 0),
    "location": "File > Import > JK level (.jkl / .gob)",
    "description": "Import JK/MotS levels from .jkl or .gob; Wren-style previews",
    "category": "Import-Export",
}

try:
    import bpy
except ImportError:  # imported by the stdlib tests or tools without Blender
    bpy = None


def _split(text):
    return [part.strip() for part in (text or "").split(";") if part.strip()]


if bpy is not None:
    from bpy.props import BoolProperty, EnumProperty, StringProperty
    from bpy_extras.io_utils import ImportHelper

    class IMPORT_SCENE_OT_jk_level(bpy.types.Operator, ImportHelper):
        """Import a JK/MotS level (.jkl, or a level inside a .gob/.goo) with its MATs and 3DO things"""

        bl_idname = "import_scene.jk_level"
        bl_label = "Import JK level (.jkl / .gob)"
        bl_options = {"REGISTER", "UNDO"}

        filter_glob: StringProperty(default="*.jkl;*.gob;*.goo", options={"HIDDEN"})
        level_entry: StringProperty(
            name="Level entry",
            description="For a GOB: the JKL entry to import (empty: the only jkl/*.jkl, else the first alphabetically)")
        extra_archives: StringProperty(
            name="Extra archives",
            description="';'-separated GOB/GOO files searched after the level's own source, e.g. Res2.gob;Res1hi.gob")
        resource_dirs: StringProperty(
            name="Resource folders",
            description="';'-separated loose folders laid out like a GOB (mat/, 3do/, misc/cmp/)")
        light: EnumProperty(name="Light", default="FLAT", items=(
            ("FLAT", "Flat (Wren)", "Textures only; Wren's Workbench look"),
            ("BAKED", "Baked", "Texture times the JKL's own light, for EEVEE or Cycles with the Raw view transform")))
        sky: EnumProperty(name="Sky", default="TEXTURED", items=(
            ("TEXTURED", "Textured", "Approximate static sky UVs"), ("HIDE", "Hide", "Leave sky surfaces out")))
        include_things: BoolProperty(name="Things", default=True, description="Place things that have 3DO models")
        include_players: BoolProperty(name="Players", default=False, description="Also place player things")
        include_hidden: BoolProperty(name="Hidden things", default=False,
                                     description="Also place things the engine does not draw (thingflags 0x2, 0x10, 0x80000)")
        framing: EnumProperty(name="Framing", default="WREN", items=(
            ("WREN", "Wren", "Wren's camera distance"), ("CONTAIN", "Contain", "Back off until the whole level fits")))
        create_cameras: BoolProperty(name="Cameras", default=True, description="Add Wren's overhead and three-quarter cameras")
        clear_scene: BoolProperty(name="Clear scene", default=False, description="Delete the scene's objects first")

        def execute(self, context):
            import tempfile
            from . import build, jkl, resources, sith_bridge
            from .gob import GobError, is_archive_path
            try:
                sith = sith_bridge.load()
            except sith_bridge.SithMissing as exc:
                self.report({"ERROR"}, str(exc))
                return {"CANCELLED"}
            archives, folders = _split(self.extra_archives), _split(self.resource_dirs)
            try:
                if is_archive_path(self.filepath):
                    entries = resources.level_entries(resources.open_source(self.filepath))
                    name = self.level_entry.strip()
                    if not name:
                        if not entries:
                            self.report({"ERROR"}, "The archive contains no jkl/*.jkl level.")
                            return {"CANCELLED"}
                        name = entries[0]
                        if len(entries) > 1:
                            self.report({"INFO"}, f"Importing {name}; the archive also has: " + ", ".join(entries[1:]))
                    level_input = resources.open_level(name, archives, folders, level_archive=self.filepath)
                else:
                    level_input = resources.open_level(self.filepath, archives, folders)
            except (GobError, resources.LevelLookupError, OSError) as exc:
                self.report({"ERROR"}, str(exc))
                return {"CANCELLED"}
            if self.clear_scene:
                for obj in list(context.scene.objects):
                    bpy.data.objects.remove(obj, do_unlink=True)
            level = jkl.parse_bytes(level_input.data, level_input.stem)
            render = context.scene.render
            options = build.BuildOptions(light=self.light.lower(), sky=self.sky.lower(),
                                         include_things=self.include_things, include_players=self.include_players,
                                         include_hidden=self.include_hidden, framing=self.framing.lower(),
                                         create_cameras=self.create_cameras,
                                         width=render.resolution_x, height=render.resolution_y)
            with tempfile.TemporaryDirectory(prefix="jk_level_") as cache:
                resolver = resources.Resolver(level_input.sources, cache)
                builder = build.LevelBuilder(level, resolver, sith, options)
                try:
                    builder.build()
                except Exception as exc:
                    self.report({"ERROR"}, f"Import failed: {type(exc).__name__}: {exc}")
                    return {"CANCELLED"}
            counts = builder.counts
            self.report({"INFO"}, f"{level_input.stem}: {counts['drawn']} surfaces, {counts['thingsPlaced']} things, "
                                  f"{counts['materials']} materials ({counts['materialsMissing']} missing, "
                                  f"{counts['materialsRefused']} refused), master palette {builder.master['name']}")
            for warning in (resolver.warnings + builder.warnings)[:10]:
                self.report({"WARNING"}, warning)
            if options.light == "baked":
                self.report({"INFO"}, "Baked light: render with EEVEE or Cycles and the Raw view transform to see it exactly.")
            return {"FINISHED"}

    def _menu(self, context):
        self.layout.operator(IMPORT_SCENE_OT_jk_level.bl_idname, text="JK level (.jkl / .gob)")

    def register():
        bpy.utils.register_class(IMPORT_SCENE_OT_jk_level)
        bpy.types.TOPBAR_MT_file_import.append(_menu)

    def unregister():
        bpy.types.TOPBAR_MT_file_import.remove(_menu)
        bpy.utils.unregister_class(IMPORT_SCENE_OT_jk_level)

else:
    def register():
        raise RuntimeError("The JK level importer operator needs Blender (bpy).")

    def unregister():
        pass
