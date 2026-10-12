# Loamlight: design

An original 2D sandbox survival game: dig, build, craft, fight and explore a seeded, procedurally generated world. All content is original. No names, items, sprites or assets come from other games.

## Pillars

1. **Light that matters.** Sprites and tiles are normal-mapped and lit by real-time colored URP 2D lights. Torches, lava and glowing ores light up cave walls. Darkness underground is a resource problem, not a screen filter.
2. **A world that reacts.** Cellular liquids (water, lava, and obsidian-like rock where they meet), falling sand and gravel, and grass and moss that spread.
3. **Building that feels good.** Ghost-preview placement, drag-to-place lines and rectangles, a hammer that shapes slopes and half-blocks, and undo for recent placements.
4. **Combat with weight.** Hitstop, knockback, screen shake, i-frames and readable telegraphs. Every weapon feels distinct.
5. **Worldgen worth exploring.** Seeded and deterministic. Layered noise for terrain and caves, biomes by depth and region, ore veins, and hand-authored structure templates placed procedurally.

## Technical baseline

| Item | Decision | Why |
|---|---|---|
| Engine | Unity 6000.4.11f1, native arm64 editor | User's choice. It is in the "Supported" stream, not LTS; it is already installed and proven on this machine. |
| Rendering | URP 17.4, 2D Renderer, Linear color space, HDR on, MSAA off | 2D lights with normal maps. MSAA is pointless for point-filtered pixel art. |
| Packages | URP, Input System, Test Framework, Burst, Collections (built-in 6.4.0), Mathematics, 2D Sprite, uGUI | Jobs ships in the engine core (`Unity.Jobs`), so there is no separate package. 2D Sprite provides `ISpriteEditorDataProvider` for slicing and secondary textures from code. |
| Input | Input System only (the legacy manager is off) | |
| Serialization | Force Text, Visible Meta Files | Diffable, mergeable assets. |
| Generated assets | Scenes, prefabs, ScriptableObjects and render-pipeline assets come from editor scripts run with `-executeMethod`. Scene, prefab and asset YAML is never hand-edited. | Everything is reproducible from code. |
| Art binaries | Every PNG and WAV is produced by a script in `Tools/`. Generated files are committed so the `.meta` GUIDs stay stable. | No hand-made binaries, and references survive regeneration. |

## Pixel scale

- **16 pixels per unit, and 1 tile = 1 unit = 16×16 px.**
- The player is about 1.5 tiles wide and 2.75 tall. Character frames are **64×64 px**: the body is about 22×44 px, and the rest is headroom for tool swings, so an overhead pickaxe doesn't clip. The feet pivot sits on pixel row 2 of every frame, so the sprite pivot is `(0.5, 2/64)`. The packer warns if any silhouette touches a frame edge.
- The camera uses URP `PixelPerfectCamera` with a 960×540 reference resolution, about 60×34 tiles on screen. That scales by an integer 2× at 1080p and 4× at 4K.
- **Facing is flipped with `SpriteRenderer.flipX`, never with a negative `transform.localScale.x`.** This was measured in the M0 capture. URP's 2D normals pass negates the normal map's XY for `flipX`, so a flipped sprite lights correctly. A negative scale mirrors the tangent but builds the bitangent without the odd-negative-scale sign, which inverts normal Y: a sphere lit from below looked lit from above. The `PipelineDemo` scene keeps a `flipX` calibration sphere, and the capture check compares its shading with the unflipped one.

## Asset pipeline

```
Tools/blender/render_sprites.py   (Blender 5.2, headless)
   └─ builds the low-poly models in code, then renders every animation frame twice:
        albedo: emission-only flat colors, Standard view transform, no AA, no dither
        normal: camera-space normal encoded as rgb = n*0.5+0.5, Raw view transform
      → Tools/.cache/frames/<sheet>/<anim>_<i>_{albedo,normal}.png
Tools/art/pack_sheets.py           (Python 3.12 + Pillow)
   └─ adds a 1 px dark outline, packs the frames into a grid, writes the JSON sidecar
      → Assets/Art/Generated/Sprites/<sheet>.png, <sheet>_n.png, <sheet>.sheet.json
Loamlight.EditorTools.ArtImport    (Unity editor, -executeMethod)
   └─ point filtering, no compression, no mips, PPU 16; the normal map is imported as a NormalMap
      (linear); slices with stable sprite IDs; assigns _NormalMap as the secondary texture
```

The normal convention is tangent space equal to sprite space: +X is right, +Y is up, and +Z faces the viewer. A pixel facing the camera is `(128,128,255)`. The calibration sphere checks this end to end.

### Sprite-sheet sidecar (`<sheet>.sheet.json`)

