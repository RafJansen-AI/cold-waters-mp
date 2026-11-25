# Cold Waters MP setup: installs DotMod v0.4c (downloaded from DotMod's own GitHub release, hash-checked)
# and Cold Waters MP into a Cold Waters folder. Run it through Install.cmd / Uninstall.cmd.
# Windows PowerShell 5.1. Read it before you run it: it only copies files. Every file it adds is listed in
# ColdWatersMP-installed.txt in the game folder (written while copying), and every file it replaces is first
# saved under ColdWatersMP-backup\, so Uninstall can undo exactly that.
param(
    [string]$GameDir = "",
    [switch]$Uninstall,
    [switch]$Yes,         # don't ask any question (for testing)
    [switch]$FolderOk     # the folder was already confirmed (set by the restart as administrator)
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Expand-Archive is very slow with the progress bar

# ---- pinned versions ----
$DotModUrl    = 'https://github.com/DotModGroup/ColdWaters-DotMod/releases/download/v0.4c/DotMod.v0.4c.zip'
$DotModPage   = 'https://github.com/DotModGroup/ColdWaters-DotMod'
$DotModSize   = 737908615
$DotModSha    = 'EF50AE065579C5FAC826A7EFE1938AC76B2E293A93C1216C7502AF8ECDF7F8D5'
$CleanDllSha  = '3BA8C01323725B8700BB0732BA348E9034F78E124ADB739D78DCC8D93F1CC2BC'   # Cold Waters 1.15g
$DotModDllSha = 'FCBED6E160370CA549ABA1752C23948CCE21198BABFF72BADB8B6384D367B561'   # DotMod v0.4c

$Here      = Split-Path -Parent $PSCommandPath
$ModFiles  = Join-Path $Here 'mod'
$Manifest  = 'ColdWatersMP-installed.txt'
$BackupDir = 'ColdWatersMP-backup'
$DllRel    = 'ColdWaters_Data\Managed\Assembly-CSharp.dll'
# Files the game and the mod create themselves inside BepInEx; removed on uninstall too.
$Generated = @('BepInEx\LogOutput.log', 'BepInEx\config\cwmp.cfg', 'BepInEx\cache')

function Say($t)  { Write-Host $t }
function Good($t) { Write-Host $t -ForegroundColor Green }
function Warn($t) { Write-Host $t -ForegroundColor Yellow }
function Fail($t) { throw (New-Object System.Exception $t) }
function Sha($p)  { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToUpperInvariant() }
function Done($code) { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null; exit $code }

function Test-Game($d) {
    return ($d -and (Test-Path -LiteralPath (Join-Path $d 'ColdWaters.exe')) -and (Test-Path -LiteralPath (Join-Path $d $DllRel)))
}

function Find-Game {
    $c = @()
    $c += Split-Path -Parent $Here     # setup unpacked inside the game folder
    $c += "${env:ProgramFiles(x86)}\GOG Galaxy\Games\Cold Waters"
    $c += "$env:ProgramFiles\GOG Galaxy\Games\Cold Waters"
    $c += "C:\GOG Games\Cold Waters"
    $c += "${env:ProgramFiles(x86)}\Steam\steamapps\common\Cold Waters"
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
    if ($f.ShowDialog() -eq 'OK') {
        if (Test-Game $f.SelectedPath) { return $f.SelectedPath }
        Fail "That folder has no ColdWaters.exe. Nothing was changed."
    }
    return $null
}

function Test-Writable($d) {
    $t = Join-Path $d ('cwmp-write-test-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try { [IO.File]::WriteAllText($t, 'x'); Remove-Item -LiteralPath $t -Force; return $true } catch { return $false }
}

function Free-GB($path) {
    $root = [IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $path).Path)
    return ((New-Object IO.DriveInfo $root).AvailableFreeSpace / 1GB)
}

function Log-Line($line) { Add-Content -LiteralPath $script:ManifestPath -Value $line -Encoding UTF8 }

# Copy a folder tree into $GameDir\$prefix. New files are listed as they are copied; an existing file is first
# saved to the backup folder and listed as "#replaced <hash we wrote> <path>".
function Copy-Tree($src, $prefix) {
    $srcFull = (Resolve-Path -LiteralPath $src).Path.TrimEnd('\')
    $known = @{}
    foreach ($l in Get-Content -LiteralPath $script:ManifestPath) { $known[$l] = $true }
    foreach ($f in Get-ChildItem -LiteralPath $srcFull -Recurse -Force -File) {
        $rel = $f.FullName.Substring($srcFull.Length + 1)
        if ($prefix) { $rel = Join-Path $prefix $rel }
        $to = Join-Path $GameDir $rel
        $dir = Split-Path -Parent $to
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $newHash = Sha $f.FullName
        if (Test-Path -LiteralPath $to) {
            $bak = Join-Path (Join-Path $GameDir $BackupDir) $rel
            $isOurs = $known.ContainsKey($rel) -or (@($known.Keys | Where-Object { $_ -like "#replaced * $rel" }).Count -gt 0)
            if (-not $isOurs -and -not (Test-Path -LiteralPath $bak)) {
                $bdir = Split-Path -Parent $bak
                if (-not (Test-Path -LiteralPath $bdir)) { New-Item -ItemType Directory -Path $bdir -Force | Out-Null }
                Copy-Item -LiteralPath $to -Destination $bak -Force
                $line = "#replaced $newHash $rel"; Log-Line $line; $known[$line] = $true
            }
        }
        elseif (-not $known.ContainsKey($rel)) { Log-Line $rel; $known[$rel] = $true }
        Copy-Item -LiteralPath $f.FullName -Destination $to -Force
    }
}

function Remove-EmptyDirs($top) {
    $t = Join-Path $GameDir $top
    if (-not (Test-Path -LiteralPath $t)) { return }
    Get-ChildItem -LiteralPath $t -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } | Remove-Item -Force
    if (-not (Get-ChildItem -LiteralPath $t -Force)) { Remove-Item -LiteralPath $t -Force }
}

function Do-Uninstall {
    if (-not (Test-Path -LiteralPath $script:ManifestPath)) { Fail "No $Manifest in this folder: this setup didn't install anything here. Nothing was changed." }
    $lines = @(Get-Content -LiteralPath $script:ManifestPath)
    $added = @($lines | Where-Object { $_ -and -not $_.StartsWith('#') -and -not $_.Contains('..') })
    $repl  = @($lines | Where-Object { $_ -like '#replaced *' })
    Say ("Removing " + $added.Count + " added files and restoring " + $repl.Count + " replaced files...")
    foreach ($rel in $added) {
        $p = Join-Path $GameDir $rel
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
    }
    $kept = 0
    foreach ($l in $repl) {
        $parts = $l.Split(' ', 3); $ours = $parts[1]; $rel = $parts[2]
        if ($rel.Contains('..')) { continue }
        $p = Join-Path $GameDir $rel; $bak = Join-Path (Join-Path $GameDir $BackupDir) $rel
        if (-not (Test-Path -LiteralPath $bak)) { continue }
        if ((Test-Path -LiteralPath $p) -and (Sha $p) -ne $ours) {
            Warn "Not restoring $rel : it was changed after setup (game update or another mod). The old copy stays in $BackupDir."
            $kept++; continue
        }
        Copy-Item -LiteralPath $bak -Destination $p -Force
        Remove-Item -LiteralPath $bak -Force
    }
    # What the game made inside BepInEx while the mod ran (only if this setup put BepInEx there).
    if ($added -contains 'BepInEx\core\BepInEx.dll') {
        foreach ($g in $Generated) { $p = Join-Path $GameDir $g; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force } }
    }
    Remove-EmptyDirs 'ColdWaters_Data\StreamingAssets\dotmod'
    Remove-EmptyDirs 'BepInEx'
    Remove-EmptyDirs $BackupDir
    if (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx')) { Warn 'The BepInEx folder still holds files this setup did not add (other mods?). They were left in place.' }
    if ($kept -eq 0) { Remove-Item -LiteralPath $script:ManifestPath -Force }
    Good 'Uninstalled.'
}

function Do-Install {
    if (-not (Test-Path -LiteralPath (Join-Path $ModFiles 'BepInEx\plugins\ColdWatersMP.dll'))) { Fail "The 'mod' folder is missing next to this script. Extract the whole setup zip first." }
    $dll = Join-Path $GameDir $DllRel
    $h = Sha $dll
    $needDotMod = $false
    $ml = @(); if (Test-Path -LiteralPath $script:ManifestPath) { $ml = @(Get-Content -LiteralPath $script:ManifestPath) }
    $halfDone = ($h -eq $DotModDllSha) -and ($ml -contains '#replaced ' + $DotModDllSha + ' ' + $DllRel) -and -not ($ml -contains '#dotmod complete')
    if ($halfDone) { $needDotMod = $true; Warn 'An earlier DotMod install by this setup did not finish: it will be completed now.' }
    elseif ($h -eq $DotModDllSha) { Good 'DotMod v0.4c is already installed: only Cold Waters MP will be added.' }
    elseif ($h -eq $CleanDllSha) { $needDotMod = $true; Say 'Clean Cold Waters 1.15g found: DotMod v0.4c will be installed first, then Cold Waters MP.' }
    elseif ($Yes -or ((Read-Host ("Assembly-CSharp.dll is not the GOG 1.15g or DotMod v0.4c version this setup knows.`n" +
            "A clean Steam copy may differ only in that file. DotMod replaces it, and the original is kept in $BackupDir.`n" +
            "If you installed another mod, answer N. Continue with DotMod (Y/N)")) -match '^[Yy]')) {
        $needDotMod = $true; Warn 'Unknown Assembly-CSharp.dll: it is backed up and DotMod v0.4c is installed over it.'
    }
    else {
        Fail ("This Cold Waters is not a clean 1.15g or DotMod v0.4c install (Assembly-CSharp.dll is another version, so another mod or game version is installed).`n" +
            "Restore the game (GOG: Manage installation > Verify / Repair; Steam: Verify integrity of game files), then run this again.`nNothing was changed.")
    }

    if ($needDotMod) {
        Say "DotMod is made by the DotMod team: $DotModPage"
        Say 'It is downloaded from their GitHub release (about 740 MB) and checked before anything is copied.'
        $tmp = Join-Path $env:TEMP 'ColdWatersMP-setup'
        if (-not (Test-Path -LiteralPath $tmp)) { New-Item -ItemType Directory -Path $tmp | Out-Null }
        if ((Free-GB $tmp) -lt 1.6) { Fail "Not enough free space for the download on the drive of $tmp (1.6 GB needed). Nothing was changed." }
        if ((Free-GB $GameDir) -lt 0.9) { Fail "Not enough free space in the game's drive (0.9 GB needed). Nothing was changed." }
        $zip = Join-Path $tmp 'DotMod.v0.4c.zip'
        if (-not ((Test-Path -LiteralPath $zip) -and (Get-Item -LiteralPath $zip).Length -eq $DotModSize -and (Sha $zip) -eq $DotModSha)) {
            Say 'Downloading DotMod v0.4c... (this can take several minutes)'
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            try { (New-Object Net.WebClient).DownloadFile($DotModUrl, $zip) }
            catch { Fail ("Download failed: " + $_.Exception.Message + "`nCheck your internet connection and run this again. Nothing was changed.") }
        }
        if ((Get-Item -LiteralPath $zip).Length -ne $DotModSize -or (Sha $zip) -ne $DotModSha) {
            Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
            Fail 'The DotMod download does not match the expected file (size or SHA-256). It was deleted and nothing was changed.'
        }
        Good 'DotMod download checked.'
        $ex = Join-Path $tmp 'dotmod'
        if (Test-Path -LiteralPath $ex) { Remove-Item -LiteralPath $ex -Recurse -Force }
        Say 'Unpacking DotMod...'
        Expand-Archive -LiteralPath $zip -DestinationPath $ex -Force
        $data = Join-Path $ex 'ColdWaters_Data'
        if ((Sha (Join-Path $data 'Managed\Assembly-CSharp.dll')) -ne $DotModDllSha) { Fail 'Unexpected DotMod contents. Nothing was changed.' }
        if (-not (Test-Path -LiteralPath $script:ManifestPath)) { Set-Content -LiteralPath $script:ManifestPath -Value '# Files added by Cold Waters MP setup. Uninstall.cmd removes them.' -Encoding UTF8 }
        Say 'Copying DotMod files...'
        Copy-Tree $data 'ColdWaters_Data'
        if ((Sha $dll) -ne $DotModDllSha) { Fail 'DotMod did not install correctly (DLL check failed). Run Uninstall.cmd to undo.' }
        Log-Line '#dotmod complete'
        Good 'DotMod v0.4c installed.'
        Remove-Item -LiteralPath $ex -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path -LiteralPath $script:ManifestPath)) { Set-Content -LiteralPath $script:ManifestPath -Value '# Files added by Cold Waters MP setup. Uninstall.cmd removes them.' -Encoding UTF8 }
    Say 'Installing Cold Waters MP...'
    Copy-Tree $ModFiles ''
    Good 'Cold Waters MP installed.'
    $chk = Join-Path $PSScriptRoot 'check-dotmod.ps1'
    if (Test-Path -LiteralPath $chk) {
        Say 'Checking DotMod game data against the official v0.4c files...'
        $n = & $chk -GameDir $GameDir -NoPause | Select-Object -Last 1
        if ($n -ne 0) { Warn ("Your DotMod data differs from the official v0.4c (listed above). Multiplayer refuses to connect two players whose data differs.`n" +
            "If you use JSGME, disable and re-enable DotMod in JSGME, or restore the listed files.") }
    }
    Say ''
    Say 'Start Cold Waters. The main menu now has a MULTIPLAYER button.'
    Say 'To remove everything this setup added, run Uninstall.cmd.'
    if ($needDotMod) { Say "The DotMod download stays in $env:TEMP\ColdWatersMP-setup; you may delete it." }
}

