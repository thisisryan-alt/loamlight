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
    for i, x in enumerate((0.3, 2.55)):
        B(f"brick{i}", x, 0.4, x + 0.2, 0.55, STONE_L, depth=1.02)


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


def obj_chair():
    B("seat", 0.12, 0.72, 0.92, 0.9, WOOD_L, depth=0.7)
    B("back", 0.12, 0.9, 0.28, 1.85, WOOD, depth=0.6)
    B("rail", 0.12, 1.5, 0.28, 1.62, WOOD_D, depth=0.62)
    for x in (0.14, 0.72):
        B(f"leg{x}", x, 0.0, x + 0.14, 0.75, WOOD_D, depth=0.5)


def obj_chest():
    B("body", 0.1, 0.0, 1.9, 1.05, "#a0743c", depth=1.0)
    B("lid", 0.05, 1.05, 1.95, 1.6, "#b8864a", depth=1.05)
    for x in (0.1, 1.75):
        B(f"band{x}", x, 0.0, x + 0.15, 1.6, IRON_D, depth=1.08)
    B("trim", 0.05, 1.0, 1.95, 1.12, IRON_D, depth=1.08)
    B("lock", 0.85, 0.8, 1.15, 1.2, "#e0b44a", depth=1.12)


def obj_door(open_=False):
    if not open_:
        B("slab", 0.1, 0.0, 0.9, 3.0, "#8a5e34", depth=0.25)
        for z in (0.35, 1.45, 2.55):
            B(f"rail{z}", 0.1, z, 0.9, z + 0.18, WOOD_D, depth=0.3)
        B("handle", 0.7, 1.35, 0.82, 1.6, "#d8b04a", depth=0.36)
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
    greens = ["#3d7a2e", "#4f8a3a", "#5aa043", "#6fb550", "#3a6e2c"]
    blobs = [(2.5, 1.6, 1.35, 2), (1.3, 1.5, 0.95, 0), (3.7, 1.5, 0.95, 1), (1.9, 2.6, 1.0, 3), (3.1, 2.6, 1.0, 2),
             (2.5, 3.2, 0.8, 3), (0.7, 1.0, 0.6, 4), (4.3, 1.0, 0.6, 4), (2.5, 0.8, 0.9, 1)]
    for i, (x, z, r, c) in enumerate(blobs):
        sphere(f"leaf{i}", r, (x, -0.2 * (i % 3), z), greens[c], subdiv=1)
    B("trunk_top", 2.25, 0.0, 2.75, 0.9, "#6b4a2c", depth=0.4, y=0.3)


def obj_pine_top():
    """Tiered conifer: stacked cones darkening toward the bottom, a dusting of snow on each tier."""
    tiers = [(0.55, 2.4, 1.15, "#24563f"), (1.35, 2.0, 1.0, "#2b6549"), (2.1, 1.55, 0.9, "#337353"), (2.8, 1.05, 0.85, "#3b805c")]
    for i, (z, r, hgt, col) in enumerate(tiers):
        bm = bmesh.new()
        bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=12, radius1=r, radius2=0.08, depth=hgt)
        for v in bm.verts:
            v.co = Vector((v.co.x + 2.5, v.co.y * 0.55 - 0.1, v.co.z + z + hgt / 2))
        rs.new_object(f"tier{i}", bm, col)
        bm = bmesh.new()
        bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=12, radius1=r * 0.55, radius2=0.05, depth=hgt * 0.45)
        for v in bm.verts:
            v.co = Vector((v.co.x + 2.5, v.co.y * 0.5 - 0.2, v.co.z + z + hgt * 0.78))
        rs.new_object(f"snow{i}", bm, "#e8f0f6")
    B("trunk_top", 2.3, 0.0, 2.7, 0.7, "#5a3d24", depth=0.4, y=0.3)


OBJECTS = {
    "workbench": (2, 1, obj_workbench), "furnace": (3, 2, obj_furnace), "anvil": (2, 1, obj_anvil),
    "table": (3, 2, obj_table), "chair": (1, 2, obj_chair), "chest": (2, 2, obj_chest),
    "door": (1, 3, lambda: obj_door(False)), "door_open": (1, 3, lambda: obj_door(True)),
    "bed": (4, 2, obj_bed), "tree_top": (5, 4, obj_tree_top), "pine_top": (5, 4, obj_pine_top),
}

# ------------------------------------------------------------------------------------------------- icons
METAL = {"copper": ("#c87533", "#8a4a24", "#f2a96a"), "iron": ("#a8a7b0", "#6d6c75", "#d8d7de"),
         "lumite": ("#3fd6c0", "#1f8a7e", "#b6fff2"), "cinderite": ("#ff7a2a", "#a3360f", "#ffd08a")}


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


ICONS = {
    "copper_pickaxe": lambda: icon_tool("pickaxe", "copper"), "iron_pickaxe": lambda: icon_tool("pickaxe", "iron"),
    "copper_axe": lambda: icon_tool("axe", "copper"), "iron_axe": lambda: icon_tool("axe", "iron"),
    "wooden_hammer": lambda: icon_tool("hammer", "copper"),
    "copper_bar": lambda: icon_bar("copper"), "iron_bar": lambda: icon_bar("iron"),
    "lumite_bar": lambda: icon_bar("lumite"), "cinderite_bar": lambda: icon_bar("cinderite"),
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
    with open(os.path.join(obj_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
    icons = sorted(n for n in list(ICONS) + [n for n in OBJECTS if n not in ("tree_top", "pine_top", "door_open")])
    with open(os.path.join(icon_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(icons, f, indent=2)
    print(f"[render_objects] {len(manifest)} objects, {len(icons)} icons -> {root}")


if __name__ == "__main__":
    main()