```json
{
  "version": 1,
  "name": "miner",
  "frameWidth": 64, "frameHeight": 64,
  "columns": 8, "rows": 3,
  "pixelsPerUnit": 16,
  "pivot": [0.5, 0.03125],
  "meshType": "Tight",
  "normalMap": "miner_n.png",
  "animations": [
    { "name": "walk", "fps": 12, "loop": true, "frames": [8, 9, 10, 11, 12, 13, 14, 15] }
  ],
  "frames": [ { "name": "miner_walk_0", "x": 256, "y": 128, "w": 64, "h": 64 } ],
  "source": "Tools/blender/render_sprites.py + Tools/art/pack_sheets.py"
}
```

- `frames[].x/y` are measured from the **bottom-left** of the sheet, which is Unity's sprite-rect convention.
- `normalMap` is empty for sheets without one; the `calib_flat` control sheet is the example.
- `meshType` is `Tight` for characters and `FullRect` for tiles, because tiled draw mode needs `FullRect`.
- Sprite IDs are MD5-derived from `sprite/<sheet>/<frame>`, so re-importing never changes the `.meta` files.
- The importer also writes `<sheet>.anim.asset`, a `SpriteAnimationSet` holding named clips with fps and loop flags.

### Normal-map conventions (verified)

- **Cycles' camera space is left-handed, with +Z pointing away from the viewer.** The renderer negates Z when encoding, so a camera-facing pixel comes out as `(128,128,255)`. The packer checks the calibration sphere against analytic normals; the max error is 1/255.
- **Unity's side is checked from pixels on every capture.** `PipelineDemoDirector.VerifyShot` runs for each shot and requires three things. Against the identical sprite without a normal map, the normal map must change the shading, with a mean difference above 5. It must also brighten the side facing the light (lit-side contrast above 8). Finally, the `flipX` copy must match the original within 3. Measured values are 9.2, about +17 and 0.0.
- **Negative control:** with `-llDemoScaleFlip` the flipped sphere is mirrored by `scale.x = -1` instead. Verification then fails (flip difference 6.5 to 22.8), which shows the check really detects lighting errors.

## Idempotent generation

- `Tools/unity.ps1 setup` can be run any number of times, and a second run leaves `git status` clean.
- Settings are only written when they differ.
- **Scene rebuilds depend on a fingerprint file.** Unity gives new scene objects random local file IDs, and there is no public API to pin them. A scene is therefore rebuilt only when the SHA-256 of every script under `Assets/Scripts`, plus every sheet sidecar, changes. The hash is stored in `Assets/Scenes/.<Scene>.fingerprint`; Unity ignores dot-files, but git tracks them. `-llForceScenes` forces a rebuild.
- `Tools/build_art.ps1 -CheckDeterminism` builds the art twice from clean and byte-compares every output.

## Debug launch flags (player)

| Flag | Effect |
|---|---|
| `-llCapture <dir>` | Measure 240 frames, run the scene's `ICaptureDirector` shots, run `ICaptureVerifier` checks, write PNGs plus `report.txt`, then quit with exit code 1 on any failure (blank frame, failed verification, watchdog) |
| `-llScene <name>` | Scene to load for the capture |
| `-llDemoScaleFlip` | PipelineDemo negative control (see above) |

vsync is turned off during captures, so the reported frame times are honest.

## Assemblies

Each system gets its own assembly definition when it arrives:

| Assembly | Contents | Arrives |
|---|---|---|
| `Loamlight.Core` | Shared math, IDs, interfaces, fixed tick | M0 |
| `Loamlight.Game` | Composition root, debug launch flags, demo/test scene behaviours | M0 |
| `Loamlight.EditorTools` | Project setup, importers, scene builders, player build | M0 |
| `Loamlight.World` | Chunked tile grid (NativeArrays), worldgen jobs, chunk meshing and streaming, save format | M1 |
| `Loamlight.Player` | Controller, movement feel, mining and placing | M1 |
| `Loamlight.Inventory`, `Loamlight.Crafting` | Items, stacks, hotbar, recipes, stations | M2 |
| `Loamlight.Lighting`, `Loamlight.Liquids` | Light propagation and liquid cellular automata (Burst jobs on a fixed tick) | M3 |
| `Loamlight.Combat`, `Loamlight.AI` | Damage, hit feel, weapons, enemy brains | M4 |
| `Loamlight.UI` | HUD, menus | M2 and later |
| `Loamlight.Tests.EditMode`, `Loamlight.Tests.PlayMode` | Unity Test Framework | M0 |

## Whole-game architecture (written at the start of M1)

