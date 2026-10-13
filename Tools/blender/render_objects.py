"""
Headless Blender renders for world objects (furniture, stations, doors, tree canopies) and item icons.

Objects are modelled in tile units (1 unit = 1 tile = 16 px) standing on y = 0 at x = 0..w, rendered front-on at
exactly w*16 x h*16 px with three passes:
  albedo    flat emission colours (Standard view transform)
  normal    camera-space normals encoded like every other Loamlight normal map (+Z toward the viewer)
  emission  only materials flagged as glowing (furnace fire), black elsewhere
Icons are 16x16 renders of items (and of each object scaled to fit), albedo + normal.

Output (all deterministic): Tools/.cache/objects/<name>_{albedo,normal,emission}.png,
Tools/.cache/icons/<item>_{albedo,normal}.png and a manifest.json per folder. Tools/art/gen_atlas.py consumes them.

  blender --background --factory-startup --python-exit-code 1 --python render_objects.py -- --out Tools/.cache
"""
import argparse
import json
import math
import os
import sys
import warnings

import bmesh
import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import render_sprites as rs  # noqa: E402  (shared helpers: materials, boxes, normal pass)

warnings.filterwarnings("ignore", category=DeprecationWarning)
PX = rs.PX
GLOW = set()   # material names that emit in the emission pass


def glow_material(hex_str):
    m = rs.flat_material(hex_str)
    GLOW.add(m.name)
    return m


def configure(width_px, height_px, center):
    rs.reset_scene()
    GLOW.clear()
    rs.configure_render(max(width_px, height_px), max(width_px, height_px) * PX, center)
    sc = bpy.context.scene
    sc.render.resolution_x = width_px
    sc.render.resolution_y = height_px


def cyl(name, radius, depth, center, color, axis="Y", verts=10, parent=None):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=verts, radius1=radius, radius2=radius, depth=depth)
    rot = {"X": (0, math.pi / 2, 0), "Y": (math.pi / 2, 0, 0), "Z": (0, 0, 0)}[axis]
    from mathutils import Euler
    m = Euler(rot).to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=m, verts=bm.verts)
    for v in bm.verts:
        v.co += Vector(center)
    return rs.new_object(name, bm, color, parent)


def sphere(name, radius, center, color, subdiv=1, parent=None, scale=(1, 1, 1)):
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=radius)
    for v in bm.verts:
        v.co = Vector((v.co.x * scale[0], v.co.y * scale[1], v.co.z * scale[2])) + Vector(center)
    return rs.new_object(name, bm, color, parent)


def B(name, x0, z0, x1, z1, color, depth=0.5, y=0.0, round_px=None):
    """Box from (x0, z0) to (x1, z1) in tile units (front view), depth along the view axis."""
    return rs.box(name, (x1 - x0, depth, z1 - z0), ((x0 + x1) / 2, y, (z0 + z1) / 2), color, round_px=round_px)


# ------------------------------------------------------------------------------------------------- objects
WOOD, WOOD_D, WOOD_L = "#9a6a3c", "#6e4a28", "#c08c56"
IRON, IRON_D, IRON_L = "#6d6c75", "#45444c", "#9a99a3"
STONE, STONE_D, STONE_L = "#7d7b84", "#55535c", "#a3a1aa"


def obj_workbench():
    B("top", 0.05, 0.62, 1.95, 0.86, WOOD_L, depth=0.7)
    B("apron", 0.15, 0.5, 1.85, 0.64, WOOD_D, depth=0.6)
    for x in (0.18, 1.62):
        B(f"leg{x}", x, 0.0, x + 0.2, 0.6, WOOD, depth=0.5)
    B("shelf", 0.2, 0.18, 1.8, 0.28, WOOD_D, depth=0.5)
    B("vise", 1.45, 0.86, 1.75, 1.0, IRON, depth=0.3)
    B("saw", 0.3, 0.86, 0.95, 0.92, IRON_L, depth=0.05, y=-0.2)
    for x in (0.65, 1.3):
        B(f"seam{x}", x, 0.62, x + 0.03, 0.86, WOOD_D, depth=0.72, round_px=0)
    B("hammer_handle", 1.0, 0.88, 1.36, 0.93, WOOD, depth=0.06, y=-0.25)
    B("hammer_head", 1.3, 0.86, 1.4, 1.0, IRON_D, depth=0.12, y=-0.25)
    for x in (0.3, 0.9, 1.5):
        B(f"box{x}", x, 0.28, x + 0.28, 0.48, "#7a5a3a" if x != 0.9 else "#5a6a7a", depth=0.4)


