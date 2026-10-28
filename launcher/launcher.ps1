# mey music player - installer / repair launcher
# Installs the latest MMP.exe from GitHub. The .NET runtime is inside MMP.exe, so there is nothing else to install.
# The console text is English on purpose.
param([switch]$Run)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12   # older Windows 10 needs this for GitHub
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Repo            = 'weojiro/mey-music-player'
$InstallDir      = Join-Path $env:LOCALAPPDATA 'MMP'
$ExePath         = Join-Path $InstallDir 'MMP.exe'
$BatPath         = Join-Path $InstallDir 'MMP Launcher.bat'

# run mode: shortcuts made by launchers before v1.0.1 may still call the script with -Run
if ($Run) { Start-Process $ExePath; exit 0 }

# ================= install mode =================
$Host.UI.RawUI.WindowTitle = 'mey music player launcher'

$Banner = @'
   __  __ __  __ ____
  |  \/  |  \/  |  _ \
  | |\/| | |\/| | |_) |
  | |  | | |  | |  __/
  |_|  |_|_|  |_|_|
'@

$Art = @'
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣀⣤⣤⣤⣄⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣀⣴⣶⣶⣶⣶⣄⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣴⣾⣿⠿⠿⠿⣿⣿⣶⣄⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⣴⣾⣿⠿⠟⠛⠛⢿⣿⣦⡀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⢀⣾⣿⡟⠁⠀⠀⠀⠀⠻⢿⣿⣷⣤⣴⣶⣶⣶⣶⣶⣶⣾⣿⣿⣿⠟⠁⠀⠀⠀⠀⠈⢿⣿⣧⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⣰⣿⣿⠋⠀⠀⠀⠀⠀⠀⠀⠀⠙⡋⠀⠈⠋⠉⠉⠀⠈⡍⠉⠁⠀⠈⡀⠀⠀⠀⠀⠀⠀⠈⣿⣿⡄⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⢰⣿⡿⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢳⠀⠀⢰⠀⢡⠀⠀⢰⠀⠀⠀⠀⠃⠀⠀⠀⠀⠀⠀⠀⢸⣿⣷⠀⠀⠀⢀⣀⣀
⠀⣴⣶⣤⣀⡀⠈⠛⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠀⠀⠈⠀⠈⠄⠀⠀⠀⢠⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠙⣫⣶⣶⣿⣿⣿⢿
⠀⠙⠻⠿⣿⣿⣷⣶⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠀⠀⠀⠈⠒⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠻⠿⠛⠋⠉⠀⠀
⠀⠀⠀⠀⠀⠈⠉⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣠⣄⠀⠀⠀⠀⣀⣀⠀⢰⣿⣿⣿⣿⣿⣿
⢾⣿⣿⣿⣿⣿⣿⡆⠀⠀⠀⢠⣶⣶⣦⠀⠀⠀⠀⠀⠀⣾⣷⡀⠀⢀⣠⣾⣿⣿⣷⣦⣀⣼⣿⡿⠀⠀⠀⣾⣿⣿⡇⠀⠀⠀⠀⠀⠀⠀
⠈⠉⠉⠉⠉⠉⠉⠀⠀⠀⠀⠘⠿⠿⠟⠀⠀⠀⠀⠀⠀⢹⣿⣿⣿⣿⣿⡿⠛⠉⣿⣿⡿⠿⠛⠁⠀⠀⠀⠉⠉⠉⠁⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⣀⡀⠀⠀⣀⣤⣤⣤⡀⠀⠀⠀⠀⠀⠀⠈⣿⣿⡏⠁⠀⠀⣰⣿⣿⠃⠀⠀⠀⠀⠀⠀⠀⠀⠀⢰⣿⣿⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⢸⣿⣷⣤⣾⣿⠿⠿⣿⣿⣶⣄⠀⠀⠀⠀⠀⣿⣿⡇⠀⠀⠐⣿⣿⡏⠀⠀⠀⠀⠀⠀⠀⠀⠀⣰⣿⣿⣿⣶⣄⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠻⢿⣿⣿⡏⠀⠀⠈⠻⢿⣿⣷⣄⠀⠀⠀⣿⣿⡇⠀⠀⢰⣿⡿⠀⠀⠀⠀⣀⣀⣴⣤⣴⣾⣿⠟⠉⢻⣿⣿⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⢹⣿⣧⡀⠀⠀⠀⠈⠻⣿⣿⣷⠀⠀⣿⣿⡇⠀⣠⣿⣿⢇⣶⣶⣿⣿⣿⣿⣿⡿⠿⡋⠁⠀⢀⣾⣿⡿⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⢿⣿⣿⡀⠀⠀⠀⠀⠙⠻⠿⠀⠀⢹⣿⣇⣴⣿⣿⠋⠀⠉⠉⠉⠉⠉⠁⠈⠛⣆⠈⡆⣰⣿⣿⡟⠁⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠘⢿⣿⣿⣄⠀⠀⠀⠀⠀⠀⠀⠀⠘⢿⣿⣿⠟⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠘⣦⣿⣿⣿⠋⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢨⣿⣿⠟⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠘⣿⣿⡄⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣿⣿⠇⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢸⣿⣷⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠙⠛⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠛⠛⠀⠀⠀⠀⠀⠀
'@

