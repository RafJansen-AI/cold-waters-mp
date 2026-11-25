# Cold Waters MP setup: installs DotMod v0.4c (downloaded from DotMod's own GitHub release, hash-checked)
# and Cold Waters MP into a Cold Waters folder. Run it through Install.cmd / Uninstall.cmd.
# Windows PowerShell 5.1. Read it before you run it: it only copies files, makes one backup and writes a list
# of what it added (ColdWatersMP-installed.txt in the game folder) so Uninstall can remove exactly that.
param(
    [string]$GameDir = "",
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest/Expand-Archive are very slow with the progress bar

# ---- pinned versions ----
$DotModUrl   = 'https://github.com/DotModGroup/ColdWaters-DotMod/releases/download/v0.4c/DotMod.v0.4c.zip'
$DotModPage  = 'https://github.com/DotModGroup/ColdWaters-DotMod'
$DotModSize  = 737908615
$DotModSha   = 'EF50AE065579C5FAC826A7EFE1938AC76B2E293A93C1216C7502AF8ECDF7F8D5'
$CleanDllSha = '3BA8C01323725B8700BB0732BA348E9034F78E124ADB739D78DCC8D93F1CC2BC'   # Cold Waters 1.15g
$DotModDllSha= 'FCBED6E160370CA549ABA1752C23948CCE21198BABFF72BADB8B6384D367B561'   # DotMod v0.4c

$Here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModFiles = Join-Path $Here 'mod'
$Manifest = 'ColdWatersMP-installed.txt'
$Backup   = 'ColdWaters_Data\Managed\Assembly-CSharp.dll.cwmp-original'

function Say($t)  { Write-Host $t }
function Good($t) { Write-Host $t -ForegroundColor Green }
function Warn($t) { Write-Host $t -ForegroundColor Yellow }
function Stop-Setup($t) { Write-Host ''; Write-Host $t -ForegroundColor Red; Write-Host ''; Read-Host 'Press Enter to close'; exit 1 }
function Sha($p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToUpperInvariant() }

function Test-Game($d) { return ($d -and (Test-Path -LiteralPath (Join-Path $d 'ColdWaters.exe')) -and (Test-Path -LiteralPath (Join-Path $d 'ColdWaters_Data\Managed\Assembly-CSharp.dll'))) }

function Find-Game {
    $c = @()
    $c += Split-Path -Parent $Here     # setup unpacked inside the game folder
    $c += $Here
    $c += "${env:ProgramFiles(x86)}\GOG Galaxy\Games\Cold Waters"
    $c += "$env:ProgramFiles\GOG Galaxy\Games\Cold Waters"
    $c += "C:\GOG Games\Cold Waters"
    $c += "${env:ProgramFiles(x86)}\Steam\steamapps\common\Cold Waters"
    # Steam libraries on other drives
    $vdf = "${env:ProgramFiles(x86)}\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path -LiteralPath $vdf) {
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $c += (Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Cold Waters')
        }
    }
    foreach ($d in $c) { if (Test-Game $d) { return (Resolve-Path -LiteralPath $d).Path } }
    return $null
}

function Ask-Game {
    Add-Type -AssemblyName System.Windows.Forms
    $f = New-Object System.Windows.Forms.FolderBrowserDialog
    $f.Description = 'Select your Cold Waters folder (the one with ColdWaters.exe)'
    if ($f.ShowDialog() -eq 'OK' -and (Test-Game $f.SelectedPath)) { return $f.SelectedPath }
    return $null
}

