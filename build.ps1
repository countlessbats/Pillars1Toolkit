<#
.SYNOPSIS
    Builds the Pillars1Speedhack sidecar assembly and installs it into the game's Managed folder.

.DESCRIPTION
    Compiles src/Pillars1Speedhack.cs into LoomTimeAccelerator.dll and copies it to
    <GameDir>/PillarsOfEternity_Data/Managed/. The internal DLL name is intentionally
    LoomTimeAccelerator.dll to match the injected hook (see README).

.PARAMETER GameDir
    Path to the Pillars of Eternity install directory (contains PillarsOfEternity_Data).

.PARAMETER Csc
    Optional path to the Roslyn C# compiler (csc.exe). If omitted, common Build Tools /
    Visual Studio locations are probed, then PATH.

.EXAMPLE
    ./build.ps1 -GameDir "E:\SteamLibrary\steamapps\common\Pillars of Eternity"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir,

    [string]$Csc
)

$ErrorActionPreference = 'Stop'

$managed = Join-Path $GameDir 'PillarsOfEternity_Data\Managed'
if (-not (Test-Path $managed)) {
    throw "Managed folder not found: $managed  (is -GameDir correct?)"
}

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

$src    = Join-Path $PSScriptRoot 'src\Pillars1Speedhack.cs'
$outDll = Join-Path $managed 'LoomTimeAccelerator.dll'

$refs = @(
    'Assembly-CSharp.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.TextRenderingModule.dll'
) | ForEach-Object { "/reference:`"$(Join-Path $managed $_)`"" }

Write-Host "Compiler : $Csc"
Write-Host "Source   : $src"
Write-Host "Output   : $outDll"

$argList = @('/nologo', '/target:library', "/out:`"$outDll`"") + $refs + @("`"$src`"")
& $Csc @argList
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }

Write-Host "`nBuilt and installed LoomTimeAccelerator.dll." -ForegroundColor Green
Write-Host "If this is a first install, run install.ps1 (or the patcher) to inject the hook." -ForegroundColor Yellow
Write-Host "Restart the game to load the new build." -ForegroundColor Yellow