def obj_furnace():
    B("body", 0.1, 0.0, 2.9, 1.55, STONE, depth=1.0)
    B("cap", 0.0, 1.5, 3.0, 1.75, STONE_L, depth=1.1)
    B("chimney", 1.9, 1.7, 2.45, 2.0, STONE_D, depth=0.6)
    B("mouth_frame", 0.75, 0.2, 2.25, 1.15, STONE_D, depth=1.04)
    B("fire_back", 0.9, 0.25, 2.1, 1.0, "#3a1408", depth=1.06)
    fire = glow_material("#ffb347")
    for i, (x, h) in enumerate(((1.05, 0.55), (1.35, 0.75), (1.65, 0.6), (1.9, 0.45))):
        o = B(f"flame{i}", x - 0.11, 0.25, x + 0.11, 0.25 + h, "#ffb347", depth=0.2, y=-0.48)
        o.data.materials[0] = fire
    ember = glow_material("#ff6a2a")
    o = B("embers", 0.95, 0.25, 2.05, 0.38, "#ff6a2a", depth=0.2, y=-0.5)
    o.data.materials[0] = ember
    for row, z in enumerate((0.05, 0.42, 0.79, 1.16)):
        off = 0.0 if row % 2 == 0 else 0.22
        for k in range(7):
            x = 0.12 + off + k * 0.44
            if x + 0.38 > 2.88 or (0.72 < x + 0.38 and x < 2.28 and z < 1.15):
                continue
            B(f"brick{row}_{k}", x, z, x + 0.38, z + 0.32, STONE_L if (row + k) % 3 else STONE, depth=1.02)
    for k in range(4):
        B(f"grate{k}", 0.95 + k * 0.33, 0.22, 1.0 + k * 0.33, 0.62, IRON_D, depth=1.1, round_px=0)


def obj_anvil():
    B("stump", 0.55, 0.0, 1.45, 0.42, WOOD_D, depth=0.7)
    B("waist", 0.75, 0.42, 1.25, 0.6, IRON_D, depth=0.4)
    B("face", 0.25, 0.6, 1.75, 0.92, IRON, depth=0.5)
    bm = bmesh.new()   # horn
    bmesh.ops.create_cone(bm, cap_ends=True, segments=8, radius1=0.16, radius2=0.01, depth=0.55)
    from mathutils import Euler
    bmesh.ops.transform(bm, matrix=Euler((0, -math.pi / 2, 0)).to_matrix().to_4x4(), verts=bm.verts)
    for v in bm.verts:
        v.co += Vector((0.0, 0.0, 0.8))
    rs.new_object("horn", bm, IRON_L)


def obj_table():
    B("top", 0.0, 1.55, 3.0, 1.8, WOOD_L, depth=0.8)
    B("apron", 0.2, 1.35, 2.8, 1.56, WOOD_D, depth=0.7)
    for x in (0.25, 2.5):
        B(f"leg{x}", x, 0.0, x + 0.25, 1.4, WOOD, depth=0.5)
    B("cloth", 1.0, 1.8, 2.0, 1.86, "#d9cfb8", depth=0.6)
    sphere("mug", 0.14, (2.3, 0, 1.98), "#a85a2a", subdiv=1, scale=(1, 1, 1.3))
    B("plate", 0.45, 1.8, 0.95, 1.85, "#e8e0d0", depth=0.4)
    B("candle", 1.42, 1.86, 1.56, 2.2, "#f0e8d0", depth=0.12)
    flame = sphere("candle_flame", 0.06, (1.49, -0.02, 2.28), "#ffd27a", subdiv=1, scale=(1, 1, 1.6))
    flame.data.materials[0] = glow_material("#ffd27a")


def obj_chair():
    B("seat", 0.12, 0.72, 0.92, 0.9, WOOD_L, depth=0.7)
    B("back", 0.12, 0.9, 0.28, 1.85, WOOD, depth=0.6)
    B("rail", 0.12, 1.5, 0.28, 1.62, WOOD_D, depth=0.62)
    B("rail_low", 0.12, 1.12, 0.28, 1.22, WOOD_D, depth=0.62)
    B("cushion", 0.2, 0.9, 0.86, 0.98, "#a84a4a", depth=0.6)
    for x in (0.14, 0.72):
        B(f"leg{x}", x, 0.0, x + 0.14, 0.75, WOOD_D, depth=0.5)


