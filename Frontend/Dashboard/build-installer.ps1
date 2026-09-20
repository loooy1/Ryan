# ============================================================
# GRCS.Dashboard packaging script (Option C: lightweight launcher EXE)
# Outputs:
#   artifacts/launcher/GRCS.Dashboard.Launcher.exe   (self-contained single-file launcher)
#   artifacts/launcher/wwwroot/**                    (Blazor WASM static site, self-contained)
#   Installer GRCS.Dashboard.Setup.exe (requires Inno Setup 6, auto-built if iscc found)
# ============================================================
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$dashboardCsproj = Join-Path $root "GRCS.Dashboard.csproj"
$launcherCsproj = Join-Path (Split-Path -Parent $root) "GRCS.Dashboard.Launcher\GRCS.Dashboard.Launcher.csproj"
$issPath = Join-Path $root "packaging\installer.iss"

# 1. Publish GRCS.Dashboard (Blazor WASM) -> complete wwwroot
Write-Host "==> [1/4] Publishing GRCS.Dashboard (Blazor WASM) ..."
dotnet publish $dashboardCsproj -c Release -o (Join-Path $artifacts "dashboard") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dashboard publish failed" }
$dashWww = Join-Path $artifacts "dashboard\wwwroot"
if (-not (Test-Path (Join-Path $dashWww "index.html"))) { throw "dashboard wwwroot incomplete (index.html missing)" }

# 2. Publish launcher (self-contained single-file, win-x64)
Write-Host "==> [2/4] Publishing launcher (self-contained single-file, win-x64) ..."
dotnet publish $launcherCsproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $artifacts "launcher") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "launcher publish failed" }
$launcherOut = Join-Path $artifacts "launcher"

# 3. Copy dashboard wwwroot into launcher output + static asset manifest
Write-Host "==> [3/4] Copying wwwroot into launcher output ..."
$targetWww = Join-Path $launcherOut "wwwroot"
if (Test-Path $targetWww) { Remove-Item $targetWww -Recurse -Force }
Copy-Item $dashWww $targetWww -Recurse -Force
$manifestSrc = Join-Path $artifacts "dashboard\GRCS.Dashboard.staticwebassets.endpoints.json"
Copy-Item $manifestSrc $launcherOut -Force
Copy-Item $manifestSrc (Join-Path $targetWww "GRCS.Dashboard.staticwebassets.endpoints.json") -Force

# 3b. Materialize static asset aliases (fingerprinted -> plain names).
# .NET 8+ publish renames framework files to fingerprinted names (e.g. blazor.webassembly.xxx.js)
# while index.html/runtime reference plain names. A real host maps them via the endpoints
# manifest; here we copy each fingerprinted file to its plain-name route so the wwwroot is
# fully self-contained and the launcher needs no external manifest at runtime.
Write-Host "    Materializing static asset aliases ..."
$endpoints = Get-Content $manifestSrc -Raw | ConvertFrom-Json
$aliasCount = 0
foreach ($ep in $endpoints.Endpoints) {
    if ($ep.Selectors.Count -gt 0) { continue }
    if ([string]::IsNullOrWhiteSpace($ep.Route) -or [string]::IsNullOrWhiteSpace($ep.AssetFile)) { continue }
    if ($ep.Route -eq $ep.AssetFile) { continue }
    $target = Join-Path $targetWww ($ep.Route -replace '/', '\')
    $source = Join-Path $targetWww ($ep.AssetFile -replace '/', '\')
    if ((Test-Path -LiteralPath $target) -or -not (Test-Path -LiteralPath $source)) { continue }
    $dir = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item -LiteralPath $source -Destination $target -Force
    $aliasCount++
}
Write-Host "    Materialized $aliasCount aliases"

# 4. Build installer (Inno Setup)
Write-Host "==> [4/4] Building installer ..."
$iscc = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc $issPath
    if ($LASTEXITCODE -ne 0) { throw "iscc failed" }
    Write-Host "DONE. Installer: $(Join-Path $artifacts 'GRCS.Dashboard.Setup.exe')"
} else {
    Write-Host "WARN: Inno Setup (iscc) not found - installer skipped."
    Write-Host "      Install Inno Setup 6 and re-run this script, or run:"
    Write-Host "      iscc `"$issPath`""
}
Write-Host "DONE. Launcher dir: $launcherOut"