<#
.SYNOPSIS
  Full verification pass: setup, EditMode + PlayMode tests, development player build, then every capture
  (pipeline/tile labs and each game scenario). Prints one summary line per step and exits non-zero on any failure.

.EXAMPLE
  ./Tools/verify.ps1                       # everything
  ./Tools/verify.ps1 -Scenarios m2,perf    # only these game scenarios (tests and labs still run)
  ./Tools/verify.ps1 -SkipTests
#>
param(
    [string[]]$Scenarios = @("m1", "m2", "m3", "m4", "m5", "m6", "perf"),
    [switch]$SkipTests,
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot/..").Path
$unity = "$root/Tools/unity.ps1"
$results = New-Object System.Collections.Generic.List[string]
$failed = $false

function Step([string]$name, [scriptblock]$body) {
    $out = & $body 2>&1 | Out-String
    $ok = $LASTEXITCODE -eq 0
    $line = ($out -split "`n" | Where-Object { $_ -match "Tests:|result:|Exit code" } | Select-Object -Last 1)
    $script:results.Add(("{0,-28} {1}  {2}" -f $name, ($(if ($ok) { "PASS" } else { "FAIL" })), "$line".Trim()))
    if (-not $ok) { $script:failed = $true; Write-Host $out }
}

Step "setup" { & $unity setup }
if (-not $SkipTests) {
    Step "tests EditMode" { & $unity test -Platform EditMode }
    Step "tests PlayMode" { & $unity test -Platform PlayMode }
}
if (-not $SkipBuild) { Step "player (development)" { & $unity player } }
Step "capture PipelineDemo" { & $unity capture -Scene PipelineDemo }
Step "capture TileLab" { & $unity capture -Scene TileLab }
$known = (Get-Content "$root/Assets/Scripts/Game/Scenarios/GameScenarioDirector.cs" -Raw)
foreach ($s in $Scenarios) {
    if ($known -notmatch "`"$s`"" -and $s -ne "m1" -and $s -ne "m6") { $results.Add(("{0,-28} SKIP  (scenario not implemented yet)" -f "scenario $s")); continue }
    $scene = if ($s -eq "m6") { "MainMenu" } else { "Game" }
    Step "scenario $s" { & $unity capture -Scene $scene -Scenario $s }
}

Write-Host ""
Write-Host "==== verify summary ===="
$results | ForEach-Object { Write-Host $_ }
if ($failed) { exit 1 }
exit 0