def obj_chest():
    B("body", 0.1, 0.0, 1.9, 1.05, "#a0743c", depth=1.0)
    B("lid", 0.05, 1.05, 1.95, 1.6, "#b8864a", depth=1.05)
    for x in (0.1, 1.75):
        B(f"band{x}", x, 0.0, x + 0.15, 1.6, IRON_D, depth=1.08)
    B("trim", 0.05, 1.0, 1.95, 1.12, IRON_D, depth=1.08)
    B("lock", 0.85, 0.8, 1.15, 1.2, "#e0b44a", depth=1.12)
    B("keyhole", 0.97, 0.88, 1.03, 1.0, "#3a2a10", depth=1.14, round_px=0)
    for z in (0.35, 0.7):
        B(f"plank{z}", 0.25, z, 1.75, z + 0.03, "#7a5428", depth=1.02, round_px=0)
    B("lid_ridge", 0.25, 1.45, 1.75, 1.52, "#d09a5a", depth=1.07)


def obj_door(open_=False):
    if not open_:
        B("slab", 0.1, 0.0, 0.9, 3.0, "#8a5e34", depth=0.25)
        for z in (0.35, 1.45, 2.55):
            B(f"rail{z}", 0.1, z, 0.9, z + 0.18, WOOD_D, depth=0.3)
        B("handle", 0.7, 1.35, 0.82, 1.6, "#d8b04a", depth=0.36)
        for x in (0.35, 0.62):
            B(f"plankline{x}", x, 0.05, x + 0.03, 2.95, "#6e4a28", depth=0.27, round_px=0)
        for z in (0.4, 2.6):
            B(f"hinge{z}", 0.06, z, 0.34, z + 0.1, IRON_D, depth=0.34, round_px=0)
    else:
        # swung open toward the viewer: seen almost edge-on against the left jamb
        B("slab_edge", 0.08, 0.0, 0.32, 3.0, "#7a5230", depth=0.9, y=-0.35)
        B("hinge_face", 0.0, 0.0, 0.1, 3.0, WOOD_D, depth=0.3)
        B("handle", 0.32, 1.35, 0.42, 1.6, "#d8b04a", depth=0.12, y=-0.7)


def obj_bed():
    B("frame", 0.0, 0.25, 4.0, 0.62, WOOD, depth=1.0)
    B("head", 0.0, 0.25, 0.3, 1.75, WOOD_D, depth=1.05)
    B("foot", 3.7, 0.25, 4.0, 1.05, WOOD_D, depth=1.05)
    for x in (0.05, 3.75):
        B(f"leg{x}", x, 0.0, x + 0.2, 0.3, WOOD_D, depth=0.6)
    B("mattress", 0.3, 0.62, 3.7, 0.9, "#e8e0cc", depth=0.95)
    B("blanket", 1.2, 0.62, 3.72, 1.02, "#b04a4a", depth=0.97)
    B("fold", 1.2, 0.95, 1.5, 1.05, "#d06a5a", depth=0.98)
    sphere("pillow", 0.32, (0.75, 0, 1.02), "#f4efe2", subdiv=2, scale=(1.3, 1.1, 0.55))


def obj_tree_top():
    """Broad leafy crown (7 x 6 tiles): a few branches out of the trunk top, then many overlapping leaf clusters,
    darker and further back at the bottom/inside, brighter on the top-left, so the baked key light reads as volume."""
    import random
    rnd = random.Random(7)
    bark, bark_d = "#6b4a2c", "#4a321d"
    B("trunk_top", 3.25, 0.0, 3.75, 1.6, bark, depth=0.45, y=0.35)
    for k, (x0, z0, x1, z1) in enumerate(((3.5, 1.0, 1.6, 2.6), (3.5, 1.2, 5.4, 2.4), (3.5, 1.4, 3.2, 3.6))):
        dx, dz = x1 - x0, z1 - z0
        ln = math.hypot(dx, dz)
        o = rs.box(f"branch{k}", (0.22, 0.3, ln), ((x0 + x1) / 2, 0.3, (z0 + z1) / 2), bark_d, round_px=0)
        o.rotation_euler = (0, math.atan2(dx, dz), 0)
    greens = ["#2f6a2a", "#3d7a2e", "#4f8a3a", "#5aa043", "#6fb550", "#86c860"]
    clusters = [(3.5, 3.4, 1.7), (2.2, 2.9, 1.35), (4.8, 2.9, 1.35), (1.3, 2.1, 1.0), (5.7, 2.1, 1.0), (2.6, 4.4, 1.25),
                (4.4, 4.4, 1.25), (3.5, 5.0, 1.05), (1.6, 3.7, 0.95), (5.4, 3.7, 0.95), (3.5, 2.2, 1.2), (2.4, 1.6, 0.8),
                (4.6, 1.6, 0.8)]
    for i, (x, z, r) in enumerate(clusters):
        y = 0.2 + (0.25 if z < 2.5 else 0.0) - 0.15 * (i % 2)
        shade = greens[min(5, max(0, int((z - 1.4) / 0.75) + (1 if x < 3.5 else 0)))]
        sphere(f"leaf{i}", r, (x, y, z), shade, subdiv=2, scale=(1.0 + rnd.uniform(-0.08, 0.12), 0.8, 0.86))
        # a few tufts break the outline of each cluster
        for t in range(3):
            a = rnd.uniform(0.3, 2.8)
            sphere(f"tuft{i}_{t}", r * 0.38, (x + math.cos(a) * r * 0.85, y - 0.1, z + math.sin(a) * r * 0.8),
                   greens[min(5, greens.index(shade) + 1)], subdiv=1)