function Test-Writable($d) {
    $t = Join-Path $d ('cwmp-write-test-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try { [IO.File]::WriteAllText($t, 'x'); Remove-Item -LiteralPath $t -Force; return $true } catch { return $false }
}

function Restart-AsAdmin($d) {
    Warn 'This folder needs administrator rights to change. Windows will ask for permission.'
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-GameDir', "`"$d`"")
    if ($Uninstall) { $a += '-Uninstall' }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $a -Verb RunAs
    exit 0
}

# Copy a folder tree into the game, recording every file in the manifest list.
function Copy-Tree($src, $dst, $list) {
    $srcFull = (Resolve-Path -LiteralPath $src).Path.TrimEnd('\')
    foreach ($f in Get-ChildItem -LiteralPath $srcFull -Recurse -Force -File) {
        $rel = $f.FullName.Substring($srcFull.Length + 1)
        $to = Join-Path $dst $rel
        $dir = Split-Path -Parent $to
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $existed = Test-Path -LiteralPath $to
        Copy-Item -LiteralPath $f.FullName -Destination $to -Force
        if (-not $existed) { [void]$list.Add($rel) }
    }
}

# ---------------------------------------------------------------------------------------------

Write-Host ''
Write-Host 'Cold Waters MP setup' -ForegroundColor Cyan
Write-Host '--------------------'
Say 'Only download this setup from https://github.com/RafJansen-AI/cold-waters-mp/releases'
Say ''

if (Get-Process -Name 'ColdWaters' -ErrorAction SilentlyContinue) { Stop-Setup 'Cold Waters is running. Close the game first, then run this again.' }

if (-not (Test-Game $GameDir)) { $GameDir = Find-Game }
if (-not $GameDir) { Say 'Cold Waters was not found in the usual places.'; $GameDir = Ask-Game }
if (-not $GameDir) { Stop-Setup 'No Cold Waters folder selected. Nothing was changed.' }
Say "Cold Waters folder: $GameDir"
if (-not (Test-Writable $GameDir)) { Restart-AsAdmin $GameDir }

$Dll = Join-Path $GameDir 'ColdWaters_Data\Managed\Assembly-CSharp.dll'
$ManifestPath = Join-Path $GameDir $Manifest

# ---------------- uninstall ----------------
if ($Uninstall) {
    if (-not (Test-Path -LiteralPath $ManifestPath)) { Stop-Setup "No $Manifest in this folder: this setup didn't install anything here. Nothing was changed." }
    $lines = @(Get-Content -LiteralPath $ManifestPath)
    Say ("Removing " + ($lines | Where-Object { $_ -and -not $_.StartsWith('#') }).Count + " files this setup added...")
    foreach ($rel in $lines) {
        if (-not $rel -or $rel.StartsWith('#') -or $rel.Contains('..')) { continue }
        $p = Join-Path $GameDir $rel
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
    }
    # BepInEx folder: if this setup created it, remove it whole (it also holds logs and cwmp.cfg made by the game).
    if ($lines -contains '#created BepInEx') {
        $bp = Join-Path $GameDir 'BepInEx'
        if (Test-Path -LiteralPath $bp) { Remove-Item -LiteralPath $bp -Recurse -Force }
    }
    # Folders that are now empty (deepest first), only inside the game folder.
    foreach ($top in @('ColdWaters_Data\StreamingAssets\dotmod', 'BepInEx')) {
        $t = Join-Path $GameDir $top
        if (Test-Path -LiteralPath $t) {
            Get-ChildItem -LiteralPath $t -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending |
                Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } | Remove-Item -Force
            if (-not (Get-ChildItem -LiteralPath $t -Force)) { Remove-Item -LiteralPath $t -Force }
        }
    }
    $b = Join-Path $GameDir $Backup
    if (Test-Path -LiteralPath $b) {
        Copy-Item -LiteralPath $b -Destination $Dll -Force
        Remove-Item -LiteralPath $b -Force
        Say 'Restored the original Assembly-CSharp.dll.'
    }
    Remove-Item -LiteralPath $ManifestPath -Force
    Good 'Uninstalled. The game is back to how it was before this setup ran.'
    Read-Host 'Press Enter to close'
    exit 0
}

# ---------------- install ----------------
if (-not (Test-Path -LiteralPath (Join-Path $ModFiles 'BepInEx\plugins\ColdWatersMP.dll'))) { Stop-Setup "The 'mod' folder is missing next to this script. Extract the whole setup zip first." }

$list = New-Object System.Collections.ArrayList
if (Test-Path -LiteralPath $ManifestPath) { foreach ($l in Get-Content -LiteralPath $ManifestPath) { if ($l) { [void]$list.Add($l) } } }

$h = Sha $Dll
if ($h -eq $DotModDllSha) {
    Good 'DotMod v0.4c is already installed. Skipping DotMod.'
}
elseif ($h -eq $CleanDllSha) {
    Say 'Clean Cold Waters 1.15g found. DotMod v0.4c will be installed first.'
    Say "DotMod is made by the DotMod team: $DotModPage"
    Say 'It is downloaded from their GitHub release (about 740 MB) and checked before anything is copied.'
    $tmp = Join-Path $env:TEMP 'ColdWatersMP-setup'
    if (-not (Test-Path -LiteralPath $tmp)) { New-Item -ItemType Directory -Path $tmp | Out-Null }
    $zip = Join-Path $tmp 'DotMod.v0.4c.zip'
    if (-not ((Test-Path -LiteralPath $zip) -and (Get-Item -LiteralPath $zip).Length -eq $DotModSize -and (Sha $zip) -eq $DotModSha)) {
        Say 'Downloading DotMod v0.4c... (this can take several minutes)'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        try { (New-Object Net.WebClient).DownloadFile($DotModUrl, $zip) }
        catch { Stop-Setup ("Download failed: " + $_.Exception.Message + "`nCheck your internet connection and run this again. Nothing was changed.") }
    }
    if ((Get-Item -LiteralPath $zip).Length -ne $DotModSize -or (Sha $zip) -ne $DotModSha) {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        Stop-Setup 'The DotMod download does not match the expected file (size or SHA-256). It was deleted and nothing was changed.'
    }
    Good 'DotMod download checked.'
    $ex = Join-Path $tmp 'dotmod'
    if (Test-Path -LiteralPath $ex) { Remove-Item -LiteralPath $ex -Recurse -Force }
    Say 'Unpacking DotMod...'
    Expand-Archive -LiteralPath $zip -DestinationPath $ex -Force
    $data = Join-Path $ex 'ColdWaters_Data'
    if ((Sha (Join-Path $data 'Managed\Assembly-CSharp.dll')) -ne $DotModDllSha) { Stop-Setup 'Unexpected DotMod contents. Nothing was changed.' }

    # Back up the one game file DotMod replaces, then copy DotMod's game data (what JSGME would copy).
    $b = Join-Path $GameDir $Backup
    if (-not (Test-Path -LiteralPath $b)) { Copy-Item -LiteralPath $Dll -Destination $b; [void]$list.Add('#backup ' + $Backup) }
    Say 'Copying DotMod files...'
    Copy-Tree $data (Join-Path $GameDir 'ColdWaters_Data') $list
    if ((Sha $Dll) -ne $DotModDllSha) { Stop-Setup 'DotMod did not install correctly (DLL check failed). Run Uninstall.cmd to undo.' }
    Good 'DotMod v0.4c installed.'
    Remove-Item -LiteralPath $ex -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    Stop-Setup ("This Cold Waters does not look like a clean 1.15g or DotMod v0.4c install (Assembly-CSharp.dll has another version).`n" +
        "Another mod or game version is installed. Restore the game (GOG: Manage installation > Verify / Repair; Steam: Verify integrity of game files), then run this again.`nNothing was changed.")
}

Say 'Installing Cold Waters MP...'
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx'))) { [void]$list.Add('#created BepInEx') }
Copy-Tree $ModFiles $GameDir $list
($list | Select-Object -Unique) | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
Good 'Cold Waters MP installed.'
Say ''
Say 'Start Cold Waters. The main menu now has a MULTIPLAYER button.'
Say 'To remove everything this setup added, run Uninstall.cmd.'
Say 'The DotMod download stays in %TEMP%\ColdWatersMP-setup; you may delete it.'
Read-Host 'Press Enter to close'
