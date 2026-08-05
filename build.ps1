<#
.SYNOPSIS
    Builds the Pillars1Toolkit BepInEx plugin.

.DESCRIPTION
    Compiles the toolkit sources into build/LoomTimeAccelerator.dll.

.PARAMETER GameDir
    Path to the Pillars of Eternity install directory (contains PillarsOfEternity_Data).

.PARAMETER Csc
    Optional path to the Roslyn C# compiler (csc.exe). If omitted, common Build Tools /
    Visual Studio locations are probed, then PATH.

.PARAMETER OutputDir
    Optional output folder. Defaults to the game's Managed folder.

.EXAMPLE
    ./build.ps1 -GameDir "E:\SteamLibrary\steamapps\common\Pillars of Eternity"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir,

    [string]$OutputDir,

    [string]$Csc
)

$ErrorActionPreference = 'Stop'

$managed = Join-Path $GameDir 'PillarsOfEternity_Data\Managed'
$bepCore = Join-Path $GameDir 'BepInEx\core'
if (-not (Test-Path $managed)) {
    throw "Managed folder not found: $managed  (is -GameDir correct?)"
}
if (-not (Test-Path $bepCore)) { throw "BepInEx core folder not found: $bepCore" }

if (-not $Csc) {
    $candidates = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe'
    )
    $Csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Csc) {
        $cmd = Get-Command csc.exe -ErrorAction SilentlyContinue
        if ($cmd) { $Csc = $cmd.Source }
    }
}
if (-not $Csc -or -not (Test-Path $Csc)) {
    throw "Could not locate csc.exe. Pass it explicitly with -Csc."
}

$src    = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' |
    Sort-Object Name |
    Select-Object -ExpandProperty FullName
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot 'build' }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$outDll = Join-Path $OutputDir 'LoomTimeAccelerator.dll'

$refs = @(
    'Assembly-CSharp.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.PhysicsModule.dll',
    'UnityEngine.TextRenderingModule.dll'
) | ForEach-Object { "/reference:$(Join-Path $managed $_)" }
$refs += "/reference:$(Join-Path $bepCore 'BepInEx.dll')"
$refs += "/reference:$(Join-Path $bepCore '0Harmony.dll')"

Write-Host "Compiler : $Csc"
Write-Host "Sources  : $($src -join ', ')"
Write-Host "Output   : $outDll"

$argList = @('/nologo', '/target:library', "/out:$outDll") + $refs + $src
& $Csc @argList
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }

Write-Host "`nBuilt LoomTimeAccelerator.dll." -ForegroundColor Green
Write-Host "Install under BepInEx\plugins\Pillars1Toolkit and restart the game." -ForegroundColor Yellow
