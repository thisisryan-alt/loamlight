<#
.SYNOPSIS
  Regenerates every art binary from scripts: Blender sprite renders -> packed sheets, plus Pillow tiles.
  Output lands in Assets/Art/Generated/Sprites; run ./Tools/unity.ps1 setup afterwards to import it.

.EXAMPLE
  ./Tools/build_art.ps1                # render + pack + tiles
  ./Tools/build_art.ps1 -Only miner    # re-render one Blender sheet (others keep their cached frames)
  ./Tools/build_art.ps1 -CheckDeterminism   # build twice and fail if any output byte differs
#>
param(
    [string]$Only = "",
    [switch]$CheckDeterminism,
    [string]$BlenderPath = $env:BLENDER,
    [string]$PythonPath = $env:LOAMLIGHT_PYTHON
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot/..").Path
$frames = "$root/Tools/.cache/frames"
$out = "$root/Assets/Art/Generated/Sprites"

if (-not $BlenderPath) {
    $BlenderPath = @("C:/Program Files/Blender Foundation/Blender 5.2/blender.exe",
                     "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $BlenderPath) { throw "Blender not found; set BLENDER to blender.exe" }
}
if (-not $PythonPath) {
    # no 'py' launcher on this machine and 'python' on PATH is the Store stub
    $PythonPath = @("$env:LOCALAPPDATA/Programs/Python/Python312-arm64/python.exe",
                    "$env:LOCALAPPDATA/Programs/Python/Python312/python.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $PythonPath) { throw "Python with Pillow+numpy not found; set LOAMLIGHT_PYTHON" }
}

function Invoke-Step([string]$label, [scriptblock]$cmd) {
    Write-Host "== $label"
    # native stderr must not become a terminating error under 'Stop' (Windows PowerShell 5.1)
    $ErrorActionPreference = "Continue"
    & $cmd
    $code = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($code -ne 0) { throw "$label failed (exit $code)" }
}

function Build-All {
    $bArgs = @("--background", "--factory-startup", "--python-exit-code", "1",
               "--python", "$root/Tools/blender/render_sprites.py", "--", "--out", $frames)
    if ($Only) { $bArgs += @("--only", $Only) }
    Invoke-Step "blender render" { & $BlenderPath @bArgs 2>&1 | Where-Object { "$_" -match "render_sprites|Error|Traceback|error" -and "$_" -notmatch "DeprecationWarning" } | ForEach-Object { Write-Host "  $_" } }
    Invoke-Step "pack sheets" { & $PythonPath "$root/Tools/art/pack_sheets.py" --frames $frames --out $out }
    Invoke-Step "tiles" { & $PythonPath "$root/Tools/art/gen_tiles.py" --out $out }
}

function Get-OutputHashes {
    Get-ChildItem $out -File | Where-Object { $_.Extension -ne ".meta" } | Sort-Object Name |
        ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16), $_.Name }
}

Build-All
if ($CheckDeterminism) {
    $first = Get-OutputHashes
    Remove-Item -Recurse -Force $frames
    Build-All
    $second = Get-OutputHashes
    $diff = Compare-Object $first $second
    if ($diff) { $diff | Format-Table | Out-String | Write-Host; throw "Art output is NOT deterministic" }
    Write-Host "Determinism: $(@($first).Count) files byte-identical across two clean builds"
    $first | ForEach-Object { Write-Host "  $_" }
}