def obj_pine_top():
    """Tall conifer (5 x 7 tiles): stacked drooping tiers, darker underneath, a dusting of snow on each."""
    tiers = [(0.7, 2.45, 1.5, "#1f4d38"), (1.75, 2.15, 1.4, "#245640"), (2.8, 1.8, 1.3, "#2b6549"),
             (3.8, 1.45, 1.2, "#337353"), (4.75, 1.1, 1.1, "#3b805c"), (5.6, 0.7, 1.2, "#44895f")]
    for i, (z, r, hgt, col) in enumerate(tiers):
        bm = bmesh.new()
        bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=14, radius1=r, radius2=0.06, depth=hgt)
        for v in bm.verts:
            # droop: the rim sags at the tips
            sag = 0.18 * (abs(v.co.x) / max(r, 0.01)) ** 2 if v.co.z < 0 else 0.0
            v.co = Vector((v.co.x + 2.5, v.co.y * 0.55 - 0.1, v.co.z + z + hgt / 2 - sag))
        rs.new_object(f"tier{i}", bm, col, None)
        bm = bmesh.new()
        bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=14, radius1=r * 0.62, radius2=0.04, depth=hgt * 0.42)
        for v in bm.verts:
            v.co = Vector((v.co.x + 2.5 - 0.08, v.co.y * 0.5 - 0.22, v.co.z + z + hgt * 0.8))
        rs.new_object(f"snow{i}", bm, "#e8f0f6", None)
    B("trunk_top", 2.3, 0.0, 2.7, 0.9, "#5a3d24", depth=0.4, y=0.3)


def obj_warden_lantern():
    """The Gloamwarden's lantern, set on a post: a caged flame that lights a wide area."""
    B("post", 0.42, 0.0, 0.58, 1.1, "#3a2e2a", depth=0.3)
    B("foot", 0.2, 0.0, 0.8, 0.12, "#2a2220", depth=0.4)
    B("cage_l", 0.18, 1.1, 0.26, 1.85, "#7a6a4a", depth=0.36)
    B("cage_r", 0.74, 1.1, 0.82, 1.85, "#7a6a4a", depth=0.36)
    B("cage_top", 0.12, 1.82, 0.88, 1.94, "#4e4430", depth=0.42)
    B("cage_bottom", 0.12, 1.06, 0.88, 1.16, "#4e4430", depth=0.42)
    core = sphere("lantern_flame", 0.2, (0.5, -0.05, 1.47), "#ffd27a", subdiv=2, scale=(1, 1, 1.35))
    core.data.materials[0] = glow_material("#ffd27a")
    hot = sphere("lantern_hot", 0.1, (0.47, -0.16, 1.5), "#fff6d8", subdiv=1)
    hot.data.materials[0] = glow_material("#fff6d8")


OBJECTS = {
    "workbench": (2, 1, obj_workbench), "furnace": (3, 2, obj_furnace), "anvil": (2, 1, obj_anvil),
    "table": (3, 2, obj_table), "chair": (1, 2, obj_chair), "chest": (2, 2, obj_chest),
    "door": (1, 3, lambda: obj_door(False)), "door_open": (1, 3, lambda: obj_door(True)),
    "bed": (4, 2, obj_bed), "tree_top": (7, 6, obj_tree_top), "pine_top": (5, 7, obj_pine_top), "warden_lantern": (1, 2, obj_warden_lantern),
}

# ------------------------------------------------------------------------------------------------- icons
METAL = {"copper": ("#c87533", "#8a4a24", "#f2a96a"), "iron": ("#a8a7b0", "#6d6c75", "#d8d7de"),
         "lumite": ("#3fd6c0", "#1f8a7e", "#b6fff2"), "cinderite": ("#ff7a2a", "#a3360f", "#ffd08a"),
         "gloam": ("#7a5ab0", "#3a2a5a", "#d8c8ff")}


