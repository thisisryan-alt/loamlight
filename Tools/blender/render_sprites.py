"""
Headless Blender sprite renderer for Loamlight.

Builds low-poly models in code, poses them per animation frame and renders every frame twice with an
orthographic camera:
  albedo  flat emission colors (Standard view transform), so the PNG holds the authored sRGB colors
  normal  camera-space shading normal encoded as rgb = n * 0.5 + 0.5 (Raw view transform, no color transform)

The encoded space is: +X right, +Y up, +Z toward the viewer (Cycles' camera space with Z negated), which is exactly the sprite tangent space URP's
2D renderer expects for _NormalMap. A pixel facing the camera encodes to (128, 128, 255).

Rendering is Cycles CPU, 1 sample, box filter of 0.01 px (every pixel is a single ray through its centre),
no dither, no metadata, so output is pixel-exact and deterministic. Frames are written to
Tools/.cache/frames/<sheet>/ together with frames.json, which Tools/art/pack_sheets.py turns into sheets.

Usage:
  blender --background --factory-startup --python-exit-code 1 --python render_sprites.py -- --out <dir> [--only miner]
"""
import argparse
import json
import math
import os
import sys
import warnings

import bmesh
import bpy
from mathutils import Matrix, Vector

# Material/World.use_nodes is deprecated in Blender 5 (nodes are always on) but still needed on older 4.x
warnings.filterwarnings("ignore", category=DeprecationWarning)

PPU = 16                      # pixels per world unit (= one tile)
PX = 1.0 / PPU                # one pixel in world units


# --------------------------------------------------------------------------------------------- helpers
def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def hex_color(h):
    h = h.lstrip("#")
    return tuple(srgb_to_linear(int(h[i:i + 2], 16) / 255.0) for i in (0, 2, 4)) + (1.0,)


_materials = {}

# Albedo shading: pixel art bakes a soft key light from the top left into its colours, so sprites read as solid
# even under the flat sky light; the normal map then adds dynamic light on top. Bands are constant (no gradient)
# so the shading stays crisp at 16 px per tile. SHADING is switched off for the calibration sphere.
SHADING = {"on": True}
KEY_LIGHT = (-0.52, -0.58, 0.63)          # world space: left, toward the viewer (-Y), up
SHADE_BANDS = [(0.0, 0.52), (0.40, 0.76), (0.62, 1.0), (0.86, 1.2)]   # (position on (n.l + 1) / 2, multiplier)


def flat_material(hex_str):
    """Emission material: the authored colour times a banded key-light term (or exactly the colour if unshaded)."""
    key = hex_str + ("_s" if SHADING["on"] else "_f")
    if key in _materials:
        return _materials[key]
    mat = bpy.data.materials.new("albedo_" + key.lstrip("#"))
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Strength"].default_value = 1.0
    if SHADING["on"]:
        geo = nt.nodes.new("ShaderNodeNewGeometry")
        dot = nt.nodes.new("ShaderNodeVectorMath")
        dot.operation = "DOT_PRODUCT"
        length = math.sqrt(sum(c * c for c in KEY_LIGHT))
        dot.inputs[1].default_value = tuple(c / length for c in KEY_LIGHT)
        remap = nt.nodes.new("ShaderNodeMath")
        remap.operation = "MULTIPLY_ADD"
        remap.inputs[1].default_value = 0.5
        remap.inputs[2].default_value = 0.5
        ramp = nt.nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.interpolation = "CONSTANT"
        els = ramp.color_ramp.elements
        while len(els) < len(SHADE_BANDS):
            els.new(0.5)
        for e, (pos, mul) in zip(els, SHADE_BANDS):
            e.position = pos
            e.color = (mul, mul, mul, 1.0)
        mix = nt.nodes.new("ShaderNodeVectorMath")
        mix.operation = "MULTIPLY"
        mix.inputs[1].default_value = hex_color(hex_str)[:3]
        nt.links.new(geo.outputs["Normal"], dot.inputs[0])
        nt.links.new(dot.outputs["Value"], remap.inputs[0])
        nt.links.new(remap.outputs["Value"], ramp.inputs["Fac"])
        nt.links.new(ramp.outputs["Color"], mix.inputs[0])
        nt.links.new(mix.outputs["Vector"], em.inputs["Color"])
    else:
        em.inputs["Color"].default_value = hex_color(hex_str)
    nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    _materials[key] = mat
    return mat


def ellipsoid(name, radii, center, color, parent=None, segments=(16, 10), pivot=None):
    """Smooth-shaded ellipsoid (uv sphere scaled per axis); origin at `pivot` (defaults to the centre)."""
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=segments[0], v_segments=segments[1], radius=1.0)
    piv = Vector(pivot if pivot is not None else center)
    off = Vector(center) - piv
    for v in bm.verts:
        v.co = Vector((v.co.x * radii[0], v.co.y * radii[1], v.co.z * radii[2])) + off
    return new_object(name, bm, color, parent, tuple(piv), smooth=True)


