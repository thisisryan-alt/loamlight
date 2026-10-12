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

## Deferred decisions

- **Chunk rendering, generated mesh vs Tilemap:** profile both in M1 with frequent edits, then choose.
- **Content data (JSON → ScriptableObjects):** the schema arrives with the first tiles in M1.
- **Save format:** versioned binary chunks plus a JSON header. Designed in M1 with round-trip tests.
