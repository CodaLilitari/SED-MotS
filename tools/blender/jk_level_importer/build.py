# Blender scene construction for a JK/MotS level: world mesh, materials, things, cameras.
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Build a level the way Wren's ``render.py`` did, finished.

Kept from Wren: the world built from the JKL's own vertices, texture vertices and surfaces, invisible
surfaces skipped, things with models placed (players skipped), backface culling, Blender-Sith for
3DO/MAT/CMP. Finished here: the level's master CMP (World Colormap 0), SED's MAT rules (true colour,
first cel, retail mip fallback), exact JK orientation, the engine's winding (stored normals), per-corner
vertices so both faces of an adjoin pair survive, sky surfaces, one material per MAT, one imported
prototype per 3DO (instanced), optional baked light, and the shared Wren framing for cameras.
"""
from __future__ import annotations

import math
import re
from dataclasses import dataclass, field

import bpy
from mathutils import Vector

from . import framing, jkl, light, matfile, orient, resources
from .threedo import canonical, decode_3do, read_3do_materials, sanitize_3do

FLAT_WORLD_COLOR = (0.18, 0.2, 0.24)       # Wren's world colour
BAKED_WORLD_COLOR = (0.05, 0.05, 0.08)     # SED's clear colour (13, 13, 20)
PLACEHOLDER_GREY = 0.5
COUNT_KEYS = ("surfaces", "drawn", "skippedPortal", "skippedNoMaterial", "skippedDegenerate", "skippedBadIndex",
              "windingFlipped", "sky", "skyHidden", "materials", "materialsMissing", "materialsRefused",
              "materialsTrueColor", "materialsSolid", "mipFallbacks", "things", "thingsPlaced",
              "thingsSkippedPlayer", "thingsSkippedHidden", "thingsNoModel", "modelsMissing",
              "modelImportErrors", "prototypes")
SKY_NOTE = ("Sky surfaces use static UVs that approximate the engine's view-dependent sky: the ceiling sky is "
            "a planar projection (exact for straight-down views), the horizon sky a cylinder around the "
            "level's centre.")


DEFAULT_VIEWS = framing.VIEWS


@dataclass
class BuildOptions:
    light: str = "flat"                  # "flat" (Wren's look) or "baked"
    sky: str = "textured"                # "textured" or "hide"
    include_things: bool = True
    include_players: bool = False
    include_hidden: bool = False
    framing: str = "wren"
    margin: float = 0.025
    create_cameras: bool = True
    views: tuple = DEFAULT_VIEWS
    width: int = 1280
    height: int = 960


@dataclass
class Prototype:
    name: str
    collection: object
    base: object
    objects: list = field(default_factory=list)


def _use_nodes(idblock):
    """Node trees are always on from Blender 5; older versions need ``use_nodes`` (deprecated in 5.x)."""
    if getattr(idblock, "node_tree", None) is None:
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", DeprecationWarning)
            idblock.use_nodes = True


def _int32(value: int) -> int:
    value &= 0xFFFFFFFF
    return value - (1 << 32) if value >= (1 << 31) else value


def _newell(points):
    nx = ny = nz = 0.0
    count = len(points)
    for i in range(count):
        x0, y0, z0 = points[i]
        x1, y1, z1 = points[(i + 1) % count]
        nx += (y0 - y1) * (z0 + z1)
        ny += (z0 - z1) * (x0 + x1)
        nz += (x0 - x1) * (y0 + y1)
    return (nx, ny, nz)


class LevelBuilder:
    """Creates the Blender objects for one parsed level. Collects counts, warnings and provenance."""

    def __init__(self, level, resolver, sith, options: BuildOptions, log=print):
        self.level = level
        self.resolver = resolver
        self.sith = sith
        self.options = options
        self.log = log
        self.counts = {key: 0 for key in COUNT_KEYS}
        self.missing = {"materials": [], "models": [], "templates": []}
        self.failed_models: list[str] = []
        self.warnings: list[str] = []
        self.notes: list[str] = []
        self.materials: dict = {}
        self.mat_infos: dict = {}
        self.uv_size: dict = {}
        self.colormap = None
        self.colormap_path = ""
        self.master = {"name": None, "source": None, "entry": None, "problems": []}
        self.prototypes: dict = {}
        self.world_object = None
        self.box = None
        self.cameras: dict = {}
        self.thing_objects: list = []
        self.baked = options.light == "baked"
        scene = bpy.context.scene
        self.level_collection = self._collection(scene, "JK level")
        self.things_collection = self._collection(scene, "JK things")

    # -- helpers ------------------------------------------------------------------------------------
    @staticmethod
    def _collection(scene, name):
        collection = bpy.data.collections.new(name)
        scene.collection.children.link(collection)
        return collection

    def warn(self, text: str):
        self.warnings.append(text)
        self.log("Warning: " + text)

    # -- master palette (the same rule and texts as SED MasterColormap.Resolve) ---------------------------
    def _load_cmp(self, name: str):
        found = self.resolver.resolve("cmp", name)
        if found is None:
            return None
        try:
            path = self.resolver.extract(found)
        except resources.CacheError as exc:     # an entry name that would leave the cache
            found.note(f"not extracted: {exc}")
            self.warn(f"Colormap '{name}': {exc}")
            return None
        try:
            colormap = self.sith.ColorMap.load(str(path))
            if len(colormap.palette) != 256:
                raise ValueError("palette has fewer than 256 entries")
        except Exception as exc:  # an unreadable CMP is unavailable, as in SED
            found.note(f"unreadable colormap: {exc}")
            return None
        return found, path, colormap

    def resolve_master_colormap(self):
        names = self.level.colormaps
        name = names[0] if names and names[0].strip() else "dflt.cmp"
        problems = []
        loaded = self._load_cmp(name)
        used = name
        if loaded is None:
            if name.lower() == "dflt.cmp":
                problems.append("No master palette is available: dflt.cmp is unavailable.")
            else:
                loaded = self._load_cmp("dflt.cmp")
                if loaded is not None:
                    used = "dflt.cmp"
                    problems.append(f"Master colormap '{name}' (World Colormap 0) is unavailable; using dflt.cmp's palette instead.")
                else:
                    problems.append(f"No master palette is available: World Colormap 0 '{name}' and dflt.cmp are both unavailable.")
        if loaded is not None:
            found, path, colormap = loaded
            self.colormap, self.colormap_path = colormap, str(path)
            self.master.update(name=used, source=found.source, entry=found.entry, sha256=found.sha256)
        self.master["problems"] = problems
        for problem in problems:
            self.warn(problem)

    # -- materials ----------------------------------------------------------------------------------
    def _image_from(self, name, width, height, pixels, alpha):
        image = bpy.data.images.new(name, width, height, alpha=alpha)
        image.colorspace_settings.name = "Non-Color" if self.baked else "sRGB"
        image.pixels.foreach_set(pixels)
        image.file_format = "PNG"
        image.pack()
        return image

    def _rebuild_images(self, material, alpha):
        """Keep Blender-Sith's pixels but store them in an image that packs alpha and, for baked light, is Non-Color.

        Blender-Sith creates 24-plane images, so packing (or a colour-space change, which reloads from the packed
        PNG) drops the colour key; the rebuilt image carries the same bytes with an alpha plane.
        """
        if not (alpha or self.baked):
            return
        for node in material.node_tree.nodes:
            if node.type != "TEX_IMAGE" or node.image is None:
                continue
            old = node.image
            width, height = old.size
            pixels = [0.0] * (width * height * 4)
            old.pixels.foreach_get(pixels)
            name = old.name
            node.image = self._image_from(name + ".jk", width, height, pixels, alpha)
            if old.users == 0:
                bpy.data.images.remove(old)
            node.image.name = name

    def _placeholder(self, key, why):
        """A neutral grey stand-in (never magenta) for a missing or refused MAT."""
        material = bpy.data.materials.new(f"{key} ({why})")
        material.diffuse_color = (PLACEHOLDER_GREY, PLACEHOLDER_GREY, PLACEHOLDER_GREY, 1.0)
        _use_nodes(material)
        tree = material.node_tree
        tree.nodes.clear()
        bsdf = tree.nodes.new("ShaderNodeBsdfPrincipled")
        output = tree.nodes.new("ShaderNodeOutputMaterial")
        tree.links.new(bsdf.outputs["BSDF"], output.inputs["Surface"])
        texture = tree.nodes.new("ShaderNodeTexImage")
        texture.image = self._image_from(f"{key} ({why})", 2, 2, [PLACEHOLDER_GREY, PLACEHOLDER_GREY, PLACEHOLDER_GREY, 1.0] * 4, False)
        tree.links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
        material["jk_placeholder"] = why
        return material

    def material(self, name: str) -> str:
        """Canonical key of MAT ``name``; imports it once (Blender-Sith ``importMat``) or makes a placeholder."""
        key = canonical(name)
        if key in self.materials:
            return key
        self.counts["materials"] += 1
        found = self.resolver.resolve("mat", name)
        if found is None:
            self.counts["materialsMissing"] += 1
            self.missing["materials"].append(name)
            self.materials[key] = self._placeholder(key, "missing")
            self.mat_infos[key] = None
            self.uv_size[key] = (64, 64)
            return key
        info = matfile.inspect(found.data)
        if info.ok and info.kind in ("indexed", "color") and self.colormap is None:
            info.error, info.kind = "No master palette is available to show this 8-bit MAT.", None
        path = None
        if info.ok:
            try:
                path = self.resolver.material_path(key)     # refused when the name would leave the cache
            except resources.CacheError as exc:
                info.error, info.kind = str(exc), None
        self.mat_infos[key] = info
        if not info.ok:
            self.counts["materialsRefused"] += 1
            found.note(f"refused: {info.error}")
            self.warn(f"MAT '{name}' is refused: {info.error}")
            self.materials[key] = self._placeholder(key, "refused")
            self.uv_size[key] = (64, 64)
            return key
        if info.kind == "truecolor":
            self.counts["materialsTrueColor"] += 1
        elif info.kind == "color":
            self.counts["materialsSolid"] += 1
        if info.mip_fallback_note:
            self.counts["mipFallbacks"] += 1
            found.note(info.mip_fallback_note)
            self.warn(f"MAT '{name}': {info.mip_fallback_note}")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(matfile.normalize(found.data, info))
        try:
            material = self.sith.importMat(str(path), self.colormap)
        except Exception as exc:
            self.counts["materialsRefused"] += 1
            found.note(f"Blender-Sith could not import the normalised MAT: {exc}")
            self.warn(f"MAT '{name}': Blender-Sith could not import it ({exc})")
            self.materials[key] = self._placeholder(key, "refused")
            self.uv_size[key] = (64, 64)
            return key
        self._rebuild_images(material, info.has_alpha)
        material["jk_mat"] = key
        self.materials[key] = material
        self.uv_size[key] = info.uv_size
        return key

    # -- world --------------------------------------------------------------------------------------
    def drawn_bounds(self):
        """Wren-framing box: drawn world surfaces (geo != 0, >= 3 corners, a material; sky included)."""
        level = self.level
        count = len(level.vertices)
        points = []
        for surface in level.surfaces:
            if surface.error or surface.geo == 0 or len(surface.verts) < 3:
                continue
            if not 0 <= surface.material < len(level.materials):
                continue
            if any(not 0 <= v < count for v in surface.verts):
                continue
            points.extend(level.vertices[v] for v in surface.verts)
        return framing.bounds(points)

    def build_world(self):
        level, counts, options = self.level, self.counts, self.options
        self.box = self.drawn_bounds()
        center = self.box.center if self.box else (0.0, 0.0, 0.0)
        header = level.header
        positions, uvs, lights, faces = [], [], [], []
        slots, surface_ids, sector_ids, surfflags, faceflags = [], [], [], [], []
        slot_of, slot_materials = {}, []
        nverts, ntex = len(level.vertices), len(level.texverts)
        for surface in level.surfaces:
            counts["surfaces"] += 1
            if surface.error:
                counts["skippedBadIndex"] += 1
                continue
            if surface.geo == 0:
                counts["skippedPortal"] += 1
                continue
            if surface.material < 0:
                counts["skippedNoMaterial"] += 1
                continue
            if surface.material >= len(level.materials):
                counts["skippedBadIndex"] += 1
                continue
            if len(surface.verts) < 3:
                counts["skippedDegenerate"] += 1
                continue
            if any(not 0 <= v < nverts for v in surface.verts):
                counts["skippedBadIndex"] += 1
                continue
            points = [level.vertices[v] for v in surface.verts]
            normal = _newell(points)
            if math.sqrt(sum(c * c for c in normal)) < 1e-12:
                counts["skippedDegenerate"] += 1
                continue
            ceiling = bool(surface.surfflags & jkl.SURF_SKY_CEILING)
            horizon = not ceiling and bool(surface.surfflags & jkl.SURF_SKY_HORIZON)
            if ceiling or horizon:
                counts["sky"] += 1
                if options.sky == "hide":
                    counts["skyHidden"] += 1
                    continue
            order = list(range(len(points)))
            stored = surface.normal
            if stored is not None and sum(a * b for a, b in zip(normal, stored)) < 0:
                order.reverse()
                counts["windingFlipped"] += 1
            key = self.material(level.materials[surface.material][0])
            if key not in slot_of:
                slot_of[key] = len(slot_materials)
                slot_materials.append(self.materials[key])
            width, height = self.uv_size[key]
            sector = level.sectors[surface.sector] if 0 <= surface.sector < len(level.sectors) else None
            sector_extra = sector.extra if sector else 0.0
            start = len(positions)
            thetas = None
            if horizon:
                thetas = []
                for p in points:
                    theta = math.atan2(p[1] - center[1], p[0] - center[0])
                    if thetas and abs(theta - thetas[0]) > math.pi:   # keep one polygon on one side of the seam
                        theta += 2 * math.pi if theta < thetas[0] else -2 * math.pi
                    thetas.append(theta)
            for k in order:
                p = points[k]
                positions.append(p)
                if ceiling:
                    u = (p[0] + header.ceiling_offset[0]) * 16.0 / width
                    v = -(p[1] + header.ceiling_offset[1]) * 16.0 / height
                elif horizon:
                    radius = max(math.hypot(p[0] - center[0], p[1] - center[1]), 1e-6)
                    u_px = thetas[k] / (2 * math.pi) * header.horizon_ppr + header.horizon_offset[0]
                    v_px = -(p[2] - center[2]) * header.horizon_ppr / (2 * math.pi * radius) + header.horizon_offset[1]
                    u, v = u_px / width, -v_px / height
                else:
                    t = surface.texverts[k] if k < len(surface.texverts) else -1
                    tu, tv = level.texverts[t] if 0 <= t < ntex else (0.0, 0.0)
                    u, v = tu / width, -tv / height
                uvs.append((u, v))
                if self.baked:
                    lights.append(light.world_corner(surface.light, ceiling or horizon, surface.rgb[k],
                                                     surface.extralight, sector_extra))
            faces.append(list(range(start, len(positions))))
            slots.append(slot_of[key])
            surface_ids.append(surface.index)
            sector_ids.append(surface.sector)
            surfflags.append(_int32(surface.surfflags))
            faceflags.append(_int32(surface.faceflags))
            counts["drawn"] += 1
        if counts["sky"] and options.sky != "hide":
            self.notes.append(SKY_NOTE)
        name = f"JK level {level.name}"
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata(positions, [], faces)
        uv_layer = mesh.uv_layers.new(name="UVMap")
        uv_layer.data.foreach_set("uv", [c for uv in uvs for c in uv])
        for material in slot_materials:
            mesh.materials.append(material)
        mesh.polygons.foreach_set("material_index", slots)
        for attr_name, values in (("jk_surface", surface_ids), ("jk_sector", sector_ids),
                                  ("jk_surfflags", surfflags), ("jk_faceflags", faceflags)):
            attribute = mesh.attributes.new(attr_name, "INT", "FACE")
            attribute.data.foreach_set("value", values)
        if self.baked:
            attribute = mesh.color_attributes.new("jk_light", "FLOAT_COLOR", "CORNER")
            attribute.data.foreach_set("color", [c for rgb in lights for c in (*rgb, 1.0)])
        mesh.update()
        obj = bpy.data.objects.new(name, mesh)
        obj.color = (0.0, 0.0, 0.0, 1.0)
        self.level_collection.objects.link(obj)
        self.world_object = obj
        return obj

    # -- things ---------------------------------------------------------------------------------------
    def _canonicalize(self, objects):
        for obj in objects:
            if obj.type != "MESH":
                continue
            slots = obj.data.materials
            for index, material in enumerate(slots):
                if material is None:
                    continue
                key = canonical(material.name)
                target = self.materials.get(key)
                if target is None and material.get("jk_mat") is None and material.get("jk_placeholder") is None:
                    target = self.materials[self.material(material.name)]   # a MAT the quick list read missed
                if target is not None and material != target:
                    slots[index] = target
                    if material.users == 0:
                        bpy.data.materials.remove(material)

    def _prepare_model(self, objects, model):
        """Hide meshes and delete faces the engine does not draw; add ``jk_light`` for baked light."""
        import bmesh
        meshes = []
        if model is not None:
            meshes = model.geosets[0].meshes if model.geosets else []
        for obj in objects:
            if obj.type != "MESH":
                continue
            if obj.display_type == "BOUNDS":            # geometry mode 0: not drawn
                obj.hide_render = True
            mesh3do = None
            node_index = getattr(obj, "sith_model3do_hnode_idx", -1)
            if model is not None and 0 <= node_index < len(model.meshHierarchy):
                mesh_index = model.meshHierarchy[node_index].meshIdx
                if 0 <= mesh_index < len(meshes):
                    mesh3do = meshes[mesh_index]
            me = obj.data
            if self.baked:
                values = []
                faces_ok = mesh3do is not None and len(mesh3do.faces) == len(me.polygons)
                for polygon in me.polygons:
                    face = mesh3do.faces[polygon.index] if faces_ok else None
                    for loop_index in polygon.loop_indices:
                        if face is None:
                            values.extend((1.0, 1.0, 1.0, 1.0))
                            continue
                        vertex = me.loops[loop_index].vertex_index
                        mode = light.model_mode(int(mesh3do.lightMode), int(face.lightMode))
                        color = mesh3do.vertexColors[vertex] if vertex < len(mesh3do.vertexColors) else (0, 0, 0, 1)
                        rgb = light.model_corner(mode, tuple(color)[:3], tuple(face.color)[:3])
                        values.extend((*rgb, 1.0))
                if not faces_ok:
                    self.warn(f"3DO mesh '{obj.name}': its faces could not be matched for baked light; drawn fully lit.")
                attribute = me.color_attributes.new("jk_light", "FLOAT_COLOR", "CORNER")
                attribute.data.foreach_set("color", values)
            bm = bmesh.new()
            bm.from_mesh(me)
            layer = bm.faces.layers.string.get("3do_geometry_mode")
            if layer is not None:
                hidden = [f for f in bm.faces if f[layer] in (b"0",)]
                if hidden:
                    bmesh.ops.delete(bm, geom=hidden, context="FACES_ONLY")
                    bm.to_mesh(me)
            bm.free()

    def prototype(self, model_name: str):
        key = model_name.strip().lower()
        if key in self.prototypes:
            return self.prototypes[key]
        self.prototypes[key] = None
        found = self.resolver.resolve("3do", model_name)
        if found is None:
            self.counts["modelsMissing"] += 1
            self.missing["models"].append(model_name)
            return None
        text, data, recoded = decode_3do(found.data)
        if recoded:
            found.note("re-encoded from Latin-1 to UTF-8 for Blender-Sith")
        text, changed = sanitize_3do(text)
        if changed:
            data = text.encode("utf-8")
            found.note(f"{changed} geometry/lighting/texture mode values outside Blender-Sith's enums were read as the "
                       "modes the engine draws them as (for example geometry mode 5 as 4)")
        try:
            path = self.resolver.extract(found, data)
        except resources.CacheError as exc:     # an entry name that would leave the cache
            self.warn(f"3DO '{model_name}' could not be imported: {exc}")
            self.counts["modelImportErrors"] += 1
            self.failed_models.append(model_name)
            found.note(f"not extracted: {exc}")
            return None
        for name in read_3do_materials(text):
            self.material(name)
        before = set(bpy.data.objects)
        model = None
        try:
            if self.baked:
                model, _ = self.sith.load3do(str(path))
            base = self.sith.import3do(str(path), [], self.colormap_path, clearScene=False)
        except Exception as exc:
            base = None
            self.warn(f"3DO '{model_name}' could not be imported: {type(exc).__name__}: {exc}")
        created = [obj for obj in bpy.data.objects if obj not in before]
        if base is None:
            for obj in created:
                bpy.data.objects.remove(obj, do_unlink=True)
            self.counts["modelImportErrors"] += 1
            self.failed_models.append(model_name)
            found.note("import failed or the model has no geometry")
            return None
        collection = bpy.data.collections.new(f"JK 3DO {key}")
        for obj in created:
            for owner in list(obj.users_collection):
                owner.objects.unlink(obj)
            collection.objects.link(obj)
        base.location = (0.0, 0.0, 0.0)          # the engine draws placed things without the insert offset
        self._canonicalize(created)
        self._prepare_model(created, model)
        proto = Prototype(model_name, collection, base, created)
        self.prototypes[key] = proto
        self.counts["prototypes"] += 1
        return proto

    def _place(self, thing, proto, floor):
        empty = bpy.data.objects.new(f"JK thing {thing.index} {thing.name}", None)
        empty.empty_display_size = 0.05
        order, euler = orient.blender_euler(*thing.pyr)
        empty.location = thing.position
        empty.rotation_mode = order
        empty.rotation_euler = euler
        empty["jk_thing"] = thing.index
        empty["jk_template"] = thing.template
        self.things_collection.objects.link(empty)
        if not self.baked:
            empty.instance_type = "COLLECTION"
            empty.instance_collection = proto.collection
            return empty
        copies = {}
        for obj in proto.objects:
            copy = obj.copy()
            copy.color = (floor, floor, floor, 1.0)
            copies[obj] = copy
            self.things_collection.objects.link(copy)
        for obj, copy in copies.items():
            parent = copies.get(obj.parent) if obj.parent is not None else empty
            matrix = copy.matrix_parent_inverse.copy()
            copy.parent = parent
            copy.matrix_parent_inverse = matrix
        return empty

    def build_things(self):
        level, counts, options = self.level, self.counts, self.options
        for thing in level.things:
            counts["things"] += 1
            params, _, problem = jkl.thing_params(level, thing)
            if problem:
                missing_name = problem.split("'")[1] if "'" in problem else thing.template
                if problem.startswith("missing template") and missing_name not in self.missing["templates"]:
                    self.missing["templates"].append(missing_name)
                    self.warn(f"Thing {thing.index} '{thing.name}': {problem}")
                elif "cycle" in problem:
                    self.warn(f"Thing {thing.index} '{thing.name}': {problem}")
            model = params.get("model3d", "").strip()
            if not model or model.lower() == "none":
                counts["thingsNoModel"] += 1
                continue
            if params.get("type", "").lower() == "player" and not options.include_players:
                counts["thingsSkippedPlayer"] += 1
                continue
            if jkl.thing_flags(params) & jkl.THING_NOT_DRAWN and not options.include_hidden:
                counts["thingsSkippedHidden"] += 1
                continue
            proto = self.prototype(model)
            if proto is None:
                continue
            sector = level.sectors[thing.sector] if 0 <= thing.sector < len(level.sectors) else None
            floor = light.thing_floor(sector.ambient, sector.extra) if sector else 0.0
            self.thing_objects.append(self._place(thing, proto, floor))
            counts["thingsPlaced"] += 1

    # -- cameras ---------------------------------------------------------------------------------------
    def build_cameras(self):
        scene = bpy.context.scene
        for view in self.options.views:
            cam = framing.frame(self.box, view, self.options.width, self.options.height,
                                self.options.margin, self.options.framing)
            data = bpy.data.cameras.new(f"JK {view}")
            obj = bpy.data.objects.new(f"JK {view}", data)
            scene.collection.objects.link(obj)
            data.sensor_fit = "AUTO"
            data.clip_start = cam.near
            data.clip_end = cam.far
            obj.location = cam.position
            if cam.projection == "orthographic":
                data.type = "ORTHO"
                data.ortho_scale = cam.blender_ortho_scale
                obj.rotation_euler = (0.0, 0.0, 0.0)
            else:
                data.type = "PERSP"
                data.lens = framing.LENS_MM
                data.sensor_width = framing.SENSOR_MM
                direction = Vector(cam.target) - Vector(cam.position)
                obj.rotation_mode = "QUATERNION"
                obj.rotation_quaternion = direction.to_track_quat("-Z", "Y")
            self.cameras[view] = (cam, obj)
        return self.cameras

    # -- baked materials -------------------------------------------------------------------------------
    def bake_materials(self):
        """Emission ``texture * max(jk_light, object colour)``, alpha-masked: PNG byte = byte(texel * L)."""
        done = set()
        for material in list(self.materials.values()):
            if material is None or material.name in done:
                continue
            done.add(material.name)
            image = None
            for node in material.node_tree.nodes:
                if node.type == "TEX_IMAGE" and node.image is not None:
                    image = node.image
                    break
            key = material.get("jk_mat")
            info = self.mat_infos.get(key) if key else None
            masked = bool(info and info.ok and info.has_alpha)
            tree = material.node_tree
            tree.nodes.clear()
            output = tree.nodes.new("ShaderNodeOutputMaterial")
            texture = tree.nodes.new("ShaderNodeTexImage")
            texture.name = texture.label = "SithTexImage"
            texture.image = image
            texture.interpolation = "Closest"
            attribute = tree.nodes.new("ShaderNodeAttribute")
            attribute.attribute_type = "GEOMETRY"
            attribute.attribute_name = "jk_light"
            info_node = tree.nodes.new("ShaderNodeObjectInfo")
            floor = tree.nodes.new("ShaderNodeVectorMath")
            floor.operation = "MAXIMUM"
            tree.links.new(attribute.outputs["Color"], floor.inputs[0])
            tree.links.new(info_node.outputs["Color"], floor.inputs[1])
            multiply = tree.nodes.new("ShaderNodeVectorMath")
            multiply.operation = "MULTIPLY"
            tree.links.new(texture.outputs["Color"], multiply.inputs[0])
            tree.links.new(floor.outputs["Vector"], multiply.inputs[1])
            emission = tree.nodes.new("ShaderNodeEmission")
            emission.inputs["Strength"].default_value = 1.0
            tree.links.new(multiply.outputs["Vector"], emission.inputs["Color"])
            transparent = tree.nodes.new("ShaderNodeBsdfTransparent")
            surface = emission.outputs["Emission"]
            if masked:
                mix = tree.nodes.new("ShaderNodeMixShader")
                tree.links.new(texture.outputs["Alpha"], mix.inputs["Fac"])
                tree.links.new(transparent.outputs["BSDF"], mix.inputs[1])
                tree.links.new(surface, mix.inputs[2])
                surface = mix.outputs["Shader"]
            # Cycles ignores material backface culling: a back face shows the transparent shader instead,
            # so both engines see only faces whose stored JK normal points at the camera.
            geometry = tree.nodes.new("ShaderNodeNewGeometry")
            cull = tree.nodes.new("ShaderNodeMixShader")
            tree.links.new(geometry.outputs["Backfacing"], cull.inputs["Fac"])
            tree.links.new(surface, cull.inputs[1])
            tree.links.new(transparent.outputs["BSDF"], cull.inputs[2])
            tree.links.new(cull.outputs["Shader"], output.inputs["Surface"])
            material.use_backface_culling = True
            material.surface_render_method = "DITHERED"
            for x, node in enumerate((texture, attribute, info_node, floor, multiply, emission, output)):
                node.location = (x * 220 - 700, 0)

    # -- all ---------------------------------------------------------------------------------------------
    def build(self):
        self.resolve_master_colormap()
        self.build_world()
        if self.options.include_things:
            self.build_things()
        for material in self.materials.values():
            material.use_backface_culling = True
        if self.baked:
            self.bake_materials()
        if self.options.create_cameras and self.box is not None:
            self.build_cameras()
        elif self.options.create_cameras:
            self.warn("Level has no drawn world surfaces to frame; no cameras were made.")
        return self