### World data
- The playable world is **4200×1200 tiles**. Storage is padded to chunk multiples: 4224×1216, which is 132×38 chunks of 32×32. Tiles outside the playable area read as an indestructible barrier.
- **Chunk-major structure-of-arrays.** All arrays are `NativeArray`s with `index = (chunk << 10) | (ly << 5) | lx`:

  | Array | Type | Contents |
  |---|---|---|
  | `block` | `ushort` | 0 means air |
  | `wall` | `ushort` | background wall |
  | `shape` | `byte` | Full, HalfBottom, SlopeUpRight, SlopeUpLeft |
  | `frame` | `byte` | cell offset within a multi-tile object, or autotile variant |
  | `liquid` | `byte` | amount, 0–255 |
  | `liquidType` | `byte` | none, water, lava |
  | light map | | computed separately, only for the view region |

- **Content is data.**
  - Tiles, walls, items, recipes, enemies, loot tables, ores and worldgen parameters are JSON in `Assets/Content/Data`.
  - An editor importer turns the JSON into ScriptableObjects, which the runtime `ContentDatabase` loads.
  - Numeric IDs are assigned in sorted-name order. Saves carry a name palette and remap on load, so adding content never breaks a save.

### Chunk rendering
- **Only chunks in view.** Each chunk in the view plus a margin gets a pooled renderer with two meshes:
  - walls, on sorting layer `Walls` with a darker vertex tint;
  - blocks and objects, on sorting layer `World`.
- **Mesh building.** Meshes are built by Burst jobs into `Mesh.MeshData`. The material is Sprite-Lit-Default with the tile atlas and its normal atlas. Vertices carry normal `(0,0,-1)` and tangent `(1,0,0,-1)`, which matches sprites.
- **Autotiling.** Terrain uses a 4-bit air-neighbour mask times 3 variants, generated by Pillow from the style parameters in `tiles.json`. Furniture, trees and torches are multi-tile objects whose 16 px cells live in the same atlas (Blender renders, sliced).
- **Shapes.** Slopes and half blocks change the geometry: a triangle, or a half-height quad.
- **Rebuilds.** Dirty chunks rebuild under a per-frame budget.
- **Mesh vs Tilemap** is benchmarked in M1; the result goes in the decision log.

### Lighting (pillar 1)
- **Light map.** A Burst job runs at 20 Hz over the view region, with a margin at least as large as the biggest emitter radius. It does a directional-sweep flood fill per RGB channel.
  - Sources: sky light (air columns open to the sky, with no background wall, scaled by time of day) and emitters (torches, lava, glowing ores, mushrooms).
  - Solid tiles attenuate light strongly and air only weakly, so caves are pitch black unless lit.
- **It is applied through a URP Sprite Light2D.** The light's cookie texture is the light map: one texel per tile, bilinear, with the light snapped to whole tiles. Normal maps are off for this light, because its position has no meaningful direction.
- **Point Light2Ds** come from a pool of about 32. They sit on the nearest emitters or emitter clusters with Accurate normal maps and supply the per-pixel highlights.
- **Global light and sky.** The global light is near zero. The sky and parallax background use an unlit material.

### Simulation, on a fixed tick separate from rendering
- **`SimulationRunner`** accumulates real time and runs these systems:

  | System | Rate |
  |---|---|
  | liquids | 30 Hz |
  | falling sand and gravel | 20 Hz |
  | light | 20 Hz |
  | grass and moss spread | 4 Hz |

- Jobs are scheduled and completed within the tick, so they never race main-thread edits.
- **Liquids.**
  - A vertical pass runs in parallel over columns, bottom-up. A horizontal pass then equalises in parallel over rows, alternating direction each tick. Each pass is parallel-safe because it only moves liquid within a column or within a row.
  - Water meeting lava turns into an obsidian-like block. Settled chunks sleep.
- **Region.** Only chunks within a few chunks of the camera are simulated.

### Worldgen (pillar 5)
- **Parallel Burst jobs** handle per-column surface height, material layers, caves (ridged "worm" noise plus caverns that grow with depth), ore blobs and walls.
- **Fixed-order work.** Anything that writes across chunks runs single-threaded in a fixed order with seeded RNG only: structures from JSON ASCII templates, trees, liquid fill and decoration.
- **Determinism is checked twice.** The world hash for a fixed seed is asserted in EditMode, and it is also printed by the player capture, so a Burst floating-point difference in the player would show up.

### Entities, with no Physics2D
- **Custom AABB-versus-tile collision.** It handles a 1-tile step-up, slopes as a height field, half blocks, one-way platforms and closed doors.
- **Fixed-step entity physics** at 60 Hz, with render interpolation.
- **The player controller is pure logic:** `Step(state, input, world) -> state`. EditMode tests cover coyote time, jump buffering, variable jump height, step-up, slopes, platforms, doors and tunneling.
- **Input goes through `IPlayerInput`,** so scenarios and bots drive the real player.
- **Hitstop is a gameplay time scale,** never `Time.timeScale`, so the UI, audio and the simulation keep running.