def icon_tool(kind, metal):
    """Tools lie on the diagonal: handle bottom-left to top-right, head at the top."""
    m = METAL[metal]
    root = rs.empty("tool_root")
    root.rotation_euler = (0, math.radians(45), 0)
    handle = rs.box("handle", (0.08, 0.08, 0.95), (0, 0, -0.05), "#8a6236", parent=root, round_px=0)
    if kind == "pickaxe":
        bm = bmesh.new()
        bmesh.ops.create_cube(bm, size=1.0)
        for v in bm.verts:
            v.co = Vector((v.co.x * 0.7, v.co.y * 0.12, v.co.z * 0.17))
            v.co.z += 0.34 - abs(v.co.x) * 0.35
        rs.new_object("pick_head", bm, m[0], root)
        rs.box("pick_shine", (0.3, 0.13, 0.05), (0, 0, 0.4), m[2], parent=root, round_px=0)
    elif kind == "axe":
        rs.box("axe_head", (0.32, 0.1, 0.34), (0.16, 0, 0.3), m[0], parent=root, round_px=0)
        rs.box("axe_edge", (0.06, 0.11, 0.38), (0.33, 0, 0.3), m[2], parent=root, round_px=0)
    elif kind == "hammer":
        rs.box("hammer_head", (0.5, 0.2, 0.24), (0, 0, 0.36), "#9a6a3c", parent=root, round_px=0)
        rs.box("hammer_band", (0.08, 0.21, 0.25), (0, 0, 0.36), "#6e4a28", parent=root, round_px=0)
    elif kind == "sword":
        handle.scale = (1, 1, 0.3)
        handle.location = (0, 0, -0.38)
        rs.box("guard", (0.3, 0.1, 0.06), (0, 0, -0.22), m[1], parent=root, round_px=0)
        rs.box("blade", (0.11, 0.05, 0.62), (0, 0, 0.12), m[2], parent=root, round_px=0)
        rs.box("fuller", (0.03, 0.06, 0.5), (0, 0, 0.1), m[0], parent=root, round_px=0)
    return root


def icon_bar(metal):
    m = METAL[metal]
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:   # trapezoid ingot
        top = v.co.z > 0
        v.co = Vector((v.co.x * (0.62 if top else 0.82), v.co.y * 0.4, v.co.z * 0.28))
    o = rs.new_object("bar", bm, m[0])
    rs.box("bar_top", (0.6, 0.38, 0.03), (0, 0, 0.14), m[2], round_px=0)
    o.location = (0, 0, -0.05)


def _diag(root, length):
    """Lay an item along the icon diagonal (handle bottom-left, business end top-right), centred, `length` long."""
    root.rotation_euler = (0, math.radians(45), 0)
    return root


def item_sword(metal, length=0.95, width=0.13, guard=0.32):
    m = METAL[metal]
    root = rs.empty("sword_root")
    _diag(root, length)
    grip = length * 0.24
    rs.box("grip", (0.07, 0.08, grip), (0, 0, -length / 2 + grip / 2), "#5a3a22", parent=root, round_px=0)
    rs.box("pommel", (0.12, 0.1, 0.07), (0, 0, -length / 2), m[1], parent=root, round_px=0)
    rs.box("guard", (guard, 0.1, 0.07), (0, 0, -length / 2 + grip), m[1], parent=root, round_px=0)
    blade = length - grip - 0.05
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:   # tapered blade with a point
        top = v.co.z > 0
        v.co = Vector((v.co.x * width * (0.25 if top else 1.0), v.co.y * 0.05, v.co.z * blade))
    o = rs.new_object("blade", bm, m[2], root)
    o.location = (0, 0, -length / 2 + grip + blade / 2 + 0.03)
    rs.box("fuller", (width * 0.25, 0.06, blade * 0.7), (0, -0.005, -length / 2 + grip + blade * 0.42), m[0], parent=root, round_px=0)
    return root


