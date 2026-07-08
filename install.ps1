<#
.SYNOPSIS
    Installs Pillars1Toolkit into Pillars of Eternity 1 with NO compilation required.

.DESCRIPTION
    This script:
      1. copies the prebuilt sidecar (LoomTimeAccelerator.dll) into the game's Managed folder,
      2. backs up Assembly-CSharp.dll (once), and
      3. injects a single call to LoomTimeAccelerator.Bootstrap.Tick() at the top of
         GameState.Update() using the bundled Mono.Cecil.dll.

    It needs only Windows PowerShell (5.1+, built into Windows) -- no .NET SDK, no C# compiler,
    no .NET runtime install. Run it from the folder that also contains LoomTimeAccelerator.dll
    and Mono.Cecil.dll (the extracted release zip). Close the game before running.

    This mod coexists with other GameState.Update sidecar mods (e.g. Reaper Stance): each injects
    its own call and this installer is idempotent (it won't add a second copy of its own hook).

.PARAMETER GameDir
    Path to the Pillars of Eternity install directory (the folder that contains
    PillarsOfEternity_Data). If omitted, common Steam locations are probed, then you are prompted.

.EXAMPLE
    ./install.ps1 -GameDir "E:\SteamLibrary\steamapps\common\Pillars of Eternity"

.EXAMPLE
    ./install.ps1        # auto-detect / prompt
#>
[CmdletBinding()]
param(
    [string]$GameDir
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$SidecarName  = 'LoomTimeAccelerator.dll'
$BootstrapType = 'LoomTimeAccelerator.Bootstrap'
$RefName      = 'LoomTimeAccelerator'

function Find-GameDir {
    $guesses = @(
        'C:\Program Files (x86)\Steam\steamapps\common\Pillars of Eternity',
        'C:\Program Files\Steam\steamapps\common\Pillars of Eternity',
        'D:\SteamLibrary\steamapps\common\Pillars of Eternity',
        'E:\SteamLibrary\steamapps\common\Pillars of Eternity'
    )
    foreach ($g in $guesses) {
        if (Test-Path (Join-Path $g 'PillarsOfEternity_Data\Managed\Assembly-CSharp.dll')) { return $g }
    }
    return $null
}

function Test-GameDir([string]$dir) {
    return $dir -and (Test-Path (Join-Path $dir 'PillarsOfEternity_Data\Managed\Assembly-CSharp.dll'))
}

# Be forgiving if the user points a bit too deep (e.g. at PillarsOfEternity_Data or Managed):
# walk up until we reach the folder that actually contains the game assembly.
function Resolve-GameDir([string]$dir) {
    $try = $dir
    while ($try -and -not (Test-GameDir $try)) {
        $parent = Split-Path $try -Parent
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $try) { break }
        $try = $parent
    }
    if (Test-GameDir $try) { return $try }
    return $dir
}

# Resolve the game directory: explicit -GameDir, then auto-detect, then prompt the user.
if ($GameDir) { $GameDir = Resolve-GameDir ($GameDir.Trim().Trim('"')) }
if (-not (Test-GameDir $GameDir)) {
    $auto = Find-GameDir
    if (Test-GameDir $auto) { $GameDir = $auto }
}
if (-not (Test-GameDir $GameDir)) {
    Write-Host "Could not find your Pillars of Eternity installation automatically." -ForegroundColor Yellow
    Write-Host "It's the folder that contains 'PillarsOfEternity_Data'" -ForegroundColor DarkGray
    Write-Host "(for example: ...\steamapps\common\Pillars of Eternity)." -ForegroundColor DarkGray
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $entry = Read-Host "Enter the path to your Pillars of Eternity folder (leave blank to cancel)"
        if ([string]::IsNullOrWhiteSpace($entry)) { throw "Installation cancelled." }
        $candidate = Resolve-GameDir ($entry.Trim().Trim('"'))
        if (Test-GameDir $candidate) { $GameDir = $candidate; break }
        Write-Host "That folder doesn't contain PillarsOfEternity_Data\Managed\Assembly-CSharp.dll. Try again." -ForegroundColor Yellow
    }
    if (-not (Test-GameDir $GameDir)) { throw "Could not locate the game after several attempts." }
}

Write-Host "Game folder: $GameDir" -ForegroundColor DarkGray