def cone(name, r0, r1, depth, center, color, parent=None, rot=(0, 0, 0), segments=10, smooth=True):
    """Cone/frustum along local Z, rotated by `rot` (radians) then placed at `center` (its origin)."""
    from mathutils import Euler
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments, radius1=r0, radius2=r1, depth=depth)
    m = Euler(rot).to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=m, verts=bm.verts)
    return new_object(name, bm, color, parent, center, smooth=smooth)


def membrane(name, points, color, parent=None, thickness=0.06):
    """A flat polygon in the XZ plane (wing membranes, rags), thickened slightly along Y so it renders both sides."""
    bm = bmesh.new()
    verts = [bm.verts.new((x, 0.0, z)) for x, z in points]
    face = bm.faces.new(verts)
    res = bmesh.ops.extrude_face_region(bm, geom=[face])
    for v in [e for e in res["geom"] if isinstance(e, bmesh.types.BMVert)]:
        v.co.y += thickness
    for v in bm.verts:
        v.co.y -= thickness / 2
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return new_object(name, bm, color, parent)


def normal_material():
    """Emits the camera-space shading normal remapped to 0..1."""
    mat = bpy.data.materials.new("camera_space_normal")
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    xf = nt.nodes.new("ShaderNodeVectorTransform")
    xf.vector_type = "NORMAL"
    xf.convert_from = "WORLD"
    xf.convert_to = "CAMERA"
    norm = nt.nodes.new("ShaderNodeVectorMath")
    norm.operation = "NORMALIZE"
    remap = nt.nodes.new("ShaderNodeVectorMath")
    remap.operation = "MULTIPLY_ADD"
    # Cycles' camera space is left-handed with +Z pointing away from the viewer; negate Z so a
    # camera-facing surface encodes to blue = 255 (verified by the calibration sphere in pack_sheets.py).
    remap.inputs[1].default_value = (0.5, 0.5, -0.5)
    remap.inputs[2].default_value = (0.5, 0.5, 0.5)
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Strength"].default_value = 1.0
    nt.links.new(geo.outputs["Normal"], xf.inputs["Vector"])
    nt.links.new(xf.outputs["Vector"], norm.inputs[0])
    nt.links.new(norm.outputs["Vector"], remap.inputs[0])
    nt.links.new(remap.outputs["Vector"], em.inputs["Color"])
    nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    return mat


def new_object(name, bm, color, parent=None, location=(0, 0, 0), smooth=False):
    for f in bm.faces:
        f.smooth = smooth
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(flat_material(color))
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = parent
    obj.location = location
    return obj