# build from the registry: 22000 and above = Windows 11
$Build = 0
try { $Build = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber } catch { }

function W($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Step($n, $text) { W ''; W ("  [{0}/4] {1}" -f $n, $text) 'Cyan' }
function Ok($text) { W ("        [ OK ] {0}" -f $text) 'Green' }

# ASCII progress bar: [##########--------------------]  33%
function Draw-Bar([int]$pct) {
    $width = 30
    $filled = [int]($width * $pct / 100)
    $bar = ('#' * $filled) + ('-' * ($width - $filled))
    Write-Host -NoNewline ("`r        [{0}] {1,3}%" -f $bar, $pct) -ForegroundColor Yellow
}

function Get-FileWithBar($url, $dest) {
    $request = [System.Net.HttpWebRequest]::Create($url)
    $request.UserAgent = 'MMP-launcher'
    $response = $request.GetResponse()
    try {
        $total = $response.ContentLength
        $in = $response.GetResponseStream()
        $out = [System.IO.File]::Create($dest)
        try {
            $buffer = New-Object byte[] 65536
            $read = 0
            $shown = -1
            while (($n = $in.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $out.Write($buffer, 0, $n)
                $read += $n
                if ($total -gt 0) {
                    $pct = [int](100 * $read / $total)
                    if ($pct -ne $shown) { Draw-Bar $pct; $shown = $pct }    # redraw only when the number changes
                }
            }
        } finally { $out.Close(); $in.Close() }
    } finally { $response.Close() }
    Draw-Bar 100
    Write-Host ''
}

function Expand-Gzip($from, $to) {
    $in = [System.IO.File]::OpenRead($from)
    try {
        $gz = New-Object System.IO.Compression.GZipStream($in, [System.IO.Compression.CompressionMode]::Decompress)
        $out = [System.IO.File]::Create($to)
        try { $gz.CopyTo($out) } finally { $out.Close(); $gz.Close() }
    } finally { $in.Close() }
}

# latest MMP.exe from the newest GitHub release, checked against the published SHA-256.
# The gzipped copy is less than half the size; releases before v1.0.1 only have MMP.exe.
function Get-App {
    $base = "https://github.com/$Repo/releases/latest/download"
    $file = Join-Path $env:TEMP 'MMP.exe.download'
    $gz = Join-Path $env:TEMP 'MMP.exe.gz.download'
    try {
        try { Get-FileWithBar "$base/MMP.exe.gz" $gz; Expand-Gzip $gz $file }
        catch { Write-Host ''; Get-FileWithBar "$base/MMP.exe" $file }
        finally { Remove-Item $gz -Force -ErrorAction SilentlyContinue }
    }
    catch { throw "Could not download MMP.exe from github.com/$Repo/releases ($($_.Exception.Message)). Check your internet connection and try again." }
    $client = New-Object System.Net.WebClient
    $client.Headers.Add('User-Agent', 'MMP-launcher')
    $expected = $client.DownloadString("$base/MMP.exe.sha256").Trim().Split(' ')[0]
    if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $expected) {
        Remove-Item $file -Force -ErrorAction SilentlyContinue
        throw 'The downloaded file is damaged (SHA-256 mismatch). Please run the launcher again.'
    }
    Ok 'SHA-256 verified'
    return $file
}

function Install-App($downloaded) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Get-Process -Name 'MMP' -ErrorAction SilentlyContinue | Stop-Process -Force
    for ($i = 0; $i -lt 10; $i++) {
        try { Move-Item $downloaded $ExePath -Force; break }
        catch { if ($i -eq 9) { throw }; Start-Sleep -Milliseconds 500 }
    }
    Remove-Item "$ExePath.old", "$ExePath.new" -Force -ErrorAction SilentlyContinue

    # keep the launcher next to the app so it can be run again (repair) and used by the shortcut
    foreach ($name in 'MMP Launcher.bat', 'launcher.ps1') {
        $from = Join-Path $PSScriptRoot $name
        $to = Join-Path $InstallDir $name
        if ((Test-Path $from) -and ($from -ne $to)) { Copy-Item $from $to -Force }
    }

    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'mey music player.lnk'))
    $shortcut.TargetPath = $BatPath
    $shortcut.Arguments = '/run'
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation = "$ExePath,0"
    $shortcut.WindowStyle = 7        # minimized: the short check window stays out of the way
    $shortcut.Save()
}