$managed    = Join-Path $GameDir 'PillarsOfEternity_Data\Managed'
$asmPath    = Join-Path $managed 'Assembly-CSharp.dll'
$sidecarSrc = Join-Path $here $SidecarName
$cecilPath  = Join-Path $here 'Mono.Cecil.dll'

foreach ($p in @($asmPath, $sidecarSrc, $cecilPath)) {
    if (-not (Test-Path $p)) { throw "Required file not found: $p" }
}

# Refuse to run while the game holds the assembly open.
$proc = Get-Process -Name 'PillarsOfEternity*' -ErrorAction SilentlyContinue
if ($proc) { throw "Pillars of Eternity is running (pid $($proc.Id)). Close it and re-run." }

# 1. Install the sidecar DLL.
$sidecarDst = Join-Path $managed $SidecarName
Copy-Item -LiteralPath $sidecarSrc -Destination $sidecarDst -Force
Write-Host "Installed sidecar -> $sidecarDst" -ForegroundColor Green

# 2. Back up the original assembly once.
$backup = "$asmPath.speedhack-backup"
if (-not (Test-Path $backup)) {
    Copy-Item -LiteralPath $asmPath -Destination $backup -Force
    Write-Host "Backed up Assembly-CSharp.dll -> $backup" -ForegroundColor Green
} else {
    Write-Host "Backup already exists: $backup" -ForegroundColor DarkGray
}

# 3. Inject the hook with Mono.Cecil.
Add-Type -Path $cecilPath

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managed)

$rp = New-Object Mono.Cecil.ReaderParameters
$rp.ReadWrite = $false
$rp.InMemory  = $true
$rp.AssemblyResolver = $resolver

$module = [Mono.Cecil.ModuleDefinition]::ReadModule($asmPath, $rp)
try {
    if ($module.AssemblyReferences | Where-Object { $_.Name -eq $RefName }) {
        Write-Host "Already patched (sidecar reference present). Nothing to do." -ForegroundColor Yellow
        return
    }

    $gameState = $module.Types | Where-Object { $_.Name -eq 'GameState' } | Select-Object -First 1
    if (-not $gameState) { throw "Could not find type GameState in Assembly-CSharp.dll." }

    $update = $gameState.Methods | Where-Object {
        $_.Name -eq 'Update' -and -not $_.IsStatic -and -not $_.HasParameters -and $_.HasBody
    } | Select-Object -First 1
    if (-not $update) { throw "Could not find GameState.Update()." }

    $sidecar   = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($sidecarSrc)
    $bootstrap = $sidecar.MainModule.Types | Where-Object { $_.FullName -eq $BootstrapType } | Select-Object -First 1
    if (-not $bootstrap) { throw "Bootstrap type not found in sidecar." }
    $tick = $bootstrap.Methods | Where-Object { $_.Name -eq 'Tick' -and $_.IsStatic -and -not $_.HasParameters } | Select-Object -First 1
    if (-not $tick) { throw "Bootstrap.Tick() not found in sidecar." }

    $importedTick = $module.ImportReference($tick)
    $il    = $update.Body.GetILProcessor()
    $first = $update.Body.Instructions[0]
    $call  = $il.Create([Mono.Cecil.Cil.OpCodes]::Call, $importedTick)
    $il.InsertBefore($first, $call)

    $anr = New-Object Mono.Cecil.AssemblyNameReference($RefName, $sidecar.Name.Version)
    $module.AssemblyReferences.Add($anr)

    $tmp = "$asmPath.speedhack-patched"
    if (Test-Path $tmp) { Remove-Item -LiteralPath $tmp -Force }
    $module.Write($tmp)
    $module.Dispose()

    Copy-Item -LiteralPath $tmp -Destination $asmPath -Force
    Remove-Item -LiteralPath $tmp -Force
    Write-Host "Patched GameState.Update -> $BootstrapType.Tick()." -ForegroundColor Green
}
finally {
    if ($module) { $module.Dispose() }
}

Write-Host "`nPillars1Toolkit installed. Launch the game and press F10 in-game to open the menu." -ForegroundColor Cyan
Write-Host "To uninstall: restore '$backup' over Assembly-CSharp.dll and delete $SidecarName." -ForegroundColor DarkGray