def box(name, size, center, color, parent=None, pivot=(0, 0, 0), round_px=None):
    """Axis-aligned box with bevelled (rounded) edges. `center` and `pivot` are in the parent's space; the
    object origin sits at `pivot` so rotating the object swings the box about that joint.
    The bevel matters for lighting: a plain box shows the camera one flat face, so its normal map would be
    a single flat color. Rounded edges give limbs and torso real shape under 2D lights."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    off = Vector(center) - Vector(pivot)
    for v in bm.verts:
        v.co = Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2])) + off
    if round_px is None:
        round_px = min(size[0], size[2]) / PX * 0.32
    if round_px > 0.2:
        bmesh.ops.bevel(bm, geom=list(bm.edges) + list(bm.verts), offset=round_px * PX, offset_type="OFFSET",
                        segments=2, profile=0.5, affect="EDGES", clamp_overlap=True)
    return new_object(name, bm, color, parent, pivot)


def empty(name, parent=None, location=(0, 0, 0)):
    obj = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = parent
    obj.location = location
    return obj


def swing(obj, degrees_forward):
    """Rotate a limb about the world Y axis (the view axis) so a downward-pointing limb moves toward +X."""
    obj.rotation_euler = (0.0, -math.radians(degrees_forward), 0.0)


# --------------------------------------------------------------------------------------------- scene
def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    _materials.clear()


def configure_render(frame_px, ortho_units, cam_center):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = 1
    sc.cycles.use_adaptive_sampling = False
    sc.cycles.use_denoising = False
    sc.cycles.seed = 0
    sc.cycles.pixel_filter_type = "BOX"
    sc.cycles.filter_width = 0.01
    sc.cycles.max_bounces = 0
    sc.cycles.use_light_tree = False
    sc.render.resolution_x = frame_px
    sc.render.resolution_y = frame_px
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = True
    sc.render.dither_intensity = 0.0
    sc.render.use_stamp = False
    for attr in dir(sc.render):
        if attr.startswith("use_stamp_"):
            try:
                setattr(sc.render, attr, False)
            except (AttributeError, TypeError):
                pass
    img = sc.render.image_settings
    img.file_format = "PNG"
    img.color_mode = "RGBA"
    img.color_depth = "8"
    img.compression = 15
    sc.display_settings.display_device = "sRGB"
    sc.view_settings.look = "None"
    sc.view_settings.exposure = 0.0
    sc.view_settings.gamma = 1.0

    world = bpy.data.worlds.new("black")
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    bg.inputs["Color"].default_value = (0, 0, 0, 1)
    bg.inputs["Strength"].default_value = 0.0
    sc.world = world

    cam_data = bpy.data.cameras.new("sprite_cam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = ortho_units
    cam_data.clip_start = 0.1
    cam_data.clip_end = 100.0
    cam = bpy.data.objects.new("sprite_cam", cam_data)
    sc.collection.objects.link(cam)
    # Looking along +Y: camera right = +X, camera up = +Z, toward the viewer = -Y.
    cam.location = (cam_center[0], -20.0, cam_center[1])
    cam.rotation_euler = (math.radians(90.0), 0.0, 0.0)
    sc.camera = cam


def render_pair(out_dir, frame_name, normal_mat):
    sc = bpy.context.scene
    vl = bpy.context.view_layer
    vl.update()

    vl.material_override = None
    sc.view_settings.view_transform = "Standard"
    sc.render.filepath = os.path.join(out_dir, frame_name + "_albedo.png")
    bpy.ops.render.render(write_still=True)

    vl.material_override = normal_mat
    sc.view_settings.view_transform = "Raw"
    sc.render.filepath = os.path.join(out_dir, frame_name + "_normal.png")
    bpy.ops.render.render(write_still=True)
    vl.material_override = None


# --------------------------------------------------------------------------------------------- models
MINER_COLORS = {
    "skin": "#e0a476", "shirt": "#3f6f8f", "shirt_dark": "#335b76", "trousers": "#5a4632",
    "boots": "#2e2620", "belt": "#3a2a1c", "buckle": "#c9a24a", "helmet": "#e2b23c",
    "helmet_rim": "#b98a26", "lamp": "#fff4c8", "pack": "#7a5a3a", "pack_strap": "#5c4229",
    "eye": "#1a1414", "handle": "#8a6236", "pick": "#9aa3ad",
}


def build_miner():
    """A stocky low-poly miner facing +X: helmet with headlamp, backpack, pickaxe for the 'use' animation.
    Height is about 44 px (2.75 tiles). All joints are empties/objects so poses are plain rotations."""
    c = MINER_COLORS
    root = empty("miner_root")
    body = empty("body", root)                      # bobbing part: everything above the hips

    hip_z = 14 * PX
    # legs: near leg (toward the camera, y < 0) and far leg
    legs = {}
    for side, y, shade in (("near", -0.12, c["trousers"]), ("far", 0.12, "#4c3b2a")):
        hip = empty(f"leg_{side}", root, (0.0, y, hip_z))
        box(f"thigh_{side}", (5 * PX, 0.22, 11 * PX), (0, 0, -5.5 * PX), shade, hip)
        box(f"boot_{side}", (7 * PX, 0.24, 3 * PX), (1 * PX, 0, -12.5 * PX), c["boots"], hip)
        box(f"sole_{side}", (7.4 * PX, 0.25, 1 * PX), (1.2 * PX, 0, -13.6 * PX), "#1a1410", hip, round_px=0)
        box(f"knee_{side}", (3.6 * PX, 0.23, 3 * PX), (0.8 * PX, -0.002, -5 * PX), "#4c3b2a" if side == "near" else "#3e3022", hip)
        legs[side] = hip

    # torso, belt, backpack
    box("torso", (9 * PX, 0.42, 14 * PX), (0, 0, hip_z + 7 * PX), c["shirt"], body)
    box("belt", (9.4 * PX, 0.44, 2 * PX), (0, 0, hip_z + 1 * PX), c["belt"], body)
    box("buckle", (1.6 * PX, 0.46, 1.6 * PX), (3.6 * PX, -0.005, hip_z + 1 * PX), c["buckle"], body)
    box("pack", (5 * PX, 0.36, 10 * PX), (-6.5 * PX, 0, hip_z + 8 * PX), c["pack"], body)
    box("pack_pocket", (3 * PX, 0.4, 4 * PX), (-7.6 * PX, -0.02, hip_z + 5 * PX), c["pack_strap"], body)
    box("pack_roll", (6 * PX, 0.4, 3 * PX), (-6 * PX, 0, hip_z + 14.5 * PX), "#9a8a6a", body)
    box("strap", (1.4 * PX, 0.44, 12 * PX), (-2.6 * PX, -0.01, hip_z + 8 * PX), c["pack_strap"], body)
    box("collar", (8 * PX, 0.44, 2 * PX), (0.5 * PX, 0, hip_z + 13.6 * PX), "#2c5470", body)

    # head: low-poly ico sphere, helmet: half uv sphere with brim, headlamp
    head_z = hip_z + 19.5 * PX
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=2, radius=5.4 * PX)
    head = new_object("head", bm, c["skin"], body, (0.6 * PX, 0, head_z), smooth=True)
    box("hair", (3 * PX, 0.38, 3 * PX), (-4.4 * PX, 0, -0.6 * PX), "#6a4424", head)
    box("brow", (2.2 * PX, 0.36, 0.8 * PX), (3.4 * PX, -0.17, 1.8 * PX), "#5a3a1e", head, round_px=0)
    # the eye box runs deep toward the camera so it pokes out of the curved side of the head
    box("eye", (1.2 * PX, 0.35, 1.6 * PX), (3.6 * PX, -0.175, 0.4 * PX), c["eye"], head)
    box("nose", (1.6 * PX, 0.1, 1.4 * PX), (5.6 * PX, 0, -0.8 * PX), "#cf8f62", head)

    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=10, v_segments=6, radius=6.2 * PX)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.co.z < -0.001], context="VERTS")
    new_object("helmet", bm, c["helmet"], head, (0, 0, 1.0 * PX), smooth=True)
    box("helmet_ridge", (1.4 * PX, 0.5, 2 * PX), (0, 0, 7.0 * PX), c["helmet_rim"], head)
    box("brim", (14.5 * PX, 0.5, 1.4 * PX), (0.6 * PX, 0, 0.6 * PX), c["helmet_rim"], head)
    box("lamp", (1.6 * PX, 0.16, 2.6 * PX), (6.4 * PX, -0.06, 3.2 * PX), c["lamp"], head)

    # arms (shoulder pivots), hands, pickaxe on the near hand
    shoulder_z = hip_z + 12.5 * PX
    arms = {}
    for side, y, shade in (("near", -0.27, c["shirt"]), ("far", 0.27, c["shirt_dark"])):
        sh = empty(f"arm_{side}", body, (1.0 * PX, y, shoulder_z))
        box(f"sleeve_{side}", (4 * PX, 0.13, 9 * PX), (0, 0, -4.5 * PX), shade, sh)
        box(f"cuff_{side}", (4.2 * PX, 0.14, 1.4 * PX), (0, 0, -8.6 * PX), "#2c5470", sh)
        box(f"hand_{side}", (3.6 * PX, 0.14, 3 * PX), (0, 0, -10 * PX), "#8a5a34", sh)
        arms[side] = sh

    # pickaxe: the grip sits in the near hand, the handle runs 20 degrees forward of the arm line (local -Z)
    pick = empty("pickaxe", arms["near"], (0, -0.1, -10 * PX))
    swing(pick, 20)
    box("pick_handle", (1.6 * PX, 0.08, 15 * PX), (0, 0, -5.5 * PX), c["handle"], pick)
    bm = bmesh.new()   # head: a bar across the handle end whose tips sweep back toward the grip
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * 14 * PX, v.co.y * 0.1, v.co.z * 2.2 * PX))
        v.co.z += -12 * PX + abs(v.co.x) * 0.3
    new_object("pick_head", bm, c["pick"], pick)

    return {"root": root, "body": body, "legs": legs, "arms": arms, "head": head, "pick": pick}


def pose_miner(rig, anim, i, n):
    """Set the rig to frame i of n of an animation. Offsets are whole pixels so the art stays crisp."""
    body, legs, arms, pick = rig["body"], rig["legs"], rig["arms"], rig["pick"]
    body.location = (0, 0, 0)
    body.rotation_euler = (0, 0, 0)
    for o in (*legs.values(), *arms.values()):
        swing(o, 0)
    pick_parts = pick.children_recursive          # hide_render on an empty does not hide its children
    for o in pick_parts:
        o.hide_render = True

    if anim == "idle":
        bob = 1 if i in (1, 2) else 0
        body.location = (0, 0, -bob * PX)
        swing(arms["near"], 4 if i in (1, 2) else 2)
        swing(arms["far"], -4 if i in (1, 2) else -2)
    elif anim == "walk":
        t = 2 * math.pi * i / n
        stride = 32 * math.sin(t)
        swing(legs["near"], stride)
        swing(legs["far"], -stride)
        swing(arms["near"], -26 * math.sin(t))
        swing(arms["far"], 26 * math.sin(t))
        body.location = (0, 0, (1 if abs(math.sin(t)) < 0.5 else 0) * PX)
    elif anim == "jump":
        swing(legs["near"], 35)
        swing(legs["far"], -20)
        swing(arms["near"], -150)
        swing(arms["far"], 40)
    elif anim == "fall":
        swing(legs["near"], 12)
        swing(legs["far"], -8)
        swing(arms["near"], -165)
        swing(arms["far"], -140)
    elif anim == "attack":
        # weapon swings: the blade is a separate sprite, so the arm alone sells the motion
        angle = [175, 120, 45, 15][i]
        swing(arms["near"], angle)
        swing(arms["far"], -20 + angle * 0.2)
        body.rotation_euler = (0, math.radians(5 if i >= 2 else -4), 0)
        swing(legs["near"], 18 if i >= 2 else 6)
        swing(legs["far"], -14 if i >= 2 else -4)
    elif anim == "use":
        for o in pick_parts:
            o.hide_render = False
        # wind-up behind the head, strike forward and down, follow through, recover
        # arm angle from straight down toward forward: 200 = up and back, 70 = impact, 30 = follow-through
        angle = [200, 150, 70, 30][i]
        swing(arms["near"], angle)
        swing(arms["far"], angle * 0.4)
        body.rotation_euler = (0, math.radians(4 if i >= 2 else -3), 0)   # lean back, then into the strike
        swing(legs["near"], 14)
        swing(legs["far"], -10)


def build_calibration_sphere():
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=48, v_segments=24, radius=19 * PX)
    return new_object("calib_sphere", bm, "#a0a0a0", None, (0, 0, 0), smooth=True)


# --------------------------------------------------------------------------------------------- enemies
def build_gloop():
    """A jelly blob: glossy dome, darker core, two big eyes. Squash and stretch come from scaling the root."""
    root = empty("gloop_root")
    body = ellipsoid("gloop_body", (11 * PX, 0.5, 8.5 * PX), (0, 0, 8.5 * PX), "#5fbf4a", root, (24, 12), pivot=(0, 0, 0))
    ellipsoid("gloop_core", (6 * PX, 0.4, 4.5 * PX), (-1.5 * PX, -0.12, 6.5 * PX), "#3f9a36", root, (16, 8), pivot=(0, 0, 0))
    ellipsoid("gloop_shine", (3 * PX, 0.2, 1.6 * PX), (-4.5 * PX, -0.42, 13 * PX), "#d8ffc0", root, (12, 6), pivot=(0, 0, 0))
    for i, x in enumerate((1.5, 6.0)):
        ellipsoid(f"eye_{i}", (2.1 * PX, 0.2, 2.5 * PX), (x * PX, -0.42, 10 * PX), "#f4f8f0", root, (12, 8), pivot=(0, 0, 0))
        ellipsoid(f"pupil_{i}", (1.0 * PX, 0.2, 1.3 * PX), ((x + 0.8) * PX, -0.5, 9.7 * PX), "#14200f", root, (8, 6), pivot=(0, 0, 0))
    box("mouth", (3 * PX, 0.2, 0.8 * PX), (4 * PX, -0.44, 6.6 * PX), "#24501c", root, round_px=0)
    return {"root": root}


def pose_gloop(rig, anim, i, n):
    root = rig["root"]
    sx, sz = {
        "idle": [(1.0, 1.0), (1.04, 0.95), (1.07, 0.92), (1.03, 0.96)],
        "squash": [(1.18, 0.78), (1.28, 0.66)],
        "jump": [(0.84, 1.22)],
        "fall": [(0.92, 1.1)],
    }[anim][i]
    root.scale = (sx, 1.0, sz)


def build_duskbat():
    """Cave bat: furry round body, tall ears, glowing eyes, two membrane wings hinged at the shoulders."""
    c = {"fur": "#4a3a5e", "belly": "#6e5a80", "wing": "#3b2c4c", "bone": "#2a1f36", "ear": "#5a4870", "eye": "#ffd84a"}
    root = empty("bat_root")
    body = empty("bat_body", root, (0, 0, 8 * PX))
    ellipsoid("bat_torso", (5 * PX, 0.4, 6 * PX), (0, 0, 0), c["fur"], body, (16, 10))
    ellipsoid("bat_belly", (3 * PX, 0.3, 4 * PX), (0.8 * PX, -0.14, -0.8 * PX), c["belly"], body, (12, 8))
    for i, x in enumerate((-2.2, 2.2)):
        cone(f"bat_ear_{i}", 2.0 * PX, 0.2 * PX, 5 * PX, (x * PX, 0, 6.8 * PX), c["ear"], body, rot=(0, math.radians(-15 if x < 0 else 15), 0), segments=8)
    for i, x in enumerate((0.6, 3.0)):
        ellipsoid(f"bat_eye_{i}", (1.1 * PX, 0.2, 1.2 * PX), (x * PX, -0.36, 1.8 * PX), c["eye"], body, (8, 6))
    box("bat_fang", (2.6 * PX, 0.2, 1.2 * PX), (1.8 * PX, -0.36, -0.6 * PX), "#f0ece0", body, round_px=0)
    wings = {}
    for side, sgn in (("near", 1), ("far", -1)):
        hinge = empty(f"wing_{side}", body, (sgn * 3.5 * PX, 0.05 * -sgn, 1.5 * PX))
        pts = [(0, 0), (sgn * 6 * PX, 4 * PX), (sgn * 12 * PX, 3 * PX), (sgn * 13 * PX, -3 * PX), (sgn * 10 * PX, -1 * PX),
               (sgn * 8 * PX, -4.5 * PX), (sgn * 5 * PX, -1.5 * PX), (sgn * 2 * PX, -4 * PX)]
        if sgn < 0:
            pts = list(reversed(pts))
        membrane(f"wing_mem_{side}", pts, c["wing"] if side == "near" else "#30243f", hinge)
        box(f"wing_bone_{side}", (12 * PX, 0.08, 1 * PX), (sgn * 6.5 * PX, -0.04, 3.5 * PX), c["bone"], hinge, round_px=0).rotation_euler = (0, math.radians(-4 * sgn), 0)
        wings[side] = (hinge, sgn)
    return {"root": root, "body": body, "wings": wings}


def pose_duskbat(rig, anim, i, n):
    body, wings = rig["body"], rig["wings"]
    body.location = (0, 0, 8 * PX)
    body.rotation_euler = (0, 0, 0)
    if anim == "fly":
        flap = [38, 12, -22, 8][i]
        body.location = (0, 0, (8 + [0, 1, 2, 1][i]) * PX)
    elif anim == "flare":
        flap = [62, 70][i]
    else:   # dive: wings swept back
        flap = -55
        body.rotation_euler = (0, math.radians(35), 0)
    for hinge, sgn in wings.values():
        hinge.rotation_euler = (0, -math.radians(flap) * sgn, 0)


def build_hollow():
    """A gaunt husk in a hood and rags: long thin arms, pale glowing eyes, hunched posture."""
    c = {"skin": "#7a8490", "cloth": "#4a4f5a", "rag": "#3a3e48", "hood": "#2f333c", "eye": "#c8f0ff", "dark": "#262a32"}
    root = empty("hollow_root")
    body = empty("hollow_body", root)
    hip_z = 15 * PX
    legs = {}
    for side, y, shade in (("near", -0.12, c["skin"]), ("far", 0.12, "#5e6672")):
        hip = empty(f"hl_leg_{side}", root, (0, y, hip_z))
        box(f"hl_shin_{side}", (3.4 * PX, 0.2, 14 * PX), (0, 0, -7 * PX), shade, hip)
        box(f"hl_foot_{side}", (5 * PX, 0.22, 2 * PX), (1.2 * PX, 0, -14 * PX), c["dark"], hip)
        legs[side] = hip
    torso = empty("hollow_torso", body, (0, 0, hip_z))
    box("hl_chest", (8 * PX, 0.4, 15 * PX), (0, 0, 7 * PX), c["cloth"], torso)
    for k, (x, z, h) in enumerate(((-3, -1, 6), (0, -2, 7), (3, -1.5, 5))):
        box(f"hl_rag_{k}", (2.6 * PX, 0.42, h * PX), (x * PX, -0.01, z * PX), c["rag"], torso)
    box("hl_belt", (8.4 * PX, 0.44, 1.6 * PX), (0, 0, 1.5 * PX), c["dark"], torso)
    head = empty("hollow_head", torso, (2 * PX, 0, 17 * PX))
    ellipsoid("hl_face", (4 * PX, 0.32, 4.6 * PX), (0.6 * PX, 0, 0), c["skin"], head, (14, 10))
    ellipsoid("hl_hood", (5.4 * PX, 0.4, 5.6 * PX), (-1.2 * PX, 0.04, 1.0 * PX), c["hood"], head, (14, 10))
    for k, x in enumerate((1.4, 3.4)):
        ellipsoid(f"hl_eye_{k}", (0.9 * PX, 0.2, 1.1 * PX), (x * PX, -0.32, 0.4 * PX), c["eye"], head, (8, 6))
    box("hl_mouth", (2.4 * PX, 0.2, 0.7 * PX), (2.6 * PX, -0.3, -2.2 * PX), c["dark"], head, round_px=0)
    arms = {}
    for side, y, shade in (("near", -0.26, c["skin"]), ("far", 0.26, "#5e6672")):
        sh = empty(f"hl_arm_{side}", torso, (1 * PX, y, 13 * PX))
        box(f"hl_upper_{side}", (2.8 * PX, 0.14, 9 * PX), (0, 0, -4.5 * PX), c["rag"], sh)
        box(f"hl_fore_{side}", (2.4 * PX, 0.14, 9 * PX), (0, 0, -12 * PX), shade, sh)
        box(f"hl_claw_{side}", (3.4 * PX, 0.15, 2.4 * PX), (0.6 * PX, 0, -16.8 * PX), c["dark"], sh)
        arms[side] = sh
    return {"root": root, "body": body, "torso": torso, "legs": legs, "arms": arms}


def pose_hollow(rig, anim, i, n):
    torso, legs, arms = rig["torso"], rig["legs"], rig["arms"]
    torso.rotation_euler = (0, math.radians(14), 0)    # the hunch
    torso.location = (0, 0, 15 * PX)
    for o in (*legs.values(), *arms.values()):
        swing(o, 0)
    if anim == "idle":
        torso.location = (0, 0, (15 - (1 if i == 1 else 0)) * PX)
        swing(arms["near"], 10 + 4 * i)
        swing(arms["far"], 4)
    elif anim == "walk":
        t = 2 * math.pi * i / n
        swing(legs["near"], 26 * math.sin(t))
        swing(legs["far"], -26 * math.sin(t))
        swing(arms["near"], 20 - 18 * math.sin(t))
        swing(arms["far"], 20 + 18 * math.sin(t))
        torso.location = (0, 0, (15 + (1 if abs(math.sin(t)) < 0.5 else 0)) * PX)
    elif anim == "windup":
        torso.rotation_euler = (0, math.radians(-6 - 6 * i), 0)
        swing(arms["near"], 160 + 10 * i)
        swing(arms["far"], 150 + 10 * i)
        swing(legs["near"], 14)
        swing(legs["far"], -18)
    elif anim == "lunge":
        torso.rotation_euler = (0, math.radians(32), 0)
        swing(arms["near"], 80)
        swing(arms["far"], 70)
        swing(legs["near"], 30)
        swing(legs["far"], -35)
    else:   # fall
        swing(arms["near"], 120)
        swing(arms["far"], 100)
        swing(legs["near"], 10)
        swing(legs["far"], -12)


def build_cinder_imp():
    """A small horned fire imp with bat wings, a flame-tipped tail and a burning orb in its hand."""
    c = {"skin": "#c2442a", "belly": "#e8864a", "horn": "#3a2a2a", "wing": "#7a2418", "eye": "#fff2a0", "orb": "#ffb040", "dark": "#5a1a10"}
    root = empty("imp_root")
    body = empty("imp_body", root, (0, 0, 12 * PX))
    ellipsoid("imp_torso", (4.6 * PX, 0.36, 6 * PX), (0, 0, 0), c["skin"], body, (16, 10))
    ellipsoid("imp_belly", (2.8 * PX, 0.3, 4 * PX), (1.0 * PX, -0.12, -0.8 * PX), c["belly"], body, (12, 8))
    head = empty("imp_head", body, (1 * PX, 0, 8.6 * PX))
    ellipsoid("imp_skull", (4.4 * PX, 0.34, 4 * PX), (0, 0, 0), c["skin"], head, (14, 10))
    for k, (x, rz) in enumerate(((-2.4, -30), (2.0, 25))):
        cone(f"imp_horn_{k}", 1.3 * PX, 0.2 * PX, 5 * PX, (x * PX, 0, 4.4 * PX), c["horn"], head, rot=(0, math.radians(rz), 0), segments=8)
    for k, x in enumerate((0.8, 2.8)):
        ellipsoid(f"imp_eye_{k}", (0.9 * PX, 0.2, 1.0 * PX), (x * PX, -0.34, 0.6 * PX), c["eye"], head, (8, 6))
    box("imp_grin", (3 * PX, 0.2, 0.8 * PX), (2.2 * PX, -0.33, -1.8 * PX), c["dark"], head, round_px=0)
    wings = []
    for side, sgn in (("near", 1), ("far", -1)):
        hinge = empty(f"imp_wing_{side}", body, (-2.5 * PX, 0.08 * sgn, 3 * PX))
        pts = [(0, 0), (-5 * PX, 6 * PX), (-10 * PX, 5 * PX), (-8 * PX, 0.5 * PX), (-9 * PX, -3 * PX), (-4 * PX, -1 * PX)]
        membrane(f"imp_wing_mem_{side}", pts, c["wing"] if sgn > 0 else c["dark"], hinge)
        wings.append(hinge)
    tail = empty("imp_tail", body, (-3 * PX, 0.05, -4 * PX))
    box("imp_tail_seg", (8 * PX, 0.1, 1.4 * PX), (-4 * PX, 0, -1 * PX), c["skin"], tail).rotation_euler = (0, math.radians(-25), 0)
    cone("imp_tail_flame", 1.6 * PX, 0.1 * PX, 3.5 * PX, (-8.4 * PX, 0, 1.8 * PX), c["orb"], tail, rot=(0, math.radians(-20), 0), segments=8)
    arm = empty("imp_arm", body, (2.6 * PX, -0.22, 3 * PX))
    box("imp_forearm", (2.2 * PX, 0.12, 7 * PX), (0, 0, -3.5 * PX), c["skin"], arm)
    orb = empty("imp_orb", arm, (0, -0.1, -7.6 * PX))
    ellipsoid("imp_orb_core", (2.4 * PX, 0.3, 2.4 * PX), (0, 0, 0), c["orb"], orb, (12, 8))
    ellipsoid("imp_orb_hot", (1.2 * PX, 0.32, 1.2 * PX), (-0.5 * PX, -0.1, 0.5 * PX), "#fff6d0", orb, (8, 6))
    legs = []
    for k, y in enumerate((-0.1, 0.1)):
        leg = empty(f"imp_leg_{k}", body, (0.6 * PX * (1 if k == 0 else -1), y, -5 * PX))
        box(f"imp_shin_{k}", (2 * PX, 0.14, 5 * PX), (0, 0, -2.5 * PX), c["skin"] if k == 0 else c["dark"], leg)
        legs.append(leg)
    return {"root": root, "body": body, "wings": wings, "arm": arm, "orb": orb, "legs": legs}


def pose_cinder_imp(rig, anim, i, n):
    body, wings, arm, orb = rig["body"], rig["wings"], rig["arm"], rig["orb"]
    body.location = (0, 0, 12 * PX)
    orb.scale = (1, 1, 1)
    for k, leg in enumerate(rig["legs"]):
        swing(leg, 20 if k == 0 else -10)
    if anim == "hover":
        body.location = (0, 0, (12 + [0, 1, 2, 1][i]) * PX)
        flap = [30, 5, -20, 5][i]
        swing(arm, 30)
    else:   # cast: raise the orb, it swells
        flap = 40
        swing(arm, [100, 150, 170][i])
        s = [1.2, 1.5, 1.8][i]
        orb.scale = (s, 1, s)
    for k, w in enumerate(wings):
        w.rotation_euler = (0, math.radians(flap if k == 0 else flap * 0.8), 0)


# --------------------------------------------------------------------------------------------- sheets
SHEETS = {
    # frame_px: square frame size; pivot_px: pivot from the bottom-left of a frame, in pixels
    "miner": {
        "frame_px": 64, "pivot_px": (32, 2), "outline": True, "meshType": "Tight",
        "animations": [("idle", 4, 6, True), ("walk", 8, 12, True), ("jump", 1, 1, False),
                       ("fall", 1, 1, False), ("use", 4, 14, False), ("attack", 4, 16, False)],
    },
    "calib": {
        "frame_px": 64, "pivot_px": (32, 32), "outline": False, "meshType": "Tight",
        "animations": [("still", 1, 1, False)],
    },
    "gloop": {
        "frame_px": 32, "pivot_px": (16, 1), "outline": True, "meshType": "Tight",
        "animations": [("idle", 4, 6, True), ("squash", 2, 8, False), ("jump", 1, 1, False), ("fall", 1, 1, False)],
    },
    "duskbat": {
        "frame_px": 48, "pivot_px": (24, 4), "outline": True, "meshType": "Tight",
        "animations": [("fly", 4, 12, True), ("flare", 2, 8, True), ("dive", 1, 1, False)],
    },
    "hollow": {
        "frame_px": 64, "pivot_px": (32, 2), "outline": True, "meshType": "Tight",
        "animations": [("idle", 2, 3, True), ("walk", 6, 8, True), ("windup", 2, 6, False), ("lunge", 1, 1, False), ("fall", 1, 1, False)],
    },
    "cinder_imp": {
        "frame_px": 48, "pivot_px": (24, 3), "outline": True, "meshType": "Tight",
        "animations": [("hover", 4, 8, True), ("cast", 3, 5, False)],
    },
}

BUILDERS = {
    "gloop": (build_gloop, pose_gloop), "duskbat": (build_duskbat, pose_duskbat),
    "hollow": (build_hollow, pose_hollow), "cinder_imp": (build_cinder_imp, pose_cinder_imp),
}


def render_sheet(name, spec, out_root):
    reset_scene()
    frame_px = spec["frame_px"]
    pvx, pvy = spec["pivot_px"]
    # camera centre so that world origin lands on the pivot pixel corner
    cam_center = ((frame_px / 2 - pvx) * PX, (frame_px / 2 - pvy) * PX)
    configure_render(frame_px, frame_px * PX, cam_center)
    nmat = normal_material()
    SHADING["on"] = name != "calib"

    if name in BUILDERS:
        build, pose = BUILDERS[name]
        rig = build()
        poser = lambda anim, i, n: pose(rig, anim, i, n)
    elif name == "miner":
        rig = build_miner()
        poser = lambda anim, i, n: pose_miner(rig, anim, i, n)
    elif name == "calib":
        build_calibration_sphere()
        poser = lambda anim, i, n: None
    else:
        raise ValueError(name)

    out_dir = os.path.join(out_root, name)
    os.makedirs(out_dir, exist_ok=True)
    frames, animations = [], []
    for anim, count, fps, loop in spec["animations"]:
        idx = []
        for i in range(count):
            poser(anim, i, count)
            frame_name = f"{name}_{anim}_{i}"
            render_pair(out_dir, frame_name, nmat)
            idx.append(len(frames))
            frames.append(frame_name)
        animations.append({"name": anim, "fps": fps, "loop": loop, "frames": idx})

    manifest = {
        "name": name, "frameWidth": frame_px, "frameHeight": frame_px, "pixelsPerUnit": PPU,
        "pivotPx": [pvx, pvy], "outline": spec["outline"], "meshType": spec["meshType"],
        "frames": frames, "animations": animations,
    }
    with open(os.path.join(out_dir, "frames.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
    print(f"[render_sprites] {name}: {len(frames)} frames -> {out_dir}")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--only", default="")
    args = ap.parse_args(argv)
    out_root = os.path.abspath(args.out)   # Blender resolves relative paths against the .blend, not the cwd
    for name, spec in SHEETS.items():
        if args.only and name != args.only:
            continue
        render_sheet(name, spec, out_root)


if __name__ == "__main__":
    main()
