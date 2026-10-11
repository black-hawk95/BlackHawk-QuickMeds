[CmdletBinding()]
param(
    [string]$SptPath
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'QuickMeds.csproj'
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist '_stage'
$zip = Join-Path $dist 'BlackHawk-QuickMeds-SPT-4.1.6-v1.2.0.zip'
$standaloneDll = Join-Path $dist 'QuickMeds.dll'
$buildInfoPath = Join-Path $dist 'BUILD-INFO.txt'

Write-Host '====================================================='
Write-Host ' BlackHawk QuickMeds - SPT 4.1.6 v1.2.0'
Write-Host '====================================================='
Write-Host ''
Write-Host 'This builder does NOT install or modify your SPT files.' -ForegroundColor Yellow
Write-Host 'It only reads your own SPT DLLs so the compiler validates the real 4.1.6 API.'
Write-Host 'The install ZIP contains only QuickMeds.dll.'
Write-Host ''

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found. Install a .NET SDK capable of building netstandard2.1.'
}

while ([string]::IsNullOrWhiteSpace($SptPath)) {
    Write-Host 'Enter your SPT 4.1.6 root folder.'
    Write-Host 'Example: C:\EFT .0.16.9.5.40743 - SPT 4.1 - Fika client'
    $SptPath = Read-Host 'SPT path'
}

$SptPath = [IO.Path]::GetFullPath($SptPath.Trim().Trim('"'))
$managed = Join-Path $SptPath 'EscapeFromTarkov_Data\Managed'

$required = @(
    (Join-Path $SptPath 'EscapeFromTarkov.exe'),
    (Join-Path $managed 'Assembly-CSharp.dll'),
    (Join-Path $managed 'ItemComponent.Types.dll'),
    (Join-Path $managed 'AnimationSystem.Types.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $SptPath 'BepInEx\core\BepInEx.dll'),
    (Join-Path $SptPath 'BepInEx\core\0Harmony.dll'),
    (Join-Path $SptPath 'BepInEx\plugins\spt\spt-common.dll'),
    (Join-Path $SptPath 'BepInEx\plugins\spt\spt-reflection.dll')
)

$missing = @($required | Where-Object { -not (Test-Path $_) })
if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host 'The selected folder is missing required SPT files:' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw 'Build stopped before compilation. No SPT files were changed.'
}

# A clean dist prevents old test files from being mistaken for the current build.
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "Building against: $SptPath"
Write-Host "dotnet SDK: $(& dotnet --version)"
Write-Host ''

& dotnet build $project -c Release -p:SptPath="$SptPath"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE. The compiler output above is authoritative; do not install this build if it does not compile cleanly."
}

$dll = Join-Path $root 'bin\Release\netstandard2.1\QuickMeds.dll'
if (-not (Test-Path $dll)) {
    throw "Compiler reported success but the DLL was not found: $dll"
}

Copy-Item $dll $standaloneDll -Force

# Runtime package: one DLL only. Documentation/testing files stay in the source package.
$packageDir = Join-Path $stage 'BepInEx\plugins\QuickMeds'
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
Copy-Item $dll (Join-Path $packageDir 'QuickMeds.dll') -Force

# Build provenance stays beside the install ZIP for bug reports; it is NOT installed into SPT.
$assemblyCSharp = Join-Path $managed 'Assembly-CSharp.dll'
$sptReflection = Join-Path $SptPath 'BepInEx\plugins\spt\spt-reflection.dll'
$fikaCore = Get-ChildItem -Path (Join-Path $SptPath 'BepInEx\plugins') -Filter 'Fika.Core.dll' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
$dllHash = (Get-FileHash $dll -Algorithm SHA256).Hash
$assemblyHash = (Get-FileHash $assemblyCSharp -Algorithm SHA256).Hash
$reflectionHash = (Get-FileHash $sptReflection -Algorithm SHA256).Hash
$fikaHash = if ($null -ne $fikaCore) { (Get-FileHash $fikaCore.FullName -Algorithm SHA256).Hash } else { 'not-present' }
$eftVersion = (Get-Item (Join-Path $SptPath 'EscapeFromTarkov.exe')).VersionInfo.FileVersion
$buildInfo = @(
    'BlackHawk QuickMeds v1.2.0',
    "Built UTC: $([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))",
    "dotnet SDK: $(& dotnet --version)",
    "EscapeFromTarkov.exe FileVersion: $eftVersion",
    "QuickMeds.dll SHA256: $dllHash",
    "Assembly-CSharp.dll SHA256: $assemblyHash",
    "spt-reflection.dll SHA256: $reflectionHash",
    "Fika.Core.dll SHA256: $fikaHash"
)
$buildInfo | Set-Content -Path $buildInfoPath -Encoding UTF8

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

# Keep the source tree clean. The compiled DLL is preserved in dist\QuickMeds.dll.
foreach ($generated in @((Join-Path $root 'bin'), (Join-Path $root 'obj'))) {
    if (Test-Path $generated) { Remove-Item $generated -Recurse -Force }
}

Write-Host ''
Write-Host '=====================================================' -ForegroundColor Green
Write-Host ' BUILD SUCCESSFUL - CLEAN RELEASE PACKAGE READY' -ForegroundColor Green
Write-Host '=====================================================' -ForegroundColor Green
Write-Host "Standalone DLL: $standaloneDll"
Write-Host "Install ZIP:    $zip"
Write-Host "Build info:     $buildInfoPath"
Write-Host "QuickMeds.dll SHA256: $dllHash"
Write-Host ''
Write-Host 'Install ZIP runtime contents:' -ForegroundColor Cyan
Write-Host '  BepInEx\plugins\QuickMeds\QuickMeds.dll'
Write-Host ''
Write-Host 'Nothing was copied into your SPT installation.' -ForegroundColor Yellow
Write-Host 'For a bug report, send the game LogOutput plus dist\BUILD-INFO.txt.'
