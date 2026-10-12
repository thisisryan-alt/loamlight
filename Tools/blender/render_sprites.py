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


def flat_material(hex_str):
    """Emission-only material: the rendered pixel is exactly the authored color."""
    if hex_str in _materials:
        return _materials[hex_str]
    mat = bpy.data.materials.new("albedo_" + hex_str.lstrip("#"))
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Color"].default_value = hex_color(hex_str)
    em.inputs["Strength"].default_value = 1.0
    nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    _materials[hex_str] = mat
    return mat


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
        legs[side] = hip

    # torso, belt, backpack
    box("torso", (9 * PX, 0.42, 14 * PX), (0, 0, hip_z + 7 * PX), c["shirt"], body)
    box("belt", (9.4 * PX, 0.44, 2 * PX), (0, 0, hip_z + 1 * PX), c["belt"], body)
    box("buckle", (1.6 * PX, 0.46, 1.6 * PX), (3.6 * PX, -0.005, hip_z + 1 * PX), c["buckle"], body)
    box("pack", (5 * PX, 0.36, 10 * PX), (-6.5 * PX, 0, hip_z + 8 * PX), c["pack"], body)
    box("pack_roll", (6 * PX, 0.4, 3 * PX), (-6 * PX, 0, hip_z + 14.5 * PX), c["pack_strap"], body)

    # head: low-poly ico sphere, helmet: half uv sphere with brim, headlamp
    head_z = hip_z + 19.5 * PX
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=1, radius=5.4 * PX)
    head = new_object("head", bm, c["skin"], body, (0.6 * PX, 0, head_z))
    # the eye box runs deep toward the camera so it pokes out of the curved side of the head
    box("eye", (1.2 * PX, 0.35, 1.6 * PX), (3.6 * PX, -0.175, 0.4 * PX), c["eye"], head)
    box("nose", (1.6 * PX, 0.1, 1.4 * PX), (5.6 * PX, 0, -0.8 * PX), "#cf8f62", head)

    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=10, v_segments=6, radius=6.2 * PX)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.co.z < -0.001], context="VERTS")
    new_object("helmet", bm, c["helmet"], head, (0, 0, 1.0 * PX))
    box("brim", (14.5 * PX, 0.5, 1.4 * PX), (0.6 * PX, 0, 0.6 * PX), c["helmet_rim"], head)
    box("lamp", (1.6 * PX, 0.16, 2.6 * PX), (6.4 * PX, -0.06, 3.2 * PX), c["lamp"], head)

    # arms (shoulder pivots), hands, pickaxe on the near hand
    shoulder_z = hip_z + 12.5 * PX
    arms = {}
    for side, y, shade in (("near", -0.27, c["shirt"]), ("far", 0.27, c["shirt_dark"])):
        sh = empty(f"arm_{side}", body, (1.0 * PX, y, shoulder_z))
        box(f"sleeve_{side}", (4 * PX, 0.13, 9 * PX), (0, 0, -4.5 * PX), shade, sh)
        box(f"hand_{side}", (3.6 * PX, 0.14, 3 * PX), (0, 0, -10 * PX), c["skin"], sh)
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


# --------------------------------------------------------------------------------------------- sheets
SHEETS = {
    # frame_px: square frame size; pivot_px: pivot from the bottom-left of a frame, in pixels
    "miner": {
        "frame_px": 64, "pivot_px": (32, 2), "outline": True, "meshType": "Tight",
        "animations": [("idle", 4, 6, True), ("walk", 8, 12, True), ("jump", 1, 1, False),
                       ("fall", 1, 1, False), ("use", 4, 14, False)],
    },
    "calib": {
        "frame_px": 64, "pivot_px": (32, 32), "outline": False, "meshType": "Tight",
        "animations": [("still", 1, 1, False)],
    },
}


def render_sheet(name, spec, out_root):
    reset_scene()
    frame_px = spec["frame_px"]
    pvx, pvy = spec["pivot_px"]
    # camera centre so that world origin lands on the pivot pixel corner
    cam_center = ((frame_px / 2 - pvx) * PX, (frame_px / 2 - pvy) * PX)
    configure_render(frame_px, frame_px * PX, cam_center)
    nmat = normal_material()

    if name == "miner":
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
