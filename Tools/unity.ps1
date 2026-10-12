<#
.SYNOPSIS
  Command-line driver for the Loamlight Unity project. Every editor run is batch mode, and every run's
  log is scanned for errors afterwards: a zero exit code alone is not trusted.

.EXAMPLE
  ./Tools/unity.ps1 import                          # open, resolve packages, compile, quit
  ./Tools/unity.ps1 setup                           # project settings, URP 2D, art import, scenes (idempotent)
  ./Tools/unity.ps1 exec -Method Loamlight.EditorTools.ArtImport.ImportAll
  ./Tools/unity.ps1 test -Platform EditMode
  ./Tools/unity.ps1 test -Platform PlayMode
  ./Tools/unity.ps1 player                          # development build -> Builds/Windows/Loamlight.exe
  ./Tools/unity.ps1 capture -Scene PipelineDemo     # run the dev build with -llCapture, screenshots -> Screenshots/<Scene>/
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("import", "setup", "exec", "test", "player", "capture")]
    [string]$Task,
    [string]$Method = "",
    [ValidateSet("EditMode", "PlayMode")]
    [string]$Platform = "EditMode",
    [string]$Scene = "PipelineDemo",
    [string]$Out = "",
    [string]$UnityPath = $env:UNITY_EDITOR,
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
$project = (Resolve-Path "$PSScriptRoot/..").Path
$playerExe = "$project/Builds/Windows/Loamlight.exe"

if (-not $UnityPath) {
    $version = (Get-Content "$project/ProjectSettings/ProjectVersion.txt" | Select-String "m_EditorVersion:").ToString().Split(":")[1].Trim()
    $candidates = @(
        "C:/Program Files/Unity/Hub/Editor/$version-arm64/Editor/Unity.exe",
        "C:/Program Files/Unity/Hub/Editor/$version/Editor/Unity.exe"
    )
    # prefer the native editor for this machine's architecture
    if ($env:PROCESSOR_ARCHITECTURE -ne "ARM64") { [array]::Reverse($candidates) }
    $UnityPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $UnityPath) { throw "Unity $version not found; set UNITY_EDITOR" }
}

New-Item -ItemType Directory -Force "$project/Logs" | Out-Null

# Lines that look like errors but are not ours (licensing chatter, package-manager network noise, etc.).
$ignore = "Curl error|DisplayProgressbar|Licensing|\[Licensing|Unable to listen|ErrorOnPackage|Error: 0 |errors: 0|0 errors|" +
          "EditorUpdateCheck|failed to query info queue|Analytics|UnityConnect|Cancelling DisplayDialog|Error_Unknown|Trusted CA|warning CS"
$errorPattern = "error CS\d+|Shader error|Compilation failed|Exception:|\[Error\]|^ERROR|Assertion failed|" +
                "NullReferenceException|MissingReferenceException|Failed to|failed to|Script attached to .* is missing|" +
                "The referenced script .* is missing|\[LL-ERROR\]"

function Show-LogIssues([string]$log) {
    if (-not (Test-Path $log)) { Write-Host "!! log not found: $log"; return 1 }
    $issues = Select-String -Path $log -Pattern $errorPattern -ErrorAction SilentlyContinue |
        Where-Object { $_.Line -notmatch $ignore } | Select-Object -First 60
    if ($issues) {
        Write-Host "---- log issues ($log) ----"
        $issues | ForEach-Object { Write-Host ("{0,6}: {1}" -f $_.LineNumber, $_.Line) }
        return @($issues).Count
    }
    Write-Host "Log clean: $log"
    return 0
}

function Invoke-Exe([string]$exe, [string[]]$exeArgs, [string]$log) {
    Write-Host "Run : $exe"
    Write-Host "Args: $($exeArgs -join ' ')"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    # Start-Process -Wait hangs on Unity's child processes; wait on the process handle instead.
    $proc = Start-Process -FilePath $exe -ArgumentList $exeArgs -PassThru -WindowStyle Hidden
    if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        $proc.Kill()
        throw "Timed out after $TimeoutMinutes minutes (log: $log)"
    }
    $code = $proc.ExitCode
    Write-Host ("Exit code {0} after {1:n0}s" -f $code, $sw.Elapsed.TotalSeconds)
    return $code
}

if ($Task -eq "capture") {
    if (-not (Test-Path $playerExe)) { throw "No player build at $playerExe; run ./Tools/unity.ps1 player first" }
    if (-not $Out) { $Out = "$project/Screenshots/$Scene" }
    if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
    New-Item -ItemType Directory -Force $Out | Out-Null
    $log = "$project/Logs/player-capture-$Scene.log"
    # Windowed so it renders on any display; never -nographics (nothing would be drawn).
    $code = Invoke-Exe $playerExe @("-llCapture", "`"$Out`"", "-llScene", $Scene, "-screen-fullscreen", "0",
        "-screen-width", "1280", "-screen-height", "720", "-logFile", "`"$log`"") $log
    $n = Show-LogIssues $log
    $shots = Get-ChildItem $Out -Filter *.png -ErrorAction SilentlyContinue
    Write-Host ("Screenshots: {0} in {1}" -f @($shots).Count, $Out)
    Get-Content "$Out/report.txt" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
    if ($code -ne 0 -or $n -gt 0 -or @($shots).Count -eq 0) { exit 1 }
    exit 0
}

$log = "$project/Logs/batch-$Task.log"
$unityArgs = @("-batchmode", "-projectPath", "`"$project`"", "-logFile", "`"$log`"")
switch ($Task) {
    "import" { $unityArgs += @("-quit") }
    "setup"  { $unityArgs += @("-quit", "-executeMethod", "Loamlight.EditorTools.ProjectSetup.RunAll") }
    "exec"   {
        if (-not $Method) { throw "exec needs -Method Namespace.Class.Method" }
        $log = "$project/Logs/batch-exec-$($Method.Split('.')[-1]).log"
        $unityArgs = @("-batchmode", "-projectPath", "`"$project`"", "-logFile", "`"$log`"", "-quit", "-executeMethod", $Method)
    }
    "test"   {
        $results = "$project/TestResults/$Platform.xml"
        New-Item -ItemType Directory -Force "$project/TestResults" | Out-Null
        if (Test-Path $results) { Remove-Item $results }
        $log = "$project/Logs/batch-test-$Platform.log"
        $unityArgs = @("-batchmode", "-projectPath", "`"$project`"", "-logFile", "`"$log`"",
                       "-runTests", "-testPlatform", $Platform, "-testResults", "`"$results`"")
    }
    "player" { $unityArgs += @("-quit", "-executeMethod", "Loamlight.EditorTools.PlayerBuild.BuildDevelopment") }
}

$code = Invoke-Exe $UnityPath $unityArgs $log
$n = Show-LogIssues $log

if ($Task -eq "test") {
    if (-not (Test-Path $results)) { Write-Host "!! no test results written"; exit 1 }
    [xml]$xml = Get-Content $results
    $run = $xml.'test-run'
    Write-Host ("Tests: {0} total, {1} passed, {2} failed, {3} skipped" -f $run.total, $run.passed, $run.failed, $run.skipped)
    $xml.SelectNodes("//test-case[@result='Failed']") | ForEach-Object {
        Write-Host "FAIL $($_.fullname)"
        Write-Host "     $($_.failure.message.'#cdata-section')"
    }
    # failing tests log exceptions on purpose; the XML is the source of truth for test runs
    if ($code -ne 0 -or [int]$run.failed -gt 0 -or [int]$run.total -eq 0) { exit 1 }
    exit 0
}
if ($code -ne 0 -or $n -gt 0) { exit 1 }
exit 0
