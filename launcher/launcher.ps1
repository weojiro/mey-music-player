# mey music player - installer / repair launcher
# Installs the .NET Desktop Runtime 8 (if it is missing) and the latest MMP.exe from GitHub.
# The console text is English on purpose. The "run" mode shows its message in the app's language.
param([switch]$Run)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12   # older Windows 10 needs this for GitHub
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Repo            = 'weojiro/mey-music-player'
$InstallDir      = Join-Path $env:LOCALAPPDATA 'MMP'
$ExePath         = Join-Path $InstallDir 'MMP.exe'
$BatPath         = Join-Path $InstallDir 'MMP Launcher.bat'
$ReleasesJsonUrl = 'https://raw.githubusercontent.com/dotnet/core/main/release-notes/8.0/releases.json'
$RuntimeFallback = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'

# ---- ".NET is missing" message, in every language of the app ----
$NetMissing = @{
    en = '.NET is missing. Please run MMP Launcher again.'
    tr = '.NET eksik. Lütfen MMP Launcher''ı yeniden çalıştırın.'
    es = 'Falta .NET. Ejecuta MMP Launcher de nuevo.'
    fr = '.NET est manquant. Veuillez relancer MMP Launcher.'
    de = '.NET fehlt. Bitte führen Sie MMP Launcher erneut aus.'
    pt = 'O .NET está ausente. Execute o MMP Launcher novamente.'
    ru = 'Отсутствует .NET. Запустите MMP Launcher ещё раз.'
    zh = '缺少 .NET。请重新运行 MMP Launcher。'
    ja = '.NET が見つかりません。MMP Launcher をもう一度実行してください。'
    ar = '.NET غير موجود. يُرجى تشغيل MMP Launcher مرة أخرى.'
    hi = '.NET उपलब्ध नहीं है। कृपया MMP Launcher को फिर से चलाएँ।'
}

function Test-DotNet {
    $key = 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App'
    if (Test-Path $key) {
        $versions = (Get-Item $key).Property
        if ($versions | Where-Object { $_ -like '8.*' }) { return $true }
    }
    $dir = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
    if (Test-Path $dir) {
        if (Get-ChildItem $dir -Directory -Filter '8.*' -ErrorAction SilentlyContinue) { return $true }
    }
    return $false
}

# language chosen in the app (settings.ini), otherwise the Windows display language, otherwise English
function Get-Lang {
    $ini = Join-Path $InstallDir 'settings.ini'
    if (Test-Path $ini) {
        foreach ($line in Get-Content $ini -Encoding UTF8) {
            if ($line -match '^\s*lang\s*=\s*([a-z]{2})\s*$' -and $NetMissing.ContainsKey($Matches[1])) { return $Matches[1] }
        }
    }
    $code = (Get-UICulture).TwoLetterISOLanguageName
    if ($NetMissing.ContainsKey($code)) { return $code }
    return 'en'
}

# ================= run mode: called by the shortcut when the quick check in the .bat failed =================
if ($Run) {
    if (Test-DotNet) { Start-Process $ExePath; exit 0 }
    Add-Type -AssemblyName PresentationFramework
    $lang = Get-Lang
    $options = [System.Windows.MessageBoxOptions]::None
    if ($lang -eq 'ar') { $options = [System.Windows.MessageBoxOptions]::RtlReading -bor [System.Windows.MessageBoxOptions]::RightAlign }
    [void][System.Windows.MessageBox]::Show($NetMissing[$lang], 'mey music player',
        [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning,
        [System.Windows.MessageBoxResult]::OK, $options)
    exit 0
}

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
            while (($n = $in.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $out.Write($buffer, 0, $n)
                $read += $n
                if ($total -gt 0) { Draw-Bar ([int](100 * $read / $total)) }
            }
        } finally { $out.Close(); $in.Close() }
    } finally { $response.Close() }
    Draw-Bar 100
    Write-Host ''
}

# moving bar while something runs that reports no progress (the .NET installer)
function Wait-WithBar($processId) {
    $width = 30
    $i = 0
    while (Get-Process -Id $processId -ErrorAction SilentlyContinue) {
        $pos = $i % ($width - 5)
        $bar = ('-' * $pos) + '#####' + ('-' * ($width - 5 - $pos))
        Write-Host -NoNewline ("`r        [{0}] installing..." -f $bar) -ForegroundColor Yellow
        Start-Sleep -Milliseconds 100
        $i++
    }
    Write-Host ("`r        [{0}] done          " -f ('#' * $width)) -ForegroundColor Yellow
}

# newest .NET 8 Desktop Runtime installer from Microsoft's own release list (with its SHA-512)
function Get-RuntimeInstaller {
    try {
        $text = (Invoke-WebRequest -Uri $ReleasesJsonUrl -UseBasicParsing -Headers @{ 'User-Agent' = 'MMP-launcher' }).Content
        $m = [regex]::Match($text, '"name":\s*"windowsdesktop-runtime-win-x64\.exe",\s*"rid":\s*"win-x64",\s*"url":\s*"([^"]+)",\s*"hash":\s*"([0-9a-fA-F]+)"')
        if ($m.Success) { return @{ Url = $m.Groups[1].Value; Hash = $m.Groups[2].Value } }
    } catch { }
    return @{ Url = $RuntimeFallback; Hash = $null }
}

function Install-DotNet {
    $info = Get-RuntimeInstaller
    $file = Join-Path $env:TEMP 'mmp-windowsdesktop-runtime-8.exe'
    W '        Downloading the .NET Desktop Runtime 8 from Microsoft...' 'Gray'
    Get-FileWithBar $info.Url $file
    if ($info.Hash) {
        if ((Get-FileHash $file -Algorithm SHA512).Hash -ne $info.Hash) { throw 'The downloaded .NET installer is damaged (hash mismatch).' }
    }
    $signature = Get-AuthenticodeSignature $file
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw 'The downloaded .NET installer is not signed by Microsoft.'
    }
    W '        Installing. Windows will ask for permission once...' 'Gray'
    $process = Start-Process -FilePath $file -ArgumentList '/install', '/quiet', '/norestart' -Verb RunAs -PassThru
    Wait-WithBar $process.Id
    Remove-Item $file -Force -ErrorAction SilentlyContinue
    if (-not (Test-DotNet)) {
        throw 'The .NET installation did not finish. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this launcher again.'
    }
}

# latest MMP.exe from the newest GitHub release, checked against the published SHA-256
function Get-App {
    $base = "https://github.com/$Repo/releases/latest/download"
    $file = Join-Path $env:TEMP 'MMP.exe.download'
    try { Get-FileWithBar "$base/MMP.exe" $file }
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

try {
    W $Banner 'Cyan'
    W '  mey music player - installer' 'White'
    W '  -----------------------------------------' 'DarkGray'

    Step 1 'Checking the .NET Desktop Runtime 8'
    if (Test-DotNet) { Ok '.NET is already installed' }
    else { Install-DotNet; Ok '.NET installed' }

    Step 2 'Downloading mey music player'
    $download = Get-App

    Step 3 'Installing'
    Install-App $download
    Ok "Installed to $InstallDir"
    Ok 'Desktop shortcut created'

    Step 4 'Starting'
    Start-Process -FilePath $BatPath -ArgumentList '/run' -WindowStyle Minimized
    Ok 'mey music player is running'

    W ''
    W '  =============================================' 'Green'
    W '            Installation complete!' 'Green'
    W '  =============================================' 'Green'
    W ''
    Write-Host $Art -ForegroundColor Magenta
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