def item_spear(metal, length=1.0):
    m = METAL[metal]
    root = rs.empty("spear_root")
    _diag(root, length)
    rs.box("shaft", (0.05, 0.06, length * 0.78), (0, 0, -length * 0.11), "#6a4a2c", parent=root, round_px=0)
    for k in range(3):
        rs.box(f"wrap{k}", (0.07, 0.07, 0.03), (0, 0, -length * 0.3 + k * 0.05), "#3a2a1a", parent=root, round_px=0)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:   # leaf blade
        top = v.co.z > 0
        v.co = Vector((v.co.x * (0.03 if top else 0.16), v.co.y * 0.05, v.co.z * length * 0.26))
    o = rs.new_object("spear_blade", bm, m[2], root)
    o.location = (0, 0, length * 0.37)
    rs.box("spear_collar", (0.12, 0.08, 0.04), (0, 0, length * 0.24), m[1], parent=root, round_px=0)
    return root


def item_staff(length=1.0):
    root = rs.empty("staff_root")
    _diag(root, length)
    rs.box("staff_shaft", (0.06, 0.06, length * 0.8), (0, 0, -length * 0.1), "#3a2a2a", parent=root, round_px=0)
    for k in range(2):
        rs.box(f"staff_band{k}", (0.09, 0.07, 0.04), (0, 0, length * (0.18 - k * 0.3)), "#c87533", parent=root, round_px=0)
    claw = rs.box("staff_claw", (0.22, 0.07, 0.06), (0, 0, length * 0.3), "#5a1a10", parent=root, round_px=0)
    orb = sphere("staff_orb", length * 0.12, (0, -0.05, length * 0.4), "#ffb040", subdiv=2, parent=root)
    orb.data.materials[0] = glow_material("#ffb040")
    sphere("staff_orb_hot", length * 0.06, (-0.02, -0.12, length * 0.42), "#fff6d0", subdiv=1, parent=root)
    return root


def item_bow():
    root = rs.empty("bow_root")
    root.rotation_euler = (0, math.radians(45), 0)
    # the limb: a thin arc of short boxes; string straight across
    for k in range(9):
        a = math.radians(-60 + k * 15)
        x, z = math.cos(a) * 0.32 - 0.18, math.sin(a) * 0.46
        b = rs.box(f"limb{k}", (0.07, 0.07, 0.13), (x, 0, z), "#8a5a2c" if k % 4 else "#5a3a1e", parent=root, round_px=0)
        b.rotation_euler = (0, -a, 0)
    rs.box("bow_string", (0.015, 0.03, 0.8), (-0.02, -0.01, 0), "#e8e0c8", parent=root, round_px=0)
    rs.box("bow_grip", (0.09, 0.08, 0.14), (0.14, 0, 0), "#3a2a1a", parent=root, round_px=0)
    return root


def item_arrow():
    root = rs.empty("arrow_root")
    _diag(root, 1.0)
    rs.box("arrow_shaft", (0.04, 0.04, 0.8), (0, 0, -0.05), "#a07a4a", parent=root, round_px=0)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=4, radius1=0.07, radius2=0.0, depth=0.16)
    o = rs.new_object("arrow_head", bm, "#b8bcc6", root)
    o.location = (0, 0, 0.42)
    for sgn in (-1, 1):
        rs.box(f"fletch{sgn}", (0.1, 0.02, 0.14), (sgn * 0.05, 0, -0.38), "#e8e4dc", parent=root, round_px=0)
    return root


def item_gel():
    sphere("gel", 0.34, (0, 0, -0.1), "#5fbf4a", subdiv=2, scale=(1.2, 1, 0.8))
    sphere("gel_shine", 0.09, (-0.14, -0.3, 0.06), "#d8ffc0", subdiv=1)


def item_wing():
    pts = [(-0.4, -0.2), (-0.1, 0.35), (0.2, 0.42), (0.45, 0.2), (0.3, 0.0), (0.38, -0.25), (0.1, -0.1), (-0.05, -0.35)]
    rs.membrane("wing", pts, "#3b2c4c")
    rs.box("wing_bone", (0.7, 0.08, 0.04), (0.05, -0.05, 0.12), "#2a1f36", round_px=0).rotation_euler = (0, math.radians(-20), 0)


def item_cloth():
    for k, (x, z, w, h, c) in enumerate(((-0.1, 0.05, 0.6, 0.5, "#4a4f5a"), (0.1, -0.1, 0.5, 0.4, "#3a3e48"), (0.0, 0.2, 0.4, 0.15, "#5a606c"))):
        rs.box(f"cloth{k}", (w, 0.08 + 0.02 * k, h), (x, -0.02 * k, z), c)


def item_core():
    o = sphere("core", 0.3, (0, 0, -0.05), "#ff7a2a", subdiv=2)
    o.data.materials[0] = glow_material("#ff7a2a")
    sphere("core_hot", 0.15, (-0.06, -0.2, 0.02), "#fff0b0", subdiv=1)
    cone_ = cyl("flame", 0.12, 0.3, (0.05, 0, 0.3), "#ffb040", axis="Z", verts=6)