# Windows' multiplane overlay (MPO) off: over a full-screen game the player and its lyrics are drawn together with
# the game, so recordings (NVIDIA with desktop capture) show what the screen shows instead of black frames.
# HKLM needs the administrator: Windows asks once. Takes effect after a restart; undo by deleting the value.
$DwmKey = 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm'
function Set-GameOverlay {
    try { if ((Get-ItemProperty $DwmKey -Name OverlayTestMode -ErrorAction Stop).OverlayTestMode -eq 5) { Ok 'Already set'; return } } catch { }
    W '        Turning off Windows multiplane overlay (MPO) so the player and its lyrics show over' 'Gray'
    W '        full-screen games and in recordings. Windows asks for permission.' 'Gray'
    try {
        $p = Start-Process reg.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden `
            -ArgumentList 'add "HKLM\SOFTWARE\Microsoft\Windows\Dwm" /v OverlayTestMode /t REG_DWORD /d 5 /f'
        if ($p.ExitCode -ne 0) { throw "reg.exe $($p.ExitCode)" }
        Ok 'Done - it takes effect after you restart the computer'
        W '        (to undo: reg delete "HKLM\SOFTWARE\Microsoft\Windows\Dwm" /v OverlayTestMode /f)' 'DarkGray'
    }
    catch { W '        [SKIP] Not changed (permission refused). MMP works the same, recordings of games may flicker.' 'Yellow' }
}

try {
    W ''
    W '  mey music player - installer' 'White'
    W '  -----------------------------------------' 'DarkGray'

    Step 1 'Downloading mey music player'
    $download = Get-App

    Step 2 'Installing'
    Install-App $download
    Ok "Installed to $InstallDir"
    Ok 'Desktop shortcut created'

    Step 3 'Setting up full-screen games'
    Set-GameOverlay

    Step 4 'Starting'
    Start-Process -FilePath $BatPath -ArgumentList '/run' -WindowStyle Minimized
    Ok 'mey music player is running'

    W ''
    W '  =============================================' 'Green'
    W '            Installation complete!' 'Green'
    W '  =============================================' 'Green'
    W ''
    # Windows 10 console fonts have no braille: the plain MMP letters there
    if ($Build -ge 22000) { Write-Host $Art -ForegroundColor Magenta }
    else { Write-Host $Banner -ForegroundColor Cyan }
}
catch {
    W ''
    W ("  ERROR: " + $_.Exception.Message) 'Red'
    W '  Something went wrong. You can run this launcher again.' 'Yellow'
}

W ''
try {
    Write-Host '  Press any key to close...' -ForegroundColor DarkGray
    [void]$Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
} catch { Start-Sleep -Seconds 5 }