### Gameplay
- **Inventory:** 50 slots, with the first 10 as the hotbar. Stacking, magnetised pickups, chests.
- **Crafting:** recipes in JSON; stations count if they are within 4 tiles.
- **Building (pillar 3):**
  - ghost preview; drag to place a line; Shift-drag to fill a rectangle;
  - the hammer cycles a block's shape, or removes a wall;
  - Ctrl+Z undoes the last 20 placement batches and refunds the items.
- **Placeables:** doors (solid when closed), one-way platforms, furniture objects.
- **Combat (pillar 4):** health and i-frames, knockback, hitstop scaled per weapon, camera shake driven by a trauma value, and telegraphs (flash plus wind-up frames before every enemy attack).
  - Weapons: shortsword stab, broadsword arc, bow and arrows, an ember staff that uses mana.
  - Enemies: a hopper, a swooping flyer, a walker that throws, and a deep caster.
  - Boss: a multi-phase crystal golem summoned with an item.
  - Loot tables in JSON.
- **Day/night cycle,** with more surface spawns at night.

### UI, saves and audio
- **UI** is uGUI built from code, integer-scaled to stay pixel-art, using a generated bitmap pixel font. The built-in font is the fallback.
- **Saves** are binary:
  - Header: magic, version, metadata, then the name palettes.
  - Body: the arrays, Deflate-compressed, plus a JSON block for the player (position, stats, inventory, spawn point).
  - Older versions load through a migration chain, tested against a committed fixture save from each version.
- **SFX** come from a Python synth and are wired in as each feature lands.

### Assembly graph
An EditMode test enforces it.

| Assembly | Allowed references |
|---|---|
| `Core` | (none) |
| `Content` | Core |
| `World` | Core, Content |
| `Lighting` | Core, Content, World |
| `Liquids` | Core, Content, World |
| `Inventory` | Core, Content |
| `Crafting` | Core, Content, Inventory |
| `Combat` | Core, Content, World |
| `Player` | Core, Content, World, Inventory, Combat |
| `AI` | Core, Content, World, Combat |
| `UI` | Core, Content, Inventory, Crafting, Player, Combat |
| `Game` | everything (composition root) |

### Verification rule
A feature counts as done only when a **scenario in the real Game scene** exercises it: the player, driven through `IPlayerInput`, does the thing, and the state and pixels are checked. Unit tests alone don't count. This guards against "correct but never called" code.

Performance is measured on the full world in a **release** build, in the worst case: flowing water, many torches, enemies on screen. A run fails if p95 is above 16.7 ms. The dev build supplies the ProfilerRecorder breakdown and a `.raw` capture, which stays out of git.

## Decision log (by milestone)
- **M0:** see the sections above.

### M1
- **Chunk rendering is a Burst-built mesh, not a Tilemap.** Rebuilding 16 chunks took 0.64 ms with the mesh job and 2.82 ms with `Tilemap.SetTilesBlock`, in the PlayMode benchmark `ChunkRenderBenchmark`. The Tilemap number also excludes autotiling and normal maps.
- **Tiles use a custom `Loamlight/Tile Lit` shader.** URP's Sprite-Lit-Default reads `unity_SpriteColor` and `unity_SpriteProps`, which only SpriteRenderers fill reliably. Its normals pass multiplies by the sprite flip, which would zero normals on a MeshRenderer. The custom shader keeps URP's 2D lighting includes and adds an emission map.
- **Light is a hybrid.**
  - A tile light map (flood fill, with occlusion) applied through a Sprite Light2D cookie decides where it is dark.
  - Normal-mapped point lights on the nearest emitters add the per-pixel shape.
  - The global light is off, so caves are truly black.
- **Saves use RLE, not Deflate.** System.IO.Compression's DeflateStream needs MonoPosixHelper, which the Windows ARM64 player doesn't ship. The tile arrays are run-heavy anyway: a full world is 2 MB.
- **Movement numbers:**

  | Parameter | Value |
  |---|---|
  | run speed | 9.5 tiles/s |
  | ground acceleration | 75 tiles/s² |
  | gravity | 82 tiles/s² |
  | jump speed | 29.5 tiles/s (5.1–5.5 tile jump) |
  | jump cut | ×0.42 |
  | apex gravity | ×0.55 while held and under 4 tiles/s |
  | coyote time | 0.1 s |
  | jump buffer | 0.12 s |
  | step-up | 1.05 tiles |

- **Player body:** 1.25 × 2.6 tiles. It can straddle three tile columns, which matters for digging down.
- **Scenario waits are time-based, never frame counts.** The game runs at 900–1000 fps, where two frames are 2 ms.