def item_brew():
    cyl("bottle", 0.24, 0.5, (0, 0, -0.15), "#9fd6f0", axis="Z", verts=12)
    cyl("brew", 0.22, 0.32, (0, -0.02, -0.22), "#3a8af0", axis="Z", verts=12)
    cyl("neck", 0.09, 0.2, (0, 0, 0.2), "#9fd6f0", axis="Z", verts=8)
    cyl("cork", 0.1, 0.1, (0, 0, 0.34), "#8a6236", axis="Z", verts=8)
    sphere("glint", 0.05, (-0.12, -0.24, -0.05), "#ffffff", subdiv=1)


def item_heartroot():
    sphere("lobe_l", 0.2, (-0.13, 0, 0.08), "#c2364a", subdiv=2)
    sphere("lobe_r", 0.2, (0.13, 0, 0.08), "#c2364a", subdiv=2)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=10, radius1=0.33, radius2=0.0, depth=0.42)
    for v in bm.verts:
        v.co.z = -v.co.z
    o = rs.new_object("tip", bm, "#a82a3c", None)
    o.location = (0, 0, -0.14)
    for k, (x, rz) in enumerate(((-0.1, 20), (0.08, -30))):
        rs.box(f"root{k}", (0.03, 0.04, 0.22), (x, 0, -0.38), "#6a4a2c", round_px=0).rotation_euler = (0, math.radians(rz), 0)
    sphere("shine", 0.05, (-0.16, -0.18, 0.16), "#ffd0d8", subdiv=1)


def item_charm():
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=False, cap_tris=False, segments=16, radius1=0.34, radius2=0.34, depth=0.1)
    o = rs.new_object("ring", bm, "#3fd6c0", None)
    o.rotation_euler = (math.radians(90), 0, 0)
    o.location = (0, 0, -0.08)
    rs.box("wick", (0.06, 0.08, 0.36), (0, -0.02, 0.0), "#e8e0c8", round_px=0)
    f = sphere("charm_flame", 0.1, (0, -0.06, 0.24), "#ffd27a", subdiv=1, scale=(1, 1, 1.5))
    f.data.materials[0] = glow_material("#ffd27a")
    for k, x in enumerate((-0.22, 0.22)):
        rs.box(f"tassel{k}", (0.05, 0.05, 0.22), (x, -0.02, -0.38), "#4a4f5a", round_px=0)


def item_shard():
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=5, radius1=0.2, radius2=0.0, depth=0.8)
    o = rs.new_object("shard", bm, "#5a3a8a", None)
    o.rotation_euler = (0, math.radians(25), 0)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=5, radius1=0.12, radius2=0.0, depth=0.45)
    o2 = rs.new_object("shard2", bm, "#7a5ab0", None)
    o2.rotation_euler = (0, math.radians(-30), 0)
    o2.location = (-0.18, -0.05, -0.15)


ICONS = {
    "copper_pickaxe": lambda: icon_tool("pickaxe", "copper"), "iron_pickaxe": lambda: icon_tool("pickaxe", "iron"),
    "copper_axe": lambda: icon_tool("axe", "copper"), "iron_axe": lambda: icon_tool("axe", "iron"),
    "wooden_hammer": lambda: icon_tool("hammer", "copper"),
    "copper_bar": lambda: icon_bar("copper"), "iron_bar": lambda: icon_bar("iron"),
    "lumite_bar": lambda: icon_bar("lumite"), "cinderite_bar": lambda: icon_bar("cinderite"),
    "lumite_pickaxe": lambda: icon_tool("pickaxe", "lumite"), "cinderite_pickaxe": lambda: icon_tool("pickaxe", "cinderite"),
    "copper_shortsword": lambda: item_sword("copper", 0.82, 0.12, 0.26), "iron_broadsword": lambda: item_sword("iron", 1.0, 0.18, 0.4),
    "lumite_glaive": lambda: item_spear("lumite"), "ember_staff": item_staff, "wooden_bow": item_bow, "wooden_arrow": item_arrow,
    "gloop_gel": item_gel, "bat_wing": item_wing, "husk_cloth": item_cloth, "ember_core": item_core, "glowcap_brew": item_brew,
    "heartroot": item_heartroot, "wick_charm": item_charm, "gloam_shard": item_shard,
    "gloamblade": lambda: item_sword("gloam", 1.0, 0.22, 0.44),
}