# ---------------------------------------------------------------------------------------------

try {
    Write-Host ''
    Write-Host 'Cold Waters MP setup' -ForegroundColor Cyan
    Write-Host '--------------------'
    Say 'Only download this setup from https://github.com/RafJansen-AI/cold-waters-mp/releases'
    Say ''

    if (Get-Process -Name 'ColdWaters' -ErrorAction SilentlyContinue) { Fail 'Cold Waters is running. Close the game first, then run this again.' }

    if ($GameDir) {
        if (-not (Test-Game $GameDir)) { Fail "$GameDir is not a Cold Waters folder (no ColdWaters.exe). Nothing was changed." }
        $GameDir = (Resolve-Path -LiteralPath $GameDir).Path
    }
    else {
        $GameDir = Find-Game
        if (-not $GameDir) { Say 'Cold Waters was not found in the usual places.'; $GameDir = Ask-Game }
        if (-not $GameDir) { Fail 'No Cold Waters folder selected. Nothing was changed.' }
    }
    $script:ManifestPath = Join-Path $GameDir $Manifest

    Say "Cold Waters folder: $GameDir"
    if (-not ($Yes -or $FolderOk)) {
        $what = if ($Uninstall) { 'Uninstall DotMod (if this setup added it) and Cold Waters MP from this folder?' } else { 'Install into this folder?' }
        $a = Read-Host "$what (Y/N)"
        if ($a -notmatch '^[Yy]') {
            if (-not $Uninstall) { $alt = Ask-Game; if ($alt) { $GameDir = $alt; $script:ManifestPath = Join-Path $GameDir $Manifest; Say "Cold Waters folder: $GameDir" } else { Say 'Nothing was changed.'; Done 0 } }
            else { Say 'Nothing was changed.'; Done 0 }
        }
    }

    if (-not (Test-Writable $GameDir)) {
        Warn 'This folder needs administrator rights to change. Windows will ask for permission.'
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-GameDir', "`"$GameDir`"", '-FolderOk')
        if ($Uninstall) { $a += '-Uninstall' }
        if ($Yes) { $a += '-Yes' }
        try { Start-Process -FilePath 'powershell.exe' -ArgumentList $a -Verb RunAs }
        catch { Fail 'Administrator permission was refused. Nothing was changed.' }
        Say 'Continuing in the new window.'
        exit 0
    }

    if ($Uninstall) { Do-Uninstall } else { Do-Install }
    Done 0
}
catch {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Done 1
}
