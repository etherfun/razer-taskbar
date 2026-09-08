# build.ps1 - Release x64 build + publish into dist/ (the canonical runtime).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1          # build + publish
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Test    # + unit tests
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Run     # start the widget even if it was not running
#   powershell -ExecutionPolicy Bypass -File build.ps1 -NoRun   # never (re)start the widget
#
# Flow follows docs/agent-csharp.md: stop the resident process first (a running
# exe locks razer-taskbar.dll and MSBuild's copy step fails silently), wipe
# dist/ (stale files mix into the publish output), publish, verify the deployed
# razer-taskbar.dll, then restart the widget from dist if it was running (or
# -Run was given). Autostart sync writes the running exe path, so once the
# widget runs from dist the HKCU Run entry follows on its own.
param(
    [switch]$Test,
    [switch]$Run,
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$app = Join-Path $root 'src\RazerTaskbar\RazerTaskbar.csproj'
$tests = Join-Path $root 'tests\RazerTaskbar.Tests\RazerTaskbar.Tests.csproj'
$dist = Join-Path $root 'dist'
$distExe = Join-Path $dist 'razer-taskbar.exe'
$distDll = Join-Path $dist 'razer-taskbar.dll'

# 1. Stop the resident widget; remember whether it was running.
$proc = Get-Process razer-taskbar -ErrorAction SilentlyContinue
$wasRunning = [bool]$proc
if ($wasRunning) {
    Write-Host 'Stopping razer-taskbar...'
    $proc | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

# 2. Clean dist so publish cannot mix in stale files. A held directory
# handle (Explorer tree pane, AV scan) only blocks removing the directory
# itself — an emptied dist is equivalent, so publish in place rather than
# fail; only leftover files are fatal.
if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $dist) {
        $left = @(Get-ChildItem $dist -Force)
        if ($left.Count -gt 0) { $left | Remove-Item -Recurse -Force }
        if (@(Get-ChildItem $dist -Force).Count -gt 0) {
            Write-Error 'dist is locked by another process and not empty; close the holder and retry'
            exit 1
        }
        Write-Host 'dist dir handle is held but the folder is empty; publishing in place.'
    }
}

# 3. Build + publish (publish implies build).
dotnet publish $app -c Release -p:Platform=x64 -o $dist --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 4. The exe is an apphost shell: deployment success shows on razer-taskbar.dll.
if (-not (Test-Path $distDll)) {
    Write-Error "deploy failed: $distDll missing"
    exit 1
}
Write-Host ("deployed: {0}  (dll {1})" -f $distExe, (Get-Item $distDll).LastWriteTime)

# 5. Optional unit tests (no --quiet: MSBuild argument parsing conflict).
if ($Test) {
    dotnet test $tests --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# 6. Restart the widget from dist.
if (($wasRunning -or $Run) -and -not $NoRun) {
    Start-Process -FilePath $distExe -WorkingDirectory $dist
    Write-Host "started: $distExe"
}