# ------------------------------------------------------------------------------------------------- render
def render_passes(out_dir, name, nmat, emission=True):
    sc = bpy.context.scene
    vl = bpy.context.view_layer
    vl.update()
    vl.material_override = None
    sc.view_settings.view_transform = "Standard"
    sc.render.filepath = os.path.join(out_dir, name + "_albedo.png")
    bpy.ops.render.render(write_still=True)

    vl.material_override = nmat
    sc.view_settings.view_transform = "Raw"
    sc.render.filepath = os.path.join(out_dir, name + "_normal.png")
    bpy.ops.render.render(write_still=True)
    vl.material_override = None

    if emission:
        black = rs.flat_material("#000000")
        saved = []
        for o in sc.objects:
            if o.type != "MESH":
                continue
            saved.append((o, list(o.data.materials)))
            for i, m in enumerate(o.data.materials):
                if m.name not in GLOW:
                    o.data.materials[i] = black
        sc.view_settings.view_transform = "Standard"
        sc.render.filepath = os.path.join(out_dir, name + "_emission.png")
        bpy.ops.render.render(write_still=True)
        for o, mats in saved:
            for i, m in enumerate(mats):
                o.data.materials[i] = m


def render_object(name, spec, out_dir):
    w, h, build = spec
    configure(w * 16, h * 16, (w / 2, h / 2))
    nmat = rs.normal_material()
    build()
    render_passes(out_dir, name, nmat, emission=True)
    return {"name": name, "w": w, "h": h}


def render_object_icon(name, spec, out_dir):
    """Furniture item icon: the object scaled to fit 14 px inside a 16 px frame."""
    w, h, build = spec
    configure(16, 16, (0, 0))
    nmat = rs.normal_material()
    build()
    s = 0.875 / max(w, h)
    for o in list(bpy.context.scene.objects):
        if o.type == "MESH" and o.parent is None:
            o.location = Vector(((o.location.x - w / 2) * s, o.location.y * s, (o.location.z - h / 2) * s))
            o.scale = (s, s, s)
    render_passes(out_dir, name, nmat, emission=False)


def render_icon(name, build, out_dir):
    configure(16, 16, (0, 0))
    nmat = rs.normal_material()
    build()
    render_passes(out_dir, name, nmat, emission=False)


HELD = {   # item id: (frame px, builder) - the model spans the frame diagonal
    "copper_shortsword": (24, lambda: item_sword("copper", 0.82, 0.12, 0.26)),
    "iron_broadsword": (36, lambda: item_sword("iron", 1.0, 0.18, 0.4)),
    "lumite_glaive": (52, lambda: item_spear("lumite")),
    "ember_staff": (32, item_staff),
    "wooden_bow": (24, item_bow),
    "gloamblade": (42, lambda: item_sword("gloam", 1.0, 0.22, 0.44)),
}


def render_held(name, spec, out_dir):
    px, build = spec
    configure(px, px, (0, 0))
    nmat = rs.normal_material()
    build()
    # icon models are built for a 16 px frame (about 1.25 units on the diagonal); scale to this frame
    s = px / 16.0
    for o in list(bpy.context.scene.objects):
        if o.type in ("MESH", "EMPTY") and o.parent is None:
            o.location = Vector((o.location.x * s, o.location.y * s, o.location.z * s))
            o.scale = (s, s, s)
    render_passes(out_dir, name, nmat, emission=False)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    args = ap.parse_args(argv)
    root = os.path.abspath(args.out)
    obj_dir, icon_dir = os.path.join(root, "objects"), os.path.join(root, "icons")
    os.makedirs(obj_dir, exist_ok=True)
    os.makedirs(icon_dir, exist_ok=True)
    manifest = []
    for name, spec in OBJECTS.items():
        manifest.append(render_object(name, spec, obj_dir))
        if name not in ("tree_top", "pine_top", "door_open"):
            render_object_icon(name, spec, icon_dir)
    for name, build in ICONS.items():
        render_icon(name, build, icon_dir)
    held_dir = os.path.join(root, "held")
    os.makedirs(held_dir, exist_ok=True)
    for name, spec in HELD.items():
        render_held(name, spec, held_dir)
    with open(os.path.join(held_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({k: v[0] for k, v in HELD.items()}, f, indent=2)
    with open(os.path.join(obj_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
    icons = sorted(n for n in list(ICONS) + [n for n in OBJECTS if n not in ("tree_top", "pine_top", "door_open")])
    with open(os.path.join(icon_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(icons, f, indent=2)
    print(f"[render_objects] {len(manifest)} objects, {len(icons)} icons -> {root}")


if __name__ == "__main__":
    main()
