# Loamlight: progress log

Working title **Loamlight**, an original 2D sandbox survival game (dig, build, craft, fight, explore). It can be renamed later; the name only appears in the product name, namespaces (`Loamlight.*`) and the executable.

This file is the resume point. A fresh session should be able to continue from here alone. `DESIGN.md` holds the design calls and data formats.

## How to run things

All commands are run from the repo root in PowerShell.

| What | Command |
|---|---|
| Open, resolve packages, compile | `./Tools/unity.ps1 import` |
| Regenerate settings, render pipeline, art import, scenes | `./Tools/unity.ps1 setup` (idempotent) |
| Re-render Blender sprites and pack the sheets | `./Tools/build_art.ps1` |
| Tests | `./Tools/unity.ps1 test -Platform EditMode` / `-Platform PlayMode` |
| Development player build | `./Tools/unity.ps1 player` → `Builds/Windows/Loamlight.exe` |
| Screenshot capture on the dev build | `./Tools/unity.ps1 capture -Scene PipelineDemo` → `Screenshots/PipelineDemo/` |

`unity.ps1` scans every Unity or player log for errors afterwards and exits non-zero if it finds any. A zero exit code from Unity alone is not trusted.

Environment: Unity 6000.4.11f1 (arm64 editor, "Supported" stream; the user chose it over installing an LTS), Blender 5.2.1, Python 3.12 arm64 with Pillow and numpy at `%LOCALAPPDATA%\Programs\Python\Python312-arm64\python.exe` (there is no `py` launcher). The machine is a Snapdragon X with an Adreno GPU and 16 GB of RAM, so it is a reasonable stand-in for a "mid-range laptop".

---

## M0: Setup (in progress)

### Plan
1. Create the project on 6000.4.11f1 with URP (2D Renderer), Input System, Test Framework, Burst, Collections, Mathematics and 2D Sprite. Jobs ships in the engine core, so there is no separate package. Force Text serialization, Visible Meta Files, Unity `.gitignore`.
2. Write `Tools/unity.ps1` as the batch driver for import, setup, exec, test, player and capture, with log scanning after every run.
3. Write `ProjectSetup.RunAll`, an `-executeMethod` entry that builds every setting and asset from code: editor and player settings, the URP asset with the 2D Renderer data, quality levels and input handling.
4. Art pipeline, part 1. A Blender headless script models a low-poly miner and a calibration sphere, then renders an orthographic albedo pass and a camera-space normal pass for every frame. Pillow packs the frames into sheets with a JSON sidecar. The pipeline must be byte-deterministic.
5. Art pipeline, part 2. An editor importer applies point filtering, no compression, no mips, PPU 16, slices the sheets with stable sprite IDs, and assigns each normal map as the `_NormalMap` secondary texture.
6. A `PipelineDemo` scene, built from code: the animated miner, a calibration sphere, a flipped copy and a no-normal-map control copy, all under an orbiting colored Light2D in darkness.
7. A development player build with the `-llCapture <dir> -llScene <name>` debug flag: load the scene, take screenshots, write a report, quit.
8. EditMode tests for the import settings of every sheet, and a PlayMode test for the demo scene.
9. Write `DESIGN.md` and update this file.

### Done
- Project created, packages resolved (URP 17.4.0, Input System 1.19.0, Burst 1.8.30, Collections 6.4.0 built-in, Mathematics 1.3.3, Test Framework 1.6.0, 2D Sprite 1.0.0).
- `Tools/unity.ps1` batch driver.
