# Render setup and outputs for JK level previews (flat Workbench or baked EEVEE/Cycles).
# Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
# one of Andrei's collaborators) wrote the original render.py; recovered 2026-10-31.
# Uses Coda's Blender-Sith fork (v1.0.1-coda.1), based on Blender-Sith by Crt Vavros (smlu).
# SPDX-License-Identifier: GPL-3.0-or-later
"""Two looks:

* ``flat`` is Wren's: Workbench, studio light, texture colour, backface culling, world (0.18, 0.2, 0.24).
* ``baked`` multiplies each texel by the JKL's own light (``jk_light``, see ``light.py``) in an emission shader
  rendered with the Raw view transform, so a PNG byte equals ``byte(texel * L)`` within one step.
  Workbench cannot multiply a texture by a colour attribute, so baked light uses EEVEE, or Cycles on
  the CPU when EEVEE cannot start.
"""
from __future__ import annotations

import hashlib
import os
import shutil
import tempfile

import bpy

from .build import BAKED_WORLD_COLOR, FLAT_WORLD_COLOR, _use_nodes

EEVEE_SAMPLES = 8
CYCLES_SAMPLES = 16


class RenderFailure(RuntimeError):
    pass


def srgb_byte(value: float) -> int:
    value = max(0.0, min(1.0, value))
    encoded = 12.92 * value if value <= 0.0031308 else 1.055 * value ** (1 / 2.4) - 0.055
    return int(round(encoded * 255))


def background_bytes(light_mode: str):
    """The PNG value of an empty pixel: Wren's world through the Standard view, or SED's clear colour raw."""
    if light_mode == "baked":
        return tuple(int(round(c * 255)) for c in BAKED_WORLD_COLOR)
    return tuple(srgb_byte(c) for c in FLAT_WORLD_COLOR)


def _world(scene, color):
    world = bpy.data.worlds.new("JK world")
    world.color = color
    _use_nodes(world)
    tree = world.node_tree
    background = tree.nodes.get("Background") or tree.nodes.new("ShaderNodeBackground")
    background.inputs["Color"].default_value = (*color, 1.0)
    background.inputs["Strength"].default_value = 1.0
    output = tree.nodes.get("World Output") or tree.nodes.new("ShaderNodeOutputWorld")
    if not output.inputs["Surface"].is_linked:
        tree.links.new(background.outputs["Background"], output.inputs["Surface"])
    scene.world = world
    return world


def _common(scene, width, height):
    render = scene.render
    render.resolution_x, render.resolution_y = width, height
    render.resolution_percentage = 100
    render.film_transparent = False
    settings = render.image_settings
    settings.file_format = "PNG"
    settings.color_mode = "RGBA"
    settings.color_depth = "8"
    # No metadata text (date, render time, file name...): the same inputs give byte-identical PNGs,
    # so the SHA-256 values in reports can be compared between runs.
    for name in render.bl_rna.properties.keys():
        if name.startswith("use_stamp"):
            setattr(render, name, False)


def setup_flat(scene, width, height):
    """Wren's look: Workbench, studio light, texture colour, backface culling."""
    _common(scene, width, height)
    scene.render.engine = "BLENDER_WORKBENCH"
    shading = scene.display.shading
    shading.light = "STUDIO"
    shading.color_type = "TEXTURE"
    shading.show_backface_culling = True
    if hasattr(shading, "background_type"):
        shading.background_type = "WORLD"
    _world(scene, FLAT_WORLD_COLOR)
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    return "BLENDER_WORKBENCH"


def setup_baked(scene, width, height, engine: str):
    """Emission-only render in the Raw view transform; ``engine`` is ``eevee`` or ``cycles``."""
    _common(scene, width, height)
    _world(scene, BAKED_WORLD_COLOR)
    view = scene.view_settings
    view.view_transform = "Raw"
    view.look = "None"
    view.exposure = 0.0
    view.gamma = 1.0
    scene.render.dither_intensity = 0.0
    if engine == "eevee":
        scene.render.engine = "BLENDER_EEVEE"
        eevee = scene.eevee
        eevee.taa_render_samples = EEVEE_SAMPLES
        for name in ("use_shadows", "use_raytracing", "use_gtao", "use_bloom", "use_ssr"):
            if name in eevee.bl_rna.properties:
                setattr(eevee, name, False)
        return "BLENDER_EEVEE"
    scene.render.engine = "CYCLES"
    cycles = scene.cycles
    cycles.device = "CPU"
    cycles.samples = CYCLES_SAMPLES
    for name, value in (("use_denoising", False), ("use_adaptive_sampling", False), ("pixel_filter_type", "BOX"),
                        ("filter_width", 0.5), ("max_bounces", 0), ("transparent_max_bounces", 16)):
        if name in cycles.bl_rna.properties:
            setattr(cycles, name, value)
    return "CYCLES"


def render_still(scene, camera, path: str):
    """Render one view and save it with colour management applied (no frame-number substitution)."""
    scene.camera = camera
    bpy.ops.render.render(write_still=False)
    result = bpy.data.images.get("Render Result")
    if result is None:
        raise RenderFailure("Blender produced no render result.")
    directory = os.path.dirname(os.path.abspath(path))
    handle, temporary = tempfile.mkstemp(suffix=".png", dir=directory)
    os.close(handle)
    try:
        result.save_render(temporary, scene=scene)
        if not os.path.getsize(temporary):
            raise RenderFailure(f"Blender wrote an empty image for {path}.")
        shutil.move(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.remove(temporary)


def png_summary(path: str, background) -> dict:
    """SHA-256 and the fraction of pixels that differ from the background by more than 3 steps."""
    digest = hashlib.sha256(open(path, "rb").read()).hexdigest()
    image = bpy.data.images.load(path, check_existing=False)
    try:
        width, height = image.size
        count = width * height
        try:
            import numpy as np
            pixels = np.empty(count * 4, dtype=np.float32)
            image.pixels.foreach_get(pixels)
            rgb = np.rint(pixels.reshape(-1, 4)[:, :3] * 255)
            differs = np.any(np.abs(rgb - np.array(background, dtype=np.float32)) > 3, axis=1)
            changed = int(differs.sum())
        except ImportError:
            pixels = list(image.pixels[:])
            changed = sum(1 for i in range(count)
                          if any(abs(round(pixels[i * 4 + c] * 255) - background[c]) > 3 for c in range(3)))
    finally:
        bpy.data.images.remove(image)
    return {"sha256": digest, "nonBackgroundFraction": round(changed / count, 6) if count else 0.0}
